using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace Ff7ec.Server;

/// <summary>
/// Records requests the ReplayStore had no captured response for, so they can be
/// reviewed later and filled in by capturing more real traffic (or hand-authored).
/// </summary>
public sealed class GapLogger
{
    private readonly string _gapsRoot;
    private readonly ILogger<GapLogger> _logger;

    public GapLogger(ILogger<GapLogger> logger, string gapsRoot)
    {
        _logger = logger;
        _gapsRoot = gapsRoot;
        Directory.CreateDirectory(_gapsRoot);
    }

    public async Task LogAsync(HttpRequest request, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'.'fffZ");
        var slug = string.Join("_", request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries)).Trim('_');
        if (slug.Length == 0) slug = "root";
        if (slug.Length > 60) slug = slug[..60];
        var baseName = $"{timestamp}_{request.Method}_{slug}";

        var record = new
        {
            host = request.Headers.Host.ToString(),
            method = request.Method,
            pathAndQuery = request.GetEncodedPathAndQuery(),
            headers = request.Headers.Select(h => new[] { h.Key, h.Value.ToString() }).ToList(),
        };

        var metaPath = Path.Combine(_gapsRoot, baseName + ".request.json");
        var bodyPath = Path.Combine(_gapsRoot, baseName + ".body.bin");

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        if (body.Length > 0)
            await File.WriteAllBytesAsync(bodyPath, body);

        _logger.LogWarning(
            "GAP (no captured response): {Method} {Host}{Path} -> logged to {File}",
            request.Method, request.Headers.Host, request.GetEncodedPathAndQuery(), metaPath);
    }
}
