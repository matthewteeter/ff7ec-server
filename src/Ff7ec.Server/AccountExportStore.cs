using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Ff7ec.Server;

public sealed class AccountExportStore
{
    public static string DefaultProtocolSchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "ProtocolSchemas", "ff7ec-24813881.json");

    private static readonly Dictionary<string, ListEndpoint> ListEndpoints = new(StringComparer.Ordinal)
    {
        ["/api/pvt/gift/list"] = new("PostPvtGiftList", "GiftsInfo", "RewardInfoList"),
        ["/api/pvt/gift/history"] = new("PostPvtGiftHistory", "GiftHistoryInfo", "HistoryGiftInfoList"),
        ["/api/pvt/friend/list"] = new("PostPvtFriendList", "FriendList", "UserListInfos"),
        ["/api/pvt/friend/receive/list"] = new("PostPvtFriendReceiveList", "FriendReceiveList", "UserListInfos"),
        ["/api/pvt/friend/request/list"] = new("PostPvtFriendRequestList", "FriendRequestList", "UserListInfos"),
        ["/api/pvt/user/deny/list"] = new("PostPvtUserDenyList", "BlockList", "UserListInfos"),
        ["/api/pvt/guild/member/list"] = new("PostPvtGuildMemberList", "GuildMemberList", "GuildMemberListInfos"),
        ["/api/pvt/guild/watch/list"] = new("PostPvtGuildWatchList", "GuildWatchList", "GuildInfoList"),
    };
    private readonly ProtocolSchema _schema;
    private readonly byte[] _accountTables;
    private readonly byte[] _otherInfo;
    private readonly Dictionary<string, byte[]> _listResponses = new(StringComparer.Ordinal);
    private readonly string _host;

    public string UserId { get; }
    public long FrozenServerTime { get; }
    public IReadOnlyDictionary<string, IReadOnlySet<long>> RequiredMasterIds { get; }

    public AccountExportStore(
        ILogger<AccountExportStore> logger,
        string jsonPath,
        string protocolPath,
        string host,
        long? frozenServerTime = null)
    {
        _schema = new ProtocolSchema(protocolPath);
        using var input = File.OpenRead(jsonPath);
        using var document = JsonDocument.Parse(input);
        var snapshot = document.RootElement;
        _host = host;
        FrozenServerTime = frozenServerTime ?? new DateTimeOffset(File.GetLastWriteTimeUtc(jsonPath)).ToUnixTimeMilliseconds();
        if (FrozenServerTime <= 0 || FrozenServerTime > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            throw new InvalidDataException("Account export FrozenServerTime must be a valid positive Unix timestamp in milliseconds.");

        var required = ListEndpoints.Values.Select(endpoint => endpoint.Section).Append("AccountInfo").Append("OtherInfo").ToHashSet();
        if (snapshot.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Account export must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in snapshot.EnumerateObject())
        {
            if (!required.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException($"Unknown or duplicate account export section: {property.Name}.");
        }
        if (!seen.SetEquals(required))
            throw new InvalidDataException("Account export is missing required sections.");

        var account = snapshot.GetProperty("AccountInfo");
        _accountTables = _schema.Encode("Tables", account);
        var masterIds = new Dictionary<string, IReadOnlySet<long>>(StringComparer.Ordinal);
        foreach (var (table, section, field) in new[]
        {
            ("m_skill_special", "UserSkillSpecialList", "SpecialSkillId"),
            ("m_character", "UserCharacterList", "CharacterId"),
            ("m_weapon", "UserWeaponList", "WeaponId"),
        })
        {
            if (!account.TryGetProperty(section, out var list)) continue;
            var ids = list.ValueKind == JsonValueKind.Null ? [] :
                list.EnumerateArray().Select(row => row.TryGetProperty(field, out var value)
                    ? value.ValueKind == JsonValueKind.String
                        ? long.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                        : value.GetInt64()
                    : 0L).ToHashSet();
            if (ids.Count > 0) masterIds.Add(table, ids.ToFrozenSet());
        }
        RequiredMasterIds = masterIds.ToFrozenDictionary(StringComparer.Ordinal);
        _otherInfo = _schema.Encode("UserOtherInfo", snapshot.GetProperty("OtherInfo"));
        var status = account.GetProperty("UserStatusList");
        if (status.ValueKind != JsonValueKind.Array || status.GetArrayLength() != 1)
            throw new InvalidDataException("Account export requires exactly one UserStatusList record.");
        long id = ReadId(status[0].GetProperty("UserId"));
        if (id <= 0) throw new InvalidDataException("Account export UserId must be positive.");
        UserId = id.ToString(CultureInfo.InvariantCulture);
        foreach (var table in account.EnumerateObject().Where(table => table.Name.StartsWith("User", StringComparison.Ordinal)))
        {
            if (table.Value.ValueKind == JsonValueKind.Null) continue;
            foreach (var row in table.Value.EnumerateArray())
                if (row.TryGetProperty("UserId", out var rowId) && ReadId(rowId) != id)
                    throw new InvalidDataException($"Account export has mixed user IDs in {table.Name}.");
        }

        foreach (var (path, endpoint) in ListEndpoints)
        {
            var list = snapshot.GetProperty(endpoint.Section);
            if (list.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new InvalidDataException($"{endpoint.Section} must be an array or null.");
            var response = new Dictionary<string, object?> { [endpoint.Property] = list };
            if (path == "/api/pvt/gift/list")
                response["TotalGiftCount"] = list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            _listResponses.Add(path, _schema.Encode(endpoint.Message + "Response", JsonSerializer.SerializeToElement(response)));
            _schema.GetField("ApiRequest", endpoint.Message);
            _schema.GetField("ApiResponse", endpoint.Message);
        }
        foreach (string message in new[] { "PostAuthSession", "PostPvtUserTitle" })
        {
            _schema.GetField("ApiRequest", message);
            _schema.GetField("ApiResponse", message);
        }
        _schema.GetField("ApiResponse", "Common");
        _schema.GetField("ApiResponse", "PostPvtStorePurchaseRestartSteam");
        _schema.GetField("CommonResponse", "User");
        foreach (string property in new[] { "Update", "Delete", "OtherInfo" })
            _schema.GetField("User", property);
        _schema.GetField("PostPvtGiftListRequest", "PageNo");
        logger.LogInformation(
            "ACCOUNT EXPORT loaded {Tables} table sections; responses use local JSON and protocol mappings. Frozen clock: {Time}",
            account.EnumerateObject().Count(), FrozenServerTime);
    }

    public static void WriteProtocolSchema(string assemblyPath, string outputPath, string clientBuild) =>
        new ProtocolSchema(assemblyPath).WriteAccountSchema(outputPath, clientBuild, ListEndpoints.Values.Select(endpoint => endpoint.Message));

    public bool IsAccountRequest(HttpRequest request) =>
        string.Equals(request.Host.Host, _host, StringComparison.OrdinalIgnoreCase) &&
        (request.Path.StartsWithSegments("/api/pvt") || request.Path == "/api/auth/session");

    public bool MatchesUser(HttpRequest request) => request.Query["user_id"].ToString() == UserId;

    public bool Handles(string path) =>
        path is "/api/auth/session" or "/api/pvt/user/title" || ListEndpoints.ContainsKey(path);

    public bool TryGetHeaderTemplate(HttpRequest request, ReplayStore store, out CapturedResponse template)
    {
        var query = "?user_id=" + Uri.EscapeDataString(UserId);
        foreach (var path in new[] { request.Path.Value, "/api/pvt/user/title", "/api/pvt/store/purchase/restart/steam", "/api/auth/session" })
        {
            if (store.TryGet(request.Host.Host, HttpMethods.Post, path + query, out template) &&
                template.StatusCode == StatusCodes.Status200OK &&
                template.ResponseHeaders.Any(header =>
                    header.Name.Equals("X-Content-Encoding-Secure", StringComparison.OrdinalIgnoreCase) && header.Value == "1"))
                return true;
        }
        template = null!;
        return false;
    }

    public byte[] CreateResponse(HttpRequest request, byte[] body, IHeaderDictionary headers)
    {
        var path = request.Path.Value ?? string.Empty;
        var message = path switch
        {
            "/api/auth/session" => "PostAuthSession",
            "/api/pvt/user/title" => "PostPvtUserTitle",
            _ => ListEndpoints.TryGetValue(path, out var endpoint) ? endpoint.Message
                : throw new InvalidDataException($"Unsupported exported account endpoint {path}."),
        };
        var decoded = ProtobufWire.Parse(ApiTransport.DecodeRequest(body));
        var requestField = _schema.GetField("ApiRequest", message);
        var payload = decoded.SingleOrDefault(field => field.Number == requestField.Number && field.WireType == 2)
            ?? throw new InvalidDataException($"Request has no {message} payload.");
        var payloadFields = ProtobufWire.Parse(payload.Value);
        var value = _listResponses.GetValueOrDefault(path) ?? [];
        if (path == "/api/pvt/gift/list")
        {
            var pageField = _schema.GetField(message + "Request", "PageNo");
            var page = payloadFields.SingleOrDefault(field => field.Number == pageField.Number);
            long pageNo = page is null ? 0 : ProtobufWire.ReadInt64(page);
            if (pageNo < 0) throw new InvalidDataException("Gift page number cannot be negative.");
            // The export has no pagination metadata. Expose its captured list as one
            // page, retaining total count on subsequent empty pages.
            if (pageNo > 0)
            {
                var count = ProtobufWire.Parse(value).Where(field =>
                    field.Number == _schema.GetField(message + "Response", "TotalGiftCount").Number);
                value = ProtobufWire.Encode(count);
            }
        }
        var responseField = _schema.GetField("ApiResponse", message);
        return Encode(responseField.Number, value, path == "/api/pvt/user/title", headers);
    }

    public byte[] CreateWriteTemplate(IHeaderDictionary headers) =>
        Encode(_schema.GetField("ApiResponse", "PostPvtStorePurchaseRestartSteam").Number, [], false, headers);

    public void ValidateEndpointSchema(string message, JsonElement payload)
    {
        _schema.GetField("ApiRequest", message);
        _schema.GetField("ApiResponse", message);
        _schema.Encode(message + "Response", payload);
    }

    public byte[] CreateEndpointResponse(
        string message, byte[] requestBody, JsonElement payload, IHeaderDictionary headers,
        bool validateRequest = true, bool includeUser = true)
    {
        if (validateRequest)
        {
            var request = ProtobufWire.Parse(ApiTransport.DecodeRequest(requestBody));
            int number = _schema.GetField("ApiRequest", message).Number;
            var field = request.SingleOrDefault(field => field.Number == number && field.WireType == 2)
                ?? throw new InvalidDataException($"Request has no {message} payload.");
            ProtobufWire.Parse(field.Value);
        }
        return Encode(_schema.GetField("ApiResponse", message).Number,
            _schema.Encode(message + "Response", payload), false, headers, includeUser);
    }

    public byte[]? RemoveCapturedAccountState(byte[] body, IHeaderDictionary headers)
    {
        SetClock(headers);
        var root = ProtobufWire.Parse(ApiTransport.DecodeResponse(body));
        int commonNumber = _schema.GetField("ApiResponse", "Common").Number;
        int commonIndex = root.FindIndex(field => field.Number == commonNumber && field.WireType == 2);
        if (commonIndex < 0) return null;
        var common = ProtobufWire.Parse(root[commonIndex].Value);
        int userNumber = _schema.GetField("CommonResponse", "User").Number;
        int userIndex = common.FindIndex(field => field.Number == userNumber && field.WireType == 2);
        if (userIndex < 0) return null;
        common[userIndex] = ProtoField.LengthDelimited(userNumber, CreateUser(false));
        root[commonIndex] = ProtoField.LengthDelimited(commonNumber, ProtobufWire.Encode(common));
        return ApiTransport.EncodeResponse(ProtobufWire.Encode(root), headers);
    }

    private byte[] Encode(int responseNumber, byte[] value, bool fullAccount, IHeaderDictionary headers, bool includeUser = true)
    {
        var fields = new List<ProtoField>();
        if (includeUser)
        {
            var common = ProtobufWire.Encode([
                ProtoField.LengthDelimited(_schema.GetField("CommonResponse", "User").Number, CreateUser(fullAccount)),
            ]);
            fields.Add(ProtoField.LengthDelimited(_schema.GetField("ApiResponse", "Common").Number, common));
        }
        // A present Common message requires User in the client's success callback.
        fields.Add(ProtoField.LengthDelimited(responseNumber, value));
        var plain = ProtobufWire.Encode(fields);
        SetClock(headers);
        return ApiTransport.EncodeResponse(plain, headers);
    }

    private byte[] CreateUser(bool fullAccount) => ProtobufWire.Encode([
        ProtoField.LengthDelimited(_schema.GetField("User", "Update").Number, fullAccount ? _accountTables : []),
        ProtoField.LengthDelimited(_schema.GetField("User", "Delete").Number, []),
        ProtoField.LengthDelimited(_schema.GetField("User", "OtherInfo").Number, _otherInfo),
    ]);

    private void SetClock(IHeaderDictionary headers) =>
        headers["X-Server-Time"] = FrozenServerTime.ToString(CultureInfo.InvariantCulture);

    private static long ReadId(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture) : value.GetInt64();

    private sealed record ListEndpoint(string Message, string Section, string Property);
}
