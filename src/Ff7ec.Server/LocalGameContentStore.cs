using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ff7ec.Octo;

namespace Ff7ec.Server;

public sealed class LocalGameContentStore
{
    private readonly string _masterRoot;
    private readonly string _masterHost;
    private readonly string _manifestHost;
    private readonly string _assetHost;
    private readonly byte[] _manifest;
    private readonly byte[] _emptyManifest;
    private readonly byte[] _revisionResponse;
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentFile> _masterFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _languageCatalogs = new(StringComparer.OrdinalIgnoreCase);

    public int OctoVersion { get; }
    public int Revision { get; }
    public string MasterCatalogPath { get; }
    public string MasterBaseUrl => "https://" + _masterHost;
    public string OctoHost => _manifestHost;
    public string MasterVersion { get; }

    public LocalGameContentStore(
        ILogger<LocalGameContentStore> logger, string gameDirectory,
        string masterHost, string manifestHost, string assetHost,
        IReadOnlyDictionary<string, IReadOnlySet<long>>? requiredMasterIds = null)
    {
        _masterHost = masterHost;
        _manifestHost = manifestHost;
        _assetHost = assetHost;
        _masterRoot = Path.Combine(Path.GetFullPath(gameDirectory), "FF7EC_Data", "StreamingAssets", "MasterData");
        string octoRoot = Path.Combine(gameDirectory, "octo");
        string manifestRoot = Path.Combine(octoRoot, "pdb", "3001");
        var manifests = Directory.EnumerateFiles(manifestRoot, "octocacheevai", SearchOption.AllDirectories)
            .Select(path => (Path: path, Version: int.Parse(Path.GetFileName(Path.GetDirectoryName(path))
                ?? throw new InvalidDataException("An installed Octo manifest has no version directory."),
                NumberStyles.None, CultureInfo.InvariantCulture)))
            .OrderByDescending(item => item.Version).ToArray();
        if (manifests.Length == 0 || manifests[0].Version <= 0)
            throw new InvalidDataException("Standalone mode requires an installed Octo manifest.");
        if (manifests.Count(item => item.Version == manifests[0].Version) != 1)
            throw new InvalidDataException("The installed Octo manifest version is ambiguous.");
        OctoVersion = manifests[0].Version;
        _manifest = DecryptManifest(logger, File.ReadAllBytes(manifests[0].Path));
        var fields = ProtobufWire.Parse(_manifest);
        Revision = checked((int)ProtobufWire.ReadInt64(fields.Single(field => field.Number == 1)));
        if (Revision <= 0) throw new InvalidDataException("The installed Octo revision must be positive.");
        _revisionResponse = ProtobufWire.Encode([ProtoField.Varint(1, (ulong)Revision)]);
        _emptyManifest = ProtobufWire.Encode(fields.Where(field => field.Number is not (2 or 4)));
        foreach (var field in fields.Where(field => field.Number is 2 or 4))
        {
            var item = ProtobufWire.Parse(field.Value);
            string objectName = StringValue(item, 11);
            string md5 = StringValue(item, 10);
            long id = ProtobufWire.ReadInt64(item.Single(value => value.Number == 1));
            long size = ProtobufWire.ReadInt64(item.Single(value => value.Number == 4));
            if (objectName.Length == 0 || objectName.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
                md5.Length != 32 || !md5.All(Uri.IsHexDigit) || size <= 0 || id <= 0)
                throw new InvalidDataException("An installed Octo entry has invalid object metadata.");
            // Octo cache keys use A{id}/R{id}, not the CDN object name.
            string cacheKey = (field.Number == 2 ? "A" : "R") + id.ToString(CultureInfo.InvariantCulture);
            string blobPath = Path.Combine(octoRoot, "v1", "3001", (id % 10).ToString(CultureInfo.InvariantCulture),
                Convert.ToHexString(Encoding.ASCII.GetBytes(cacheKey)), md5.ToLowerInvariant());
            if (!_assets.TryAdd("/" + objectName, new Asset(
                blobPath, md5.ToLowerInvariant(), size)))
                throw new InvalidDataException($"Duplicate Octo object: {objectName}.");
        }
        var assetBuckets = Enumerable.Range(0, 10)
            .Select(bucket => Path.Combine(octoRoot, "v1", "3001", bucket.ToString(CultureInfo.InvariantCulture))).ToArray();
        if (!assetBuckets.Any(Directory.Exists))
            throw new DirectoryNotFoundException("Standalone mode requires the installed Octo asset cache.");

        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_masterRoot, "index.json")));
        string indexedCatalog = index.RootElement.GetProperty("master_data_catalog").GetString()
            ?? throw new InvalidDataException("The masterdata index has no master catalog.");
        MasterCatalogPath = SelectMasterCatalog(logger, indexedCatalog, requiredMasterIds);
        LoadCatalog(MasterCatalogPath, "tables");
        foreach (var entry in index.RootElement.EnumerateObject()
                     .Where(entry => entry.Name.StartsWith("language_catalog_", StringComparison.Ordinal)))
        {
            string path = entry.Value.GetString() ?? throw new InvalidDataException("A language catalog path is null.");
            string language = entry.Name["language_catalog_".Length..];
            if (!_languageCatalogs.TryAdd(language, path))
                throw new InvalidDataException($"Duplicate language catalog: {language}.");
            LoadCatalog(path, "files");
        }
        if (!_languageCatalogs.ContainsKey("en"))
            throw new InvalidDataException("Standalone mode requires the English masterdata catalog.");
        // A stable numeric version also works with clients that parse this header as an integer.
        MasterVersion = (BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", _languageCatalogs.Values.Order(StringComparer.Ordinal).Prepend(MasterCatalogPath)))), 0)
            & int.MaxValue).ToString(CultureInfo.InvariantCulture);
        logger.LogInformation(
            "LOCAL CONTENT ready: Octo version {Version}, revision {Revision}, {Assets} manifest objects, {Files} verified masterdata files",
            OctoVersion, Revision, _assets.Count, _masterFiles.Count);
        logger.LogInformation("LOCAL CONTENT master catalog: {Catalog}", MasterCatalogPath);
    }

    public string LanguageCatalogPath(string language)
    {
        string normalized = language.ToLowerInvariant() switch
        {
            "" or "en" or "en-us" or "english" => "en",
            "ja" or "ja-jp" or "japanese" => "ja",
            _ => language.ToLowerInvariant(),
        };
        return _languageCatalogs.TryGetValue(normalized, out var path) ? path
            : throw new InvalidDataException($"No installed masterdata catalog for language '{language}'.");
    }

    private static byte[] DecryptManifest(ILogger logger, byte[] file)
    {
        try
        {
            return OctoCrypto.DecryptSecureFile(file);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException)
        {
            logger.LogInformation("Legacy Octo cache verification failed; checking the installed Steam cache format.");
            return OctoCrypto.DecryptSecureFile(file, OctoCrypto.SteamDatabaseAppKey);
        }
    }

    public bool TryGet(string host, string path, out byte[] body, out string contentType)
    {
        body = [];
        contentType = "application/octet-stream";
        if (host.Equals(_masterHost, StringComparison.OrdinalIgnoreCase) &&
            _masterFiles.TryGetValue(path, out var master))
        {
            body = ReadVerified(master);
            return true;
        }
        if (host.Equals(_manifestHost, StringComparison.OrdinalIgnoreCase))
        {
            if (path == $"/v1/revision/{OctoVersion}")
            {
                body = _revisionResponse;
                contentType = "application/x-protobuf";
                return true;
            }
            string prefix = $"/v1/list/{OctoVersion}/";
            if (path.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(path[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int revision) &&
                revision >= 0 && revision <= Revision)
            {
                body = revision == Revision ? _emptyManifest : _manifest;
                contentType = "application/x-protobuf";
                return true;
            }
        }
        if (host.Equals(_assetHost, StringComparison.OrdinalIgnoreCase) && _assets.TryGetValue(path, out var asset))
        {
            if (!File.Exists(asset.Path))
                throw new FileNotFoundException($"The installed cache has no bundle for Octo object {path}.");
            body = File.ReadAllBytes(asset.Path);
            if (body.LongLength != asset.Size ||
                !Convert.ToHexString(MD5.HashData(body)).Equals(asset.Md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Installed bundle integrity check failed for {path}.");
            return true;
        }
        return false;
    }

    private void LoadCatalog(string path, string listName)
    {
        ContentFile catalog = AddMasterFile(path, null);
        byte[] encrypted = ReadVerified(catalog);
        using var json = JsonDocument.Parse(MasterDataCrypto.Decrypt(encrypted));
        foreach (var entry in json.RootElement.GetProperty(listName).EnumerateArray())
        {
            string entryPath = entry.GetProperty("path").GetString()
                ?? throw new InvalidDataException($"An installed catalog has a null file path: {path}.");
            ContentFile part = AddMasterFile(entryPath, entry.GetProperty("size").GetInt64());
            ReadVerified(part);
        }
    }

    private string SelectMasterCatalog(
        ILogger logger, string indexedCatalog, IReadOnlyDictionary<string, IReadOnlySet<long>>? requiredIds)
    {
        if (requiredIds is null || requiredIds.Count == 0 || IsCompatible(indexedCatalog)) return indexedCatalog;
        var compatible = Directory.EnumerateFiles(Path.Combine(_masterRoot, "catalogs"), "*.json")
            .Select(file => "/catalogs/" + Path.GetFileName(file))
            .Where(path => path != indexedCatalog).Order(StringComparer.Ordinal).Where(IsCompatible).ToArray();
        if (compatible.Length == 0)
            throw new InvalidDataException("No installed masterdata catalog contains the exported character, weapon, and special-skill IDs. Download matching game content before using standalone mode.");
        if (compatible.Length > 1)
            throw new InvalidDataException("Multiple installed masterdata catalogs match the export; compatible catalog selection is ambiguous: " + string.Join(", ", compatible));
        logger.LogInformation("The indexed master catalog does not match the export; selected verified installed catalog {Catalog}.",
            compatible[0]);
        return compatible[0];

        bool IsCompatible(string path)
        {
            using var catalog = JsonDocument.Parse(MasterDataCrypto.Decrypt(ReadVerified(CreateMasterFile(path, null))));
            var tables = catalog.RootElement.GetProperty("tables").EnumerateArray()
                .Where(entry => entry.TryGetProperty("table", out _))
                .ToDictionary(entry => entry.GetProperty("table").GetString()
                    ?? throw new InvalidDataException("An installed masterdata table has no name."), StringComparer.Ordinal);
            foreach (var (table, ids) in requiredIds)
            {
                if (!tables.TryGetValue(table, out var entry))
                {
                    logger.LogInformation("Master catalog {Catalog} has no required table {Table}.", path, table);
                    return false;
                }
                string partPath = entry.GetProperty("path").GetString()
                    ?? throw new InvalidDataException($"An installed masterdata table has no path: {table}.");
                var available = MasterTableIds.Read(ReadVerified(CreateMasterFile(partPath, entry.GetProperty("size").GetInt64())));
                long[] missing = ids.Where(id => !available.Contains(id)).Order().ToArray();
                if (missing.Length > 0)
                {
                    logger.LogInformation("Master catalog {Catalog} lacks {Count} exported IDs in {Table}: {Ids}.",
                        path, missing.Length, table, string.Join(", ", missing.Take(8)));
                    return false;
                }
            }
            return true;
        }
    }

    private ContentFile AddMasterFile(string path, long? size)
    {
        var file = CreateMasterFile(path, size);
        if (_masterFiles.TryGetValue(path, out var existing) && existing != file)
            throw new InvalidDataException($"Conflicting installed masterdata records: {path}.");
        _masterFiles[path] = file;
        return file;
    }

    private ContentFile CreateMasterFile(string path, long? size)
    {
        if (size is <= 0) throw new InvalidDataException($"Masterdata file size must be positive: {path}.");
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.Contains('\\') || path.Contains('%') ||
            path.Contains('?') || path.Contains('#') || path.Split('/').Skip(1).Any(part => part is "" or "." or "..") ||
            !(path.StartsWith("/catalogs/", StringComparison.Ordinal) ||
              path.StartsWith("/language_catalogs/", StringComparison.Ordinal) ||
              path.StartsWith("/assets/", StringComparison.Ordinal) ||
              path.StartsWith("/language_assets/", StringComparison.Ordinal)))
            throw new InvalidDataException($"Unsafe installed masterdata path: {path}.");
        string hash = Path.GetFileNameWithoutExtension(path);
        if (hash.Length != 52 || hash.Any(character => character is not (>= 'A' and <= 'Z' or >= '2' and <= '7')))
            throw new InvalidDataException($"Masterdata path is not SHA-256-addressed: {path}.");
        return new ContentFile(Path.Combine(_masterRoot, path[1..].Replace('/', Path.DirectorySeparatorChar)), hash, size);
    }

    private static byte[] ReadVerified(ContentFile file)
    {
        byte[] bytes = File.ReadAllBytes(file.Path);
        if ((file.Size.HasValue && bytes.LongLength != file.Size.Value) || Sha256Name(bytes) != file.Hash)
            throw new InvalidDataException($"Installed masterdata integrity check failed: {file.Path}.");
        return bytes;
    }

    public static string Sha256Name(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder(52);
        uint accumulator = 0;
        int bits = 0;
        foreach (byte value in SHA256.HashData(bytes))
        {
            accumulator = (accumulator << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(int)((accumulator >> bits) & 31)]);
            }
        }
        if (bits > 0) output.Append(alphabet[(int)((accumulator << (5 - bits)) & 31)]);
        return output.ToString();
    }

    private static string StringValue(List<ProtoField> fields, int number) =>
        Encoding.UTF8.GetString(fields.Single(field => field.Number == number && field.WireType == 2).Value);

    private sealed record Asset(string Path, string Md5, long Size);
    private sealed record ContentFile(string Path, string Hash, long? Size);
}
