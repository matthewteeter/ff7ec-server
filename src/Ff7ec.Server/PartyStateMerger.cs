using System.Globalization;
using System.Security.Cryptography;
using K4os.Compression.LZ4.Streams;

namespace Ff7ec.Server;

public sealed class PartyStateMerger
{
    private const int ApiRequestMultiField = 319;
    private const int ApiRequestSoloField = 321;
    private const int ApiRequestHomeBackgroundSettingField = 526;
    private const int ApiResponseStorePurchaseRestartField = 2001;
    private const int UserPartyMemberTable = 17062056;
    private const int UserHomeBackgroundSettingTable = 242346576;
    private const int UserPartyTable = 312005933;

    private static readonly byte[] ClientApiKey = Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4=");
    private static readonly byte[] ServerApiKey = Convert.FromBase64String("CMMnsenXvr7izAFborJvCZHwFrG40sykNgUSqgJ99+A=");

    private readonly PartySettingsStore _store;
    private readonly ILogger<PartyStateMerger> _logger;

    public PartyStateMerger(PartySettingsStore store, ILogger<PartyStateMerger> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Creates the party-upsert response patch that the live API would return in
    /// CommonResponse.User.Update. Applying this patch, including an entity timestamp
    /// consistent with the frozen replay clock, lets the client refresh its in-memory
    /// party cache immediately instead of waiting for the next title load.
    /// </summary>
    public byte[]? CreateWriteResponse(
        string endpoint,
        string userId,
        byte[] requestBody,
        byte[] responseTemplateBody,
        IHeaderDictionary responseHeaders)
    {
        try
        {
            var replayTime = GetReplayTime(responseHeaders);
            var state = new SavedState();
            ReadRequest(state, endpoint, requestBody, userId, replayTime);
            if (state.Members.Count == 0 &&
                state.Parties.Count == 0 &&
                state.HomeBackgroundSettings.Count == 0)
                return null;

            var tables = new List<ProtoField>();
            tables.AddRange(state.Parties.Select(bytes => ProtoField.LengthDelimited(UserPartyTable, bytes)));
            tables.AddRange(state.Members.Select(bytes => ProtoField.LengthDelimited(UserPartyMemberTable, bytes)));
            tables.AddRange(state.HomeBackgroundSettings.Select(bytes =>
                ProtoField.LengthDelimited(UserHomeBackgroundSettingTable, bytes)));

            // Preserve the complete captured success envelope. Some endpoint handlers expect
            // User.delete and User.other_info to be present even when empty; synthesizing only
            // User.update leaves the wallpaper request waiting forever in the client.
            var templatePlain = Decompress(Decrypt(responseTemplateBody, ServerApiKey));
            var root = ProtobufWire.Parse(templatePlain);
            var commonIndex = root.FindIndex(field => field.Number == 101 && field.WireType == 2);
            if (commonIndex < 0)
                throw new InvalidDataException("Secure response template has no CommonResponse.");

            var common = ProtobufWire.Parse(root[commonIndex].Value);
            var userIndex = common.FindIndex(field => field.Number == 1 && field.WireType == 2);
            if (userIndex < 0)
                throw new InvalidDataException("Secure response template has no User response.");

            var user = ProtobufWire.Parse(common[userIndex].Value);
            user.RemoveAll(field => field.Number == 1);
            user.Insert(0, ProtoField.LengthDelimited(1, ProtobufWire.Encode(tables)));
            common[userIndex] = ProtoField.LengthDelimited(1, ProtobufWire.Encode(user));
            root[commonIndex] = ProtoField.LengthDelimited(101, ProtobufWire.Encode(common));

            var responseField = GetRequestField(endpoint);
            root.RemoveAll(field => field.Number == ApiResponseStorePurchaseRestartField);
            root.RemoveAll(field => field.Number == responseField);
            root.Add(ProtoField.LengthDelimited(responseField, []));

            return EncodeResponse(ProtobufWire.Encode(root), responseHeaders);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create settings cache update response for user {UserId}.", userId);
            return null;
        }
    }

    /// <summary>
    /// Applies the local party overlay only to replay responses that contain the
    /// account's existing party tables. Other boot-time responses use the same API
    /// envelope but do not carry the full user snapshot and must remain unchanged.
    /// </summary>
    public byte[]? MergeReplayResponse(
        HttpRequest request,
        byte[] capturedBody,
        IHeaderDictionary responseHeaders)
    {
        var userId = request.Query["user_id"].ToString();
        if (string.IsNullOrWhiteSpace(userId)) return null;
        return MergeIntoResponse(request.Headers.Host.ToString(), userId, capturedBody, responseHeaders);
    }

    private byte[]? MergeIntoResponse(
        string host,
        string userId,
        byte[] capturedBody,
        IHeaderDictionary responseHeaders)
    {
        try
        {
            var records = _store.GetRecords(host, userId);
            if (records.Count == 0) return null;

            var compressed = Decrypt(capturedBody, ServerApiKey);
            var plain = Decompress(compressed);
            var root = ProtobufWire.Parse(plain);
            if (root.Count == 0) return null;

            if (!TryFindMessage(root, 101, out var commonBytes)) return null;
            var common = ProtobufWire.Parse(commonBytes);
            if (!TryFindMessage(common, 1, out var userBytes)) return null;
            var user = ProtobufWire.Parse(userBytes);
            var tablesIndex = user.FindIndex(field => field.Number == 1 && field.WireType == 2);
            if (tablesIndex < 0) return null;

            var tables = ProtobufWire.Parse(user[tablesIndex].Value);
            if (!tables.Any(field =>
                    field.WireType == 2 &&
                    (field.Number == UserPartyTable ||
                     field.Number == UserPartyMemberTable ||
                     field.Number == UserHomeBackgroundSettingTable)))
                return null;

            var replayTime = GetReplayTime(responseHeaders);
            var saved = ReadSavedState(records, userId, replayTime);
            if (saved.Members.Count == 0 &&
                saved.Parties.Count == 0 &&
                saved.HomeBackgroundSettings.Count == 0)
                return null;

            tables = MergeTable(tables, UserPartyTable, saved.Parties, keyField: 2);
            tables = MergeTable(tables, UserPartyMemberTable, saved.Members, keyField: 2);
            tables = MergeTable(
                tables,
                UserHomeBackgroundSettingTable,
                saved.HomeBackgroundSettings,
                keyField: 1);
            user[tablesIndex] = ProtoField.LengthDelimited(1, ProtobufWire.Encode(tables));
            common[common.FindIndex(field => field.Number == 1 && field.WireType == 2)] =
                ProtoField.LengthDelimited(1, ProtobufWire.Encode(user));
            root[root.FindIndex(field => field.Number == 101 && field.WireType == 2)] =
                ProtoField.LengthDelimited(101, ProtobufWire.Encode(common));

            return EncodeResponse(ProtobufWire.Encode(root), responseHeaders);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not merge persisted party settings into replayed response for user {UserId}.", userId);
            return null;
        }
    }

    private SavedState ReadSavedState(
        IReadOnlyList<PartySettingsStore.PartySettingsRecord> records,
        string userId,
        long replayTime)
    {
        var state = new SavedState();
        foreach (var record in records)
        {
            try
            {
                ReadRequest(
                    state,
                    record.Endpoint,
                    Convert.FromBase64String(record.BodyBase64),
                    userId,
                    Math.Min(record.ReceivedAtUtc.ToUnixTimeMilliseconds(), replayTime));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring an unreadable persisted party write from {Endpoint}.", record.Endpoint);
            }
        }
        return state;
    }

    private static void ReadRequest(
        SavedState state,
        string endpoint,
        byte[] body,
        string userId,
        long updatedDatetime)
    {
        var compressed = Decrypt(body, ClientApiKey);
        var request = ProtobufWire.Parse(Decompress(compressed));
        var fieldNumber = GetRequestField(endpoint);
        var requestField = request.FirstOrDefault(field => field.Number == fieldNumber && field.WireType == 2);
        if (requestField is null)
            throw new InvalidDataException($"Settings write has no protobuf field {fieldNumber}.");

        var fields = ProtobufWire.Parse(requestField.Value);
        switch (fieldNumber)
        {
            case ApiRequestSoloField:
                ReadSolo(state, fields, userId, updatedDatetime);
                break;
            case ApiRequestMultiField:
                ReadMulti(state, fields, userId, updatedDatetime);
                break;
            case ApiRequestHomeBackgroundSettingField:
                ReadHomeBackgroundSetting(state, fields, userId);
                break;
        }
    }

    private static int GetRequestField(string endpoint) => endpoint switch
    {
        "/api/pvt/party/multi/set/upsert" => ApiRequestMultiField,
        "/api/pvt/party/solo/set/upsert" => ApiRequestSoloField,
        "/api/pvt/user/home/background/setting" => ApiRequestHomeBackgroundSettingField,
        _ => throw new InvalidDataException($"Unsupported writable settings endpoint '{endpoint}'."),
    };

    private static void ReadHomeBackgroundSetting(
        SavedState state,
        List<ProtoField> fields,
        string userId)
    {
        var setting = new List<ProtoField>
        {
            ProtoField.Varint(1, ulong.Parse(userId)),
        };

        for (var sourceNumber = 1; sourceNumber <= 5; sourceNumber++)
            CopyAs(fields, setting, sourceNumber, sourceNumber + 1);

        state.HomeBackgroundSettings.Add(ProtobufWire.Encode(setting));
    }

    private static void ReadSolo(
        SavedState state,
        List<ProtoField> fields,
        string userId,
        long updatedDatetime)
    {
        var partyId = GetInt64(fields, 1);
        if (partyId is null) return;
        var party = new List<ProtoField>
        {
            ProtoField.Varint(1, ulong.Parse(userId)),
            ProtoField.Varint(2, (ulong)partyId.Value),
        };
        CopyAs(fields, party, sourceNumber: 2, targetNumber: 3);
        CopyAs(fields, party, sourceNumber: 4, targetNumber: 4);
        CopyAs(fields, party, sourceNumber: 3, targetNumber: 6);
        state.Parties.Add(ProtobufWire.Encode(party));
        foreach (var member in fields.Where(field => field.Number == 5 && field.WireType == 2))
        {
            var memberFields = ProtobufWire.Parse(member.Value);
            EnsureVarint(memberFields, 1, ulong.Parse(userId));
            EnsureVarint(memberFields, 22, (ulong)updatedDatetime);
            state.Members.Add(ProtobufWire.Encode(memberFields));
        }
    }

    private static void ReadMulti(
        SavedState state,
        List<ProtoField> fields,
        string userId,
        long updatedDatetime)
    {
        foreach (var wrapper in fields.Where(field => field.Number == 1 && field.WireType == 2))
        {
            var memberWrapper = ProtobufWire.Parse(wrapper.Value);
            var member = memberWrapper.FirstOrDefault(field => field.Number == 1 && field.WireType == 2);
            if (member is null) continue;
            var memberFields = ProtobufWire.Parse(member.Value);
            EnsureVarint(memberFields, 1, ulong.Parse(userId));
            EnsureVarint(memberFields, 22, (ulong)updatedDatetime);
            state.Members.Add(ProtobufWire.Encode(memberFields));
        }
    }

    private static List<ProtoField> MergeTable(List<ProtoField> tables, int tableNumber, List<byte[]> replacements, int keyField)
    {
        if (replacements.Count == 0) return tables;
        var latestByKey = replacements
            .Select(bytes => (Bytes: bytes, Key: GetInt64(ProtobufWire.Parse(bytes), keyField)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!.Value)
            .Select(group => group.Last().Bytes)
            .ToList();
        var keys = latestByKey.Select(bytes => GetInt64(ProtobufWire.Parse(bytes), keyField)!.Value).ToHashSet();
        var result = tables.Where(field =>
        {
            if (field.Number != tableNumber || field.WireType != 2) return true;
            var key = GetInt64(ProtobufWire.Parse(field.Value), keyField);
            return key is null || !keys.Contains(key.Value);
        }).ToList();
        result.AddRange(latestByKey.Select(bytes => ProtoField.LengthDelimited(tableNumber, bytes)));
        return result;
    }

    private static bool TryFindMessage(List<ProtoField> fields, int number, out byte[] value)
    {
        var field = fields.FirstOrDefault(candidate => candidate.Number == number && candidate.WireType == 2);
        value = field?.Value ?? [];
        return field is not null;
    }

    private static long? GetInt64(List<ProtoField> fields, int number)
    {
        var field = fields.FirstOrDefault(value => value.Number == number && value.WireType == 0);
        return field is null ? null : ProtobufWire.ReadInt64(field);
    }

    private static void CopyAs(List<ProtoField> source, List<ProtoField> target, int sourceNumber, int targetNumber)
    {
        var field = source.FirstOrDefault(value => value.Number == sourceNumber);
        if (field is not null)
            target.Add(field with { Number = targetNumber });
    }

    private static void EnsureVarint(List<ProtoField> fields, int number, ulong value)
    {
        fields.RemoveAll(field => field.Number == number);
        fields.Insert(0, ProtoField.Varint(number, value));
    }

    private static long GetReplayTime(IHeaderDictionary responseHeaders)
    {
        var value = responseHeaders["X-Server-Time"].ToString();
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var replayTime))
            throw new InvalidDataException("Secure response template has no valid X-Server-Time header.");
        return replayTime;
    }

    private static byte[] EncodeResponse(byte[] plain, IHeaderDictionary responseHeaders)
    {
        var compressed = Compress(plain);
        responseHeaders["X-Content-Hash"] = ToUrlSafeBase64(SHA256.HashData(compressed));
        return Encrypt(compressed, ServerApiKey);
    }

    private static byte[] Decrypt(byte[] payload, byte[] key)
    {
        if (payload.Length < 32) throw new InvalidDataException("Encrypted payload is too short.");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = payload[..16];
        using var input = new MemoryStream(payload, 16, payload.Length - 16);
        using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var output = new MemoryStream();
        crypto.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] payload, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var output = new MemoryStream();
        output.Write(aes.IV);
        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            crypto.Write(payload);
            crypto.FlushFinalBlock();
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var decoder = LZ4Stream.Decode(input);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var encoder = LZ4Stream.Encode(output))
            encoder.Write(bytes);
        return output.ToArray();
    }

    private static string ToUrlSafeBase64(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    private sealed class SavedState
    {
        public List<byte[]> Parties { get; } = [];
        public List<byte[]> Members { get; } = [];
        public List<byte[]> HomeBackgroundSettings { get; } = [];
    }
}
