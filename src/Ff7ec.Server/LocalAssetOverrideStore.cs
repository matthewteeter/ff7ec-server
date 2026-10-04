using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Ff7ec.Octo;

namespace Ff7ec.Server;

public sealed class LocalAssetOverrideStore
{
    private readonly ILogger<LocalAssetOverrideStore> _logger;
    private readonly string[] _stateFiles;
    private readonly string _stateDirectory;
    private readonly string _manifestHost;
    private readonly string _manifestPath;
    private readonly string _assetHost;

    public LocalAssetOverrideStore(
        ILogger<LocalAssetOverrideStore> logger,
        string stateDirectory,
        string manifestHost,
        string manifestPath,
        string assetHost,
        IEnumerable<string>? stateFiles = null)
    {
        _logger = logger;
        _stateDirectory = stateDirectory;
        _stateFiles = stateFiles?.ToArray() ?? [];
        _manifestHost = manifestHost;
        _manifestPath = manifestPath;
        _assetHost = assetHost;
    }

    public bool TryGetManifest(string host, string pathAndQuery, out byte[] body)
    {
        body = [];
        if (!host.Equals(_manifestHost, StringComparison.OrdinalIgnoreCase) ||
            !pathAndQuery.Equals(_manifestPath, StringComparison.Ordinal))
            return false;

        OverrideState[] states = ReadStates().ToArray();
        OverrideState? active = states.FirstOrDefault(state => state.Applied);
        if (active is null) return false;
        body = ReadFile(active.PatchedManifestPayloadPath, active.PatchedManifestPayloadSha256);

        try
        {
            var metadata = new Dictionary<string, List<ProtoField>>(StringComparer.Ordinal);
            foreach (OverrideState state in states)
            {
                if (state.Applied)
                {
                    metadata.Add(state.AssetName,
                    [
                        ProtoField.Varint(4, checked((ulong)state.ReplacementSize)),
                        ProtoField.Varint(5, state.ReplacementCrc),
                        ProtoField.LengthDelimited(10, Encoding.ASCII.GetBytes(state.ReplacementMd5))
                    ]);
                }
                else
                {
                    if (!string.IsNullOrEmpty(state.OriginalMd5))
                    {
                        metadata.Add(state.AssetName,
                        [
                            ProtoField.Varint(4, checked((ulong)state.OriginalSize)),
                            ProtoField.Varint(5, state.OriginalCrc),
                            ProtoField.LengthDelimited(10, Encoding.ASCII.GetBytes(state.OriginalMd5))
                        ]);
                    }
                    else
                    {
                        byte[] original = OctoCrypto.DecryptSecureFile(File.ReadAllBytes(state.ManifestBackupPath));
                        ProtoField item = ProtobufWire.Parse(original).Single(field =>
                            field.Number == 2 && field.WireType == 2 && GetAssetName(field) == state.AssetName);
                        metadata.Add(state.AssetName, ProtobufWire.Parse(item.Value)
                            .Where(field => field.Number is 4 or 5 or 10).ToList());
                    }
                }
            }

            // Each saved full manifest may contain another override that is now disabled.
            // Merge only size, CRC, and MD5 for every registered state, including originals.
            List<ProtoField> fields = ProtobufWire.Parse(body);
            var patched = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Count; i++)
            {
                ProtoField field = fields[i];
                if (field.Number != 2 || field.WireType != 2) continue;
                string name = GetAssetName(field);
                if (!metadata.TryGetValue(name, out List<ProtoField>? replacement)) continue;
                List<ProtoField> itemFields = ProtobufWire.Parse(field.Value);
                if (replacement.Count != 3 ||
                    !replacement.Select(value => value.Number).ToHashSet().SetEquals([4, 5, 10]))
                    throw new InvalidDataException($"Incomplete asset override metadata for {name}.");
                foreach (ProtoField value in replacement)
                {
                    int index = itemFields.FindIndex(existing => existing.Number == value.Number);
                    if (index < 0) throw new InvalidDataException($"Manifest asset {name} is missing field {value.Number}.");
                    itemFields[index] = value;
                }
                fields[i] = ProtoField.LengthDelimited(2, ProtobufWire.Encode(itemFields));
                if (!patched.Add(name)) throw new InvalidDataException($"Duplicate manifest asset {name}.");
            }
            if (!patched.SetEquals(metadata.Keys))
                throw new InvalidDataException("The manifest is missing a registered asset override.");
            body = ProtobufWire.Encode(fields);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not compose the local asset override manifest");
            throw;
        }
    }

    public bool TryGetAsset(string host, string pathAndQuery, out byte[] body)
    {
        body = [];
        if (!host.Equals(_assetHost, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (OverrideState state in ReadStates().ToArray())
        {
            if (state.Applied && pathAndQuery.Equals('/' + state.ObjectName, StringComparison.Ordinal))
            {
                body = ReadFile(state.ServedBlobPath);
                if (!Convert.ToHexString(MD5.HashData(body)).Equals(state.ReplacementMd5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Replacement blob integrity check failed for {state.AssetName}.");
                return true;
            }
        }
        return false;
    }

    private static string GetAssetName(ProtoField item) =>
        Encoding.UTF8.GetString(ProtobufWire.Parse(item.Value)
            .Single(field => field.Number == 3 && field.WireType == 2).Value);

    private IEnumerable<OverrideState> ReadStates()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string stateFile in GetStateFiles())
        {
            if (!File.Exists(stateFile)) continue;
            OverrideState state;
            try
            {
                state = JsonSerializer.Deserialize<OverrideState>(File.ReadAllText(stateFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException($"Invalid asset override state: {stateFile}");
                if (string.IsNullOrWhiteSpace(state.AssetName) || !names.Add(state.AssetName))
                    throw new InvalidDataException($"Duplicate or empty asset name in override state: {stateFile}");
                if (state.Applied && (string.IsNullOrWhiteSpace(state.ObjectName) ||
                    string.IsNullOrWhiteSpace(state.ServedBlobPath) || string.IsNullOrWhiteSpace(state.PatchedManifestPayloadPath)))
                    throw new InvalidDataException($"Incomplete applied asset override state: {stateFile}");
                if (state.Applied) ValidateMetadata(state.ReplacementMd5, state.ReplacementSize);
                if (!string.IsNullOrEmpty(state.OriginalMd5)) ValidateMetadata(state.OriginalMd5, state.OriginalSize);
                if (state.RestoreRequired)
                    throw new InvalidDataException($"Override recovery is required; run Set-Ff7ecAssetOverride.ps1 Apply or Restore: {stateFile}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read local asset override state from {Path}", stateFile);
                throw;
            }
            yield return state;
        }
    }

    private IEnumerable<string> GetStateFiles()
    {
        if (File.Exists(_stateDirectory))
            throw new IOException($"Override state directory is not a directory: {_stateDirectory}");
        string[] directories;
        try { directories = Directory.GetDirectories(_stateDirectory); }
        catch (DirectoryNotFoundException) { directories = []; }
        return directories.Order(StringComparer.Ordinal)
            .Select(directory => Path.Combine(directory, "state.json"))
            .Concat(_stateFiles).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateMetadata(string md5, int size)
    {
        if (size <= 0 || md5 is null || md5.Length != 32 || !md5.All(Uri.IsHexDigit))
            throw new InvalidDataException("Override metadata requires a positive size and a hexadecimal MD5.");
    }

    private byte[] ReadFile(string path, string? expectedSha256 = null)
    {
        try
        {
            byte[] body = File.ReadAllBytes(path);
            if (!string.IsNullOrEmpty(expectedSha256) &&
                !Convert.ToHexString(SHA256.HashData(body)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Override file integrity check failed: {path}");
            return body;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read local asset override file {Path}", path);
            throw;
        }
    }

    private sealed class OverrideState
    {
        public bool Applied { get; set; }
        public bool RestoreRequired { get; set; }
        public string AssetName { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string ServedBlobPath { get; set; } = "";
        public string PatchedManifestPayloadPath { get; set; } = "";
        public string ManifestBackupPath { get; set; } = "";
        public string PatchedManifestPayloadSha256 { get; set; } = "";
        public string OriginalMd5 { get; set; } = "";
        public int OriginalSize { get; set; }
        public uint OriginalCrc { get; set; }
        public int ReplacementSize { get; set; }
        public uint ReplacementCrc { get; set; }
        public string ReplacementMd5 { get; set; } = "";
    }
}
