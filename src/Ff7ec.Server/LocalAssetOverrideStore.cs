using System.Text.Json;

namespace Ff7ec.Server;

public sealed class LocalAssetOverrideStore
{
    private readonly ILogger<LocalAssetOverrideStore> _logger;
    private readonly string _stateFile;
    private readonly string _manifestHost;
    private readonly string _manifestPath;
    private readonly string _assetHost;

    public LocalAssetOverrideStore(
        ILogger<LocalAssetOverrideStore> logger,
        string stateFile,
        string manifestHost,
        string manifestPath,
        string assetHost)
    {
        _logger = logger;
        _stateFile = stateFile;
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

        return TryReadActiveFile(static state => state.PatchedManifestPayloadPath, out body);
    }

    public bool TryGetAsset(string host, string pathAndQuery, out byte[] body)
    {
        body = [];
        if (!host.Equals(_assetHost, StringComparison.OrdinalIgnoreCase)) return false;

        OverrideState? state = ReadState();
        if (state?.Applied != true || !pathAndQuery.Equals('/' + state.ObjectName, StringComparison.Ordinal))
            return false;

        return TryReadFile(state.ServedBlobPath, out body);
    }

    private bool TryReadActiveFile(Func<OverrideState, string> selectPath, out byte[] body)
    {
        body = [];
        OverrideState? state = ReadState();
        return state?.Applied == true && TryReadFile(selectPath(state), out body);
    }

    private OverrideState? ReadState()
    {
        if (!File.Exists(_stateFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<OverrideState>(File.ReadAllText(_stateFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read local asset override state from {Path}", _stateFile);
            return null;
        }
    }

    private bool TryReadFile(string path, out byte[] body)
    {
        body = [];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogError("Active local asset override file is missing: {Path}", path);
            return false;
        }

        try
        {
            body = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read local asset override file {Path}", path);
            return false;
        }
    }

    private sealed class OverrideState
    {
        public bool Applied { get; set; }
        public string ObjectName { get; set; } = "";
        public string ServedBlobPath { get; set; } = "";
        public string PatchedManifestPayloadPath { get; set; } = "";
    }
}
