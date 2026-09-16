using System.Globalization;
using System.Security.Cryptography;
using K4os.Compression.LZ4.Streams;

namespace Ff7ec.Server;

public sealed class PartyStateMerger
{
    private const int ApiRequestMultiField = 319;
    private const int ApiRequestSoloField = 321;
    private const int ApiStoryResultField = 323;
    private const int ApiDungeonStoryEndField = 330;
    private const int ApiDungeonStoryStartField = 329;
    private const int ApiStoryBattleStartField = 335;
    private const int ApiStoryBattleEndField = 336;
    private const int ApiStorySelectDramaField = 352;
    private const int ApiEventSoloBattleStartField = 469;
    private const int ApiEventSoloBattleEndField = 470;
    private const int ApiRequestHomeBackgroundSettingField = 526;
    private const int ApiResponseStorePurchaseRestartField = 2001;
    private const int UserPartyMemberTable = 17062056;
    private const int UserStoryDramaSelectionTable = 78231314;
    private const int UserHomeBackgroundSettingTable = 242346576;
    private const int UserPartyTable = 312005933;

    private static readonly byte[] ClientApiKey = Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4=");
    private static readonly byte[] ServerApiKey = Convert.FromBase64String("CMMnsenXvr7izAFborJvCZHwFrG40sykNgUSqgJ99+A=");

    private readonly PartySettingsStore _store;
    private readonly StoryStateStore _storyStore;
    private readonly ILogger<PartyStateMerger> _logger;

    public PartyStateMerger(
        PartySettingsStore store,
        StoryStateStore storyStore,
        ILogger<PartyStateMerger> logger)
    {
        _store = store;
        _storyStore = storyStore;
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

            return CreateResponse(
                responseTemplateBody,
                responseHeaders,
                GetRequestField(endpoint),
                responseValue: [],
                tables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create settings cache update response for user {UserId}.", userId);
            return null;
        }
    }

    public byte[]? CreateEmptyWriteResponse(
        string endpoint,
        string userId,
        byte[] requestBody,
        byte[] responseTemplateBody,
        IHeaderDictionary responseHeaders)
    {
        try
        {
            var responseField = GetEmptyWriteField(endpoint);
            var request = ProtobufWire.Parse(Decompress(Decrypt(requestBody, ClientApiKey)));
            var requestField = request.FirstOrDefault(
                field => field.Number == responseField && field.WireType == 2);
            if (requestField is null)
                throw new InvalidDataException(
                    $"Write to {endpoint} has no protobuf field {responseField}.");

            List<ProtoField>? updateTables = null;
            if (endpoint == "/api/pvt/story/select/drama")
            {
                var selection = ProtobufWire.Parse(requestField.Value);
                var selectionId = GetInt64(selection, 1)
                    ?? throw new InvalidDataException("Drama selection has no selection ID.");
                var selectionIndex = GetInt64(selection, 2) ?? 0;
                var row = ProtobufWire.Encode([
                    ProtoField.Varint(1, ulong.Parse(userId)),
                    ProtoField.Varint(2, (ulong)selectionId),
                    ProtoField.Varint(3, (ulong)selectionIndex),
                ]);
                updateTables = [ProtoField.LengthDelimited(UserStoryDramaSelectionTable, row)];
            }

            return CreateResponse(
                responseTemplateBody,
                responseHeaders,
                responseField,
                GetWriteResponseValue(endpoint, requestField.Value),
                updateTables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not create empty write response for {Endpoint}, user {UserId}.",
                endpoint,
                userId);
            return null;
        }
    }

