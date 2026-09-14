using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ff7ec.Server;

/// <summary>
/// Durably records supported opaque settings writes without attempting to decrypt or
/// interpret their protobuf payloads.
/// </summary>
public sealed class PartySettingsStore
{
    private const int CurrentFormatVersion = 1;
    private const string StoreFileName = "party-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _storePath;
    private readonly ILogger<PartySettingsStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private PartySettingsDocument _document;

    public PartySettingsStore(ILogger<PartySettingsStore> logger, string dataDirectory)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, StoreFileName);
        _document = Load();
    }

    public async Task<bool> AppendAsync(
        string host,
        string endpoint,
        string pathAndQuery,
        string userId,
        string contentHash,
        string? contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var bodyBase64 = Convert.ToBase64String(body);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var latest = _document.Records.LastOrDefault(record =>
                string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Endpoint, endpoint, StringComparison.Ordinal) &&
                string.Equals(record.UserId, userId, StringComparison.Ordinal));

            if (latest is not null &&
                string.Equals(latest.ContentHash, contentHash, StringComparison.Ordinal) &&
                string.Equals(latest.BodyBase64, bodyBase64, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "SETTINGS WRITE duplicate acknowledged without append: {Host}{Endpoint} user {UserId}",
                    host, endpoint, userId);
                return false;
            }

            var record = new PartySettingsRecord
            {
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                Host = host,
                Method = HttpMethods.Post,
                Endpoint = endpoint,
                PathAndQuery = pathAndQuery,
                UserId = userId,
                ContentHash = contentHash,
                ContentType = contentType,
                BodyBase64 = bodyBase64,
            };

            var updatedDocument = new PartySettingsDocument
            {
                FormatVersion = CurrentFormatVersion,
                Records = [.. _document.Records, record],
            };

            await WriteAtomicallyAsync(updatedDocument, cancellationToken);
            _document = updatedDocument;

            _logger.LogInformation(
                "SETTINGS WRITE persisted: {Host}{Endpoint} user {UserId} ({Bytes} bytes)",
                host, endpoint, userId, body.Length);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private PartySettingsDocument Load()
    {
        if (!File.Exists(_storePath))
            return new PartySettingsDocument { FormatVersion = CurrentFormatVersion, Records = [] };

        try
        {
            using var stream = File.OpenRead(_storePath);
            var document = JsonSerializer.Deserialize<PartySettingsDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException($"Party settings store '{_storePath}' is empty.");

            if (document.FormatVersion != CurrentFormatVersion)
                throw new InvalidDataException(
                    $"Party settings store '{_storePath}' has unsupported format version {document.FormatVersion}.");

            if (document.Records is null)
                throw new InvalidDataException($"Party settings store '{_storePath}' has no records array.");

            _logger.LogInformation(
                "Loaded {Count} persisted settings writes from {Path}",
                document.Records.Count, _storePath);
            return document;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Party settings store '{Path}' could not be read; starting from an empty in-memory state.",
                _storePath);
            return new PartySettingsDocument { FormatVersion = CurrentFormatVersion, Records = [] };
        }
    }

    private async Task WriteAtomicallyAsync(
        PartySettingsDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        var tempPath = Path.Combine(directory, $".{StoreFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public IReadOnlyList<PartySettingsRecord> GetRecords(string host, string userId) =>
        _document.Records
            .Where(record =>
                string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.UserId, userId, StringComparison.Ordinal))
            .ToArray();

    private sealed class PartySettingsDocument
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("records")]
        public required List<PartySettingsRecord> Records { get; init; }
    }

    public sealed class PartySettingsRecord
    {
        [JsonPropertyName("receivedAtUtc")]
        public DateTimeOffset ReceivedAtUtc { get; init; }

        [JsonPropertyName("host")]
        public required string Host { get; init; }

        [JsonPropertyName("method")]
        public required string Method { get; init; }

        [JsonPropertyName("endpoint")]
        public required string Endpoint { get; init; }

        [JsonPropertyName("pathAndQuery")]
        public required string PathAndQuery { get; init; }

        [JsonPropertyName("userId")]
        public required string UserId { get; init; }

        [JsonPropertyName("contentHash")]
        public required string ContentHash { get; init; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        [JsonPropertyName("bodyBase64")]
        public required string BodyBase64 { get; init; }
    }
}

