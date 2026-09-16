using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ff7ec.Server;

/// <summary>
/// Persists story-mode drama selections so later user snapshots can be overlaid with the
/// choices made against the offline server instead of restoring stale captured choices.
/// </summary>
public sealed class StoryStateStore
{
    private const int CurrentFormatVersion = 1;
    private const string StoreFileName = "story-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _storePath;
    private readonly ILogger<StoryStateStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StoryStateDocument _document;

    public StoryStateStore(ILogger<StoryStateStore> logger, string dataDirectory)
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
                    "STORY state duplicate acknowledged without append: {Host}{Endpoint} user {UserId}",
                    host, endpoint, userId);
                return false;
            }

            var metadata = TryParseMetadata(endpoint, body);
            var record = new StoryStateRecord
            {
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                Host = host,
                Endpoint = endpoint,
                PathAndQuery = pathAndQuery,
                UserId = userId,
                ContentHash = contentHash,
                BodyBase64 = bodyBase64,
                Chapter = metadata.Chapter,
                SelectionId = metadata.SelectionId,
                SelectionIndex = metadata.SelectionIndex,
                EpisodeId = metadata.EpisodeId,
                StoryModeType = metadata.StoryModeType,
            };

            var updatedDocument = new StoryStateDocument
            {
                FormatVersion = CurrentFormatVersion,
                Records = [.. _document.Records, record],
            };

            await WriteAtomicallyAsync(updatedDocument, cancellationToken);
            _document = updatedDocument;

            _logger.LogInformation(
                "STORY state persisted: {Host}{Endpoint} user {UserId} chapter {Chapter} selection {SelectionId}:{SelectionIndex}",
                host,
                endpoint,
                userId,
                metadata.Chapter,
                metadata.SelectionId,
                metadata.SelectionIndex);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<StoryStateRecord> GetSelections(string host, string userId) =>
        _document.Records
            .Where(record =>
                string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.UserId, userId, StringComparison.Ordinal) &&
                record.Endpoint == "/api/pvt/story/select/drama" &&
                record.SelectionId is not null &&
                record.SelectionIndex is not null)
            .GroupBy(record => record.SelectionId!.Value)
            .Select(group => group.Last())
            .ToArray();

    private StoryStateMetadata TryParseMetadata(string endpoint, byte[] body)
    {
        try
        {
            var decrypted = DecryptRequest(body, ClientApiKey);
            var request = ProtobufWire.Parse(decrypted);
            if (endpoint == "/api/pvt/story/select/drama")
            {
                var field = request.FirstOrDefault(candidate => candidate.Number == 352 && candidate.WireType == 2);
                if (field is null)
                    return default;

                var inner = ProtobufWire.Parse(field.Value);
                return new StoryStateMetadata
                {
                    SelectionId = GetInt64(inner, 1),
                    // Protobuf omits scalar zero values. Choice index zero is still a
                    // real selection and must replace the captured value.
                    SelectionIndex = GetInt64(inner, 2) ?? 0,
                };
            }

            if (endpoint == "/api/pvt/story/result")
            {
                var field = request.FirstOrDefault(candidate => candidate.Number == 323 && candidate.WireType == 2);
                if (field is null)
                    return default;

                var inner = ProtobufWire.Parse(field.Value);
                var episodeId = GetInt64(inner, 1);
                var storyModeType = GetInt64(inner, 2);
                return new StoryStateMetadata
                {
                    EpisodeId = episodeId,
                    StoryModeType = storyModeType,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse story-state metadata for {Endpoint}.", endpoint);
        }

        return default;
    }

    private static long? GetInt64(List<ProtoField> fields, int number)
    {
        var field = fields.FirstOrDefault(value => value.Number == number && value.WireType == 0);
        return field is null ? null : ProtobufWire.ReadInt64(field);
    }

    private static byte[] DecryptRequest(byte[] payload, byte[] key)
    {
        if (payload.Length < 32) throw new InvalidDataException("Encrypted payload is too short.");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = payload[..16];
        using var input = new MemoryStream(payload, 16, payload.Length - 16);
        using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var output = new MemoryStream();
        crypto.CopyTo(output);
        var decrypted = output.ToArray();
        using var lz4 = new MemoryStream(decrypted);
        using var decoder = K4os.Compression.LZ4.Streams.LZ4Stream.Decode(lz4);
        using var plain = new MemoryStream();
        decoder.CopyTo(plain);
        return plain.ToArray();
    }

    private static readonly byte[] ClientApiKey = Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4=");

    private StoryStateDocument Load()
    {
        if (!File.Exists(_storePath))
            return new StoryStateDocument { FormatVersion = CurrentFormatVersion, Records = [] };

        try
        {
            using var stream = File.OpenRead(_storePath);
            var document = JsonSerializer.Deserialize<StoryStateDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException($"Story state store '{_storePath}' is empty.");

            if (document.FormatVersion != CurrentFormatVersion)
                throw new InvalidDataException(
                    $"Story state store '{_storePath}' has unsupported format version {document.FormatVersion}.");

            if (document.Records is null)
                throw new InvalidDataException($"Story state store '{_storePath}' has no records array.");

            _logger.LogInformation(
                "Loaded {Count} persisted story-state records from {Path}",
                document.Records.Count, _storePath);
            return document;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Story state store '{Path}' could not be read; starting from an empty in-memory state.",
                _storePath);
            return new StoryStateDocument { FormatVersion = CurrentFormatVersion, Records = [] };
        }
    }

    private async Task WriteAtomicallyAsync(
        StoryStateDocument document,
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

    private sealed class StoryStateDocument
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("records")]
        public required List<StoryStateRecord> Records { get; init; }
    }

    public sealed class StoryStateRecord
    {
        [JsonPropertyName("receivedAtUtc")]
        public DateTimeOffset ReceivedAtUtc { get; init; }

        [JsonPropertyName("host")]
        public required string Host { get; init; }

        [JsonPropertyName("endpoint")]
        public required string Endpoint { get; init; }

        [JsonPropertyName("pathAndQuery")]
        public required string PathAndQuery { get; init; }

        [JsonPropertyName("userId")]
        public required string UserId { get; init; }

        [JsonPropertyName("contentHash")]
        public required string ContentHash { get; init; }

        [JsonPropertyName("bodyBase64")]
        public required string BodyBase64 { get; init; }

        [JsonPropertyName("chapter")]
        public int? Chapter { get; init; }

        [JsonPropertyName("selectionId")]
        public long? SelectionId { get; init; }

        [JsonPropertyName("selectionIndex")]
        public long? SelectionIndex { get; init; }

        [JsonPropertyName("episodeId")]
        public long? EpisodeId { get; init; }

        [JsonPropertyName("storyModeType")]
        public long? StoryModeType { get; init; }
    }

    private readonly struct StoryStateMetadata
    {
        public int? Chapter { get; init; }
        public long? SelectionId { get; init; }
        public long? SelectionIndex { get; init; }
        public long? EpisodeId { get; init; }
        public long? StoryModeType { get; init; }
    }
}