    /// <summary>
    /// Applies persisted party, wallpaper, and story-choice overlays to replay responses
    /// that contain the corresponding user tables. Other responses remain unchanged.
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
            var storySelections = _storyStore.GetSelections(host, userId);
            if (records.Count == 0 && storySelections.Count == 0) return null;

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
                     field.Number == UserHomeBackgroundSettingTable ||
                     field.Number == UserStoryDramaSelectionTable)))
                return null;

            var replayTime = GetReplayTime(responseHeaders);
            var saved = ReadSavedState(records, userId, replayTime);
            foreach (var selection in storySelections)
            {
                saved.StoryDramaSelections.Add(ProtobufWire.Encode([
                    ProtoField.Varint(1, ulong.Parse(userId)),
                    ProtoField.Varint(2, (ulong)selection.SelectionId!.Value),
                    ProtoField.Varint(3, (ulong)selection.SelectionIndex!.Value),
                ]));
            }

            if (saved.Members.Count == 0 &&
                saved.Parties.Count == 0 &&
                saved.HomeBackgroundSettings.Count == 0 &&
                saved.StoryDramaSelections.Count == 0)
                return null;

            tables = MergeTable(tables, UserPartyTable, saved.Parties, keyField: 2);
            tables = MergeTable(tables, UserPartyMemberTable, saved.Members, keyField: 2);
            tables = MergeTable(
                tables,
                UserHomeBackgroundSettingTable,
                saved.HomeBackgroundSettings,
                keyField: 1);
            tables = MergeTable(
                tables,
                UserStoryDramaSelectionTable,
                saved.StoryDramaSelections,
                keyField: 2);
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

    private static int GetEmptyWriteField(string endpoint) => endpoint switch
    {
        "/api/pvt/dungeon/story/end" => ApiDungeonStoryEndField,
        "/api/pvt/dungeon/story/start" => ApiDungeonStoryStartField,
        "/api/pvt/event/solo/battle/end" => ApiEventSoloBattleEndField,
        "/api/pvt/event/solo/battle/start" => ApiEventSoloBattleStartField,
        "/api/pvt/story/battle/end" => ApiStoryBattleEndField,
        "/api/pvt/story/battle/start" => ApiStoryBattleStartField,
        "/api/pvt/story/result" => ApiStoryResultField,
        "/api/pvt/story/select/drama" => ApiStorySelectDramaField,
        _ => throw new InvalidDataException($"Unsupported empty-response write endpoint '{endpoint}'."),
    };

    private static byte[] GetWriteResponseValue(string endpoint, byte[] requestValue) => endpoint switch
    {
        // The client constructs its result model from BattleResult. Omitting this
        // nested message leaves the post-battle result screen waiting indefinitely.
        "/api/pvt/story/battle/end" => ProtobufWire.Encode([
            ProtoField.LengthDelimited(1, []),
        ]),
        "/api/pvt/event/solo/battle/end" => CreateEventSoloBattleEndResponse(requestValue),
        _ => [],
    };

    private static byte[] CreateEventSoloBattleEndResponse(byte[] requestValue)
    {
        var request = ProtobufWire.Parse(requestValue);
        var resultType = GetInt64(request, 3) ?? 0;
        var rank = resultType == 1 ? 7UL : 1UL;
        var scoreResult = ProtobufWire.Encode([
            ProtoField.Varint(8, rank),
            ProtoField.Varint(9, rank),
            ProtoField.Varint(10, rank),
            ProtoField.Varint(11, rank),
            ProtoField.Varint(12, rank),
        ]);

        var response = new List<ProtoField>
        {
            ProtoField.LengthDelimited(1, []),
            ProtoField.LengthDelimited(2, scoreResult),
        };

        // The result flow reads the server's BattleInput rather than retaining the
        // submitted instance. Echo it so both win and loss paths can finish loading.
        var battleInput = request.FirstOrDefault(field => field.Number == 4 && field.WireType == 2);
        if (battleInput is not null)
            response.Add(ProtoField.LengthDelimited(12, battleInput.Value));

        return ProtobufWire.Encode(response);
    }

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
        CopyNestedAs(fields, party, sourceNumber: 7, nestedSourceNumber: 1, targetNumber: 7);
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

            var memberId = GetInt64(memberFields, 2);
            if (memberId is null ||
                !TryFindMessage(memberWrapper, 3, out var memoriaBytes))
                continue;

            // Multi-party member IDs append the one-based member slot to the party ID
            // (for example, member 2041 belongs to party 204).
            var party = new List<ProtoField>
            {
                ProtoField.Varint(1, ulong.Parse(userId)),
                ProtoField.Varint(2, (ulong)(memberId.Value / 10)),
            };
            CopyAs(memberFields, party, sourceNumber: 20, targetNumber: 4);
            party.Add(ProtoField.Varint(6, 1));
            CopyAs(ProtobufWire.Parse(memoriaBytes), party, sourceNumber: 1, targetNumber: 7);
            state.Parties.Add(ProtobufWire.Encode(party));
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

    private static void CopyNestedAs(
        List<ProtoField> source,
        List<ProtoField> target,
        int sourceNumber,
        int nestedSourceNumber,
        int targetNumber)
    {
        var wrapper = source.FirstOrDefault(value => value.Number == sourceNumber && value.WireType == 2);
        if (wrapper is null) return;
        CopyAs(ProtobufWire.Parse(wrapper.Value), target, nestedSourceNumber, targetNumber);
    }

    private static void EnsureVarint(List<ProtoField> fields, int number, ulong value)
    {
        fields.RemoveAll(field => field.Number == number);
        fields.Insert(0, ProtoField.Varint(number, value));
    }

    private static byte[] CreateResponse(
        byte[] responseTemplateBody,
        IHeaderDictionary responseHeaders,
        int responseField,
        byte[] responseValue,
        List<ProtoField>? updateTables)
    {
        // Preserve the complete captured success envelope. Some endpoint handlers expect
        // User.delete and User.other_info to remain present even when no rows are updated.
        var templatePlain = Decompress(Decrypt(responseTemplateBody, ServerApiKey));
        var root = ProtobufWire.Parse(templatePlain);

        if (updateTables is not null)
        {
            var commonIndex = root.FindIndex(field => field.Number == 101 && field.WireType == 2);
            if (commonIndex < 0)
                throw new InvalidDataException("Secure response template has no CommonResponse.");

            var common = ProtobufWire.Parse(root[commonIndex].Value);
            var userIndex = common.FindIndex(field => field.Number == 1 && field.WireType == 2);
            if (userIndex < 0)
                throw new InvalidDataException("Secure response template has no User response.");

            var user = ProtobufWire.Parse(common[userIndex].Value);
            user.RemoveAll(field => field.Number == 1);
            user.Insert(0, ProtoField.LengthDelimited(1, ProtobufWire.Encode(updateTables)));
            common[userIndex] = ProtoField.LengthDelimited(1, ProtobufWire.Encode(user));
            root[commonIndex] = ProtoField.LengthDelimited(101, ProtobufWire.Encode(common));
        }

        root.RemoveAll(field => field.Number == ApiResponseStorePurchaseRestartField);
        root.RemoveAll(field => field.Number == responseField);
        root.Add(ProtoField.LengthDelimited(responseField, responseValue));
        return EncodeResponse(ProtobufWire.Encode(root), responseHeaders);
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
        public List<byte[]> StoryDramaSelections { get; } = [];
    }
}
