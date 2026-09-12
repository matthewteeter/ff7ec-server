using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ff7ec.Server;

/// <summary>One header captured from either side of a real request/response pair.</summary>
public sealed record CapturedHeader(string Name, string Value);

/// <summary>A single stored (request-key -> response) capture entry, loaded from disk.</summary>
public sealed class CapturedResponse
{
    public required string Host { get; init; }
    public required string Method { get; init; }
    public required string PathAndQuery { get; init; }
    public required int StatusCode { get; init; }
    public required List<CapturedHeader> ResponseHeaders { get; init; }
    public required byte[] Body { get; init; }
    public required string SourceFile { get; init; }
}

/// <summary>
/// Loads every captured request/response pair exported by tools/export_capture_store.py
/// into memory and serves lookups keyed by (host, method, path+query). This is the heart
/// of "replay mode": the emulator does not understand or decrypt the game's payloads, it
/// just plays back exactly what the real server once returned for the same call, headers
/// included, so any client-side integrity check over the (body, headers) pair still
/// passes trivially - nothing was tampered with, it's the same bytes the client already
/// saw once during capture.
/// </summary>
public sealed class ReplayStore
{
    private readonly Dictionary<(string Host, string Method, string PathAndQuery), CapturedResponse> _byKey;
    private readonly ILogger<ReplayStore> _logger;

    public int Count => _byKey.Count;

    public ReplayStore(ILogger<ReplayStore> logger, string capturesRoot)
    {
        _logger = logger;
        _byKey = Load(capturesRoot, logger);
    }

    public bool TryGet(string host, string method, string pathAndQuery, out CapturedResponse response) =>
        _byKey.TryGetValue((host, method.ToUpperInvariant(), pathAndQuery), out response!);

    private static Dictionary<(string, string, string), CapturedResponse> Load(string capturesRoot, ILogger logger)
    {
        var result = new Dictionary<(string, string, string), CapturedResponse>();
        if (!Directory.Exists(capturesRoot))
        {
            logger.LogWarning("Captures directory {Dir} does not exist - no responses will be served.", capturesRoot);
            return result;
        }

        var metaFiles = Directory.EnumerateFiles(capturesRoot, "*.meta.json", SearchOption.AllDirectories);
        foreach (var metaPath in metaFiles)
        {
            try
            {
                var bodyPath = metaPath[..^".meta.json".Length] + ".body.bin";
                using var stream = File.OpenRead(metaPath);
                var meta = JsonSerializer.Deserialize<CaptureMeta>(stream)
                    ?? throw new InvalidDataException("Empty meta file");

                var body = File.Exists(bodyPath) ? File.ReadAllBytes(bodyPath) : [];
                var headers = (meta.ResponseHeaders ?? [])
                    .Select(pair => new CapturedHeader(pair[0], pair[1]))
                    .ToList();

                var entry = new CapturedResponse
                {
                    Host = meta.Host,
                    Method = meta.Method.ToUpperInvariant(),
                    PathAndQuery = meta.PathAndQuery,
                    StatusCode = meta.StatusCode,
                    ResponseHeaders = headers,
                    Body = body,
                    SourceFile = metaPath,
                };

                result[(entry.Host, entry.Method, entry.PathAndQuery)] = entry;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load capture {Path}", metaPath);
            }
        }

        logger.LogInformation("Loaded {Count} captured responses from {Dir}", result.Count, capturesRoot);
        return result;
    }

    private sealed class CaptureMeta
    {
        [JsonPropertyName("host")] public required string Host { get; set; }
        [JsonPropertyName("method")] public required string Method { get; set; }
        [JsonPropertyName("pathAndQuery")] public required string PathAndQuery { get; set; }
        [JsonPropertyName("statusCode")] public required int StatusCode { get; set; }
        [JsonPropertyName("responseHeaders")] public List<string[]>? ResponseHeaders { get; set; }
    }
}
