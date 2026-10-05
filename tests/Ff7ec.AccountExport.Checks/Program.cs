using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ff7ec.Server;
using K4os.Compression.LZ4.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

string root = Path.Combine(Path.GetTempPath(), "ff7ec-account-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
const long userId = 9007199254740993;
const long clock = 1791143461000;
string id = userId.ToString(CultureInfo.InvariantCulture);
string jsonPath = Path.Combine(root, "snapshot.json");
string assembly = typeof(Fixture.Protocol.Tables).Assembly.Location;
string fixtureSchemaPath = Path.Combine(root, "fixture-schema.json");
string source = $$$"""
{
  "AccountInfo": {
    "UserStatusList": [{"UserId": {{{id}}}, "Exp": 9223372036854775807}],
    "UserWeaponList": [{"UserId": "{{{id}}}", "WeaponId": -1, "Name": "sample", "IsLock": false, "Quality": 1, "Weight": "18446744073709551615"}],
    "UserPartyList": [{"UserId": {{{id}}}, "PartyId": 1, "Name": "original"}],
    "UserPartyMemberList": null,
    "UserHomeBackgroundSettingList": [{"UserId": {{{id}}}, "BackgroundId": 1}],
    "UserStoryDramaSelectionList": [{"UserId": {{{id}}}, "SelectionId": 1, "SelectionIndex": 2}]
  },
  "OtherInfo": {"UserStoneList": [{"Count": 5}], "MissionAchievedList": null, "HasGift": true, "IsCombatPowerRefresh": false},
  "GiftsInfo": [{"Count": 3}],
  "GiftHistoryInfo": [{"RewardInfo": {"Count": 7}}],
  "FriendList": [{"PlayerName": "friend"}],
  "FriendReceiveList": null,
  "FriendRequestList": [],
  "BlockList": null,
  "GuildMemberList": [{"UserListInfo": {"PlayerName": "member"}}],
  "GuildWatchList": [{"GuildName": "guild"}]
}
""";

try
{
    File.WriteAllText(jsonPath, source);
    AccountExportStore.WriteProtocolSchema(assembly, fixtureSchemaPath, "1");
    string schemaSource = File.ReadAllText(fixtureSchemaPath);
    AccountExportStore.WriteProtocolSchema(assembly, fixtureSchemaPath, "1");
    Check(File.ReadAllText(fixtureSchemaPath) == schemaSource, "Schema generation is not deterministic.");
    var export = Load();
    Check(export.UserId == id && export.FrozenServerTime == clock, "Identity/clock changed.");
    var request = Request("/api/pvt/user/title");
    Check(export.IsAccountRequest(request) && export.MatchesUser(request), "Account request not recognized.");
    request.QueryString = new QueryString("?user_id=1");
    Check(!export.MatchesUser(request), "Another account was accepted.");
    request.Host = new HostString("assets.test");
    Check(!export.IsAccountRequest(request), "Asset host was treated as an account request.");
    request = Request("/api/pvt/user/title");

    var headers = new HeaderDictionary();
    var encrypted = export.CreateResponse(request, EncryptRequest(353, []), headers);
    var plain = Decode(encrypted, headers);
    var tables = Tables(plain);
    var fileExport = new AccountExportStore(NullLogger<AccountExportStore>.Instance, jsonPath, fixtureSchemaPath, "game.test", clock);
    var fileHeaders = new HeaderDictionary();
    var fileTitle = fileExport.CreateResponse(request, EncryptRequest(353, []), fileHeaders);
    Check(ProtobufWire.Encode(Decode(fileTitle, fileHeaders)).SequenceEqual(ProtobufWire.Encode(plain)),
        "Schema-file and metadata-backed responses differ.");
    using (var fixtureJson = JsonDocument.Parse(source))
        VerifyMessage(new ProtocolSchema(assembly), "Tables", fixtureJson.RootElement.GetProperty("AccountInfo"), tables);
    var status = Nested(tables, 259686066);
    Check(ProtobufWire.ReadInt64(status.Single(field => field.Number == 1)) == userId, "64-bit account ID lost precision.");
    Check(ProtobufWire.ReadInt64(status.Single(field => field.Number == 2)) == long.MaxValue, "64-bit value lost precision.");
    var weapon = Nested(tables, 231622239);
    Check(ProtobufWire.ReadInt64(weapon.Single(field => field.Number == 2)) == -1, "Signed value lost precision.");
    Check(ProtobufWire.ReadInt64(weapon.Single(field => field.Number == 6)) == -1, "Unsigned 64-bit value lost precision.");
    Check(ProtobufWire.ReadInt64(weapon.Single(field => field.Number == 4)) == 0, "False field was not encoded.");
    Check(ProtobufWire.ReadInt64(weapon.Single(field => field.Number == 5)) == 1, "Enum was not encoded.");
    Check(headers["X-Server-Time"] == clock.ToString(CultureInfo.InvariantCulture), "Clock not applied.");

    foreach (var (path, field, count) in new (string, int, int)[]
    {
        ("/api/auth/session", 303, 0), ("/api/pvt/gift/list", 376, 2),
        ("/api/pvt/gift/history", 377, 1), ("/api/pvt/friend/list", 481, 1),
        ("/api/pvt/friend/receive/list", 483, 0), ("/api/pvt/friend/request/list", 482, 0),
        ("/api/pvt/user/deny/list", 587, 0), ("/api/pvt/guild/member/list", 570, 1),
        ("/api/pvt/guild/watch/list", 569, 1),
    })
    {
        var response = Decode(export.CreateResponse(Request(path), EncryptRequest(field, []), headers), headers);
        Check(Nested(response, field).Count == count, $"Wrong endpoint shape for {path}.");
        Check(Tables(response).Count == 0, $"List response resent entire account at {path}.");
    }
    var nextPage = Decode(export.CreateResponse(Request("/api/pvt/gift/list"),
        EncryptRequest(376, ProtobufWire.Encode([ProtoField.Varint(1, 1)])), headers), headers);
    Check(Nested(nextPage, 376).Count == 1, "Subsequent gift page returned duplicates.");
    ExpectFailure(() => export.CreateResponse(Request("/api/pvt/gift/list"),
        EncryptRequest(376, ProtobufWire.Encode([ProtoField.Varint(1, ulong.MaxValue)])), headers));
    ExpectFailure(() => export.CreateResponse(request, EncryptRequest(303, []), headers));
    ExpectFailure(() => export.CreateResponse(request, EncryptRequest(353, [0x80]), headers));
    ExpectFailure(() => export.CreateResponse(request, [], headers));

    var removed = Decode(export.RemoveCapturedAccountState(encrypted, headers)!, headers);
    Check(Tables(removed).Count == 0, "Captured account tables were not suppressed.");
    Check(Nested(Nested(Nested(removed, 101), 1), 2).Count == 0, "Captured account deletes were not suppressed.");
    Check(Decode(export.CreateWriteTemplate(headers), headers).Any(field => field.Number == 2001), "Generated write template missing.");

    var parties = new PartySettingsStore(NullLogger<PartySettingsStore>.Instance, root);
    var stories = new StoryStateStore(NullLogger<StoryStateStore>.Instance, root);
    var merger = new PartyStateMerger(parties, stories, NullLogger<PartyStateMerger>.Instance);
    var partyWrite = EncryptRequest(321, ProtobufWire.Encode([
        ProtoField.Varint(1, 1), ProtoField.LengthDelimited(2, Encoding.UTF8.GetBytes("changed")),
    ]));
    await parties.AppendAsync("game.test", "/api/pvt/party/solo/set/upsert",
        "/api/pvt/party/solo/set/upsert?user_id=" + id, id, "fixture", "application/protobuf", partyWrite, default);
    var wallpaperWrite = EncryptRequest(526, ProtobufWire.Encode([ProtoField.Varint(1, 7)]));
    await parties.AppendAsync("game.test", "/api/pvt/user/home/background/setting",
        "/api/pvt/user/home/background/setting?user_id=" + id, id, "wallpaper", "application/protobuf", wallpaperWrite, default);
    var storyWrite = EncryptRequest(352, ProtobufWire.Encode([ProtoField.Varint(1, 1)]));
    await stories.AppendAsync("game.test", "/api/pvt/story/select/drama",
        "/api/pvt/story/select/drama?user_id=" + id, id, "story", storyWrite, default);
    var writeResponse = merger.CreateWriteResponse("/api/pvt/party/solo/set/upsert", id, partyWrite,
        export.CreateWriteTemplate(headers), headers);
    Check(writeResponse is not null, "Exported write acknowledgement failed.");
    Check(merger.CreateWriteResponse("/api/pvt/user/home/background/setting", id, wallpaperWrite,
        export.CreateWriteTemplate(headers), headers) is not null, "Exported wallpaper acknowledgement failed.");
    Check(merger.CreateEmptyWriteResponse("/api/pvt/story/select/drama", id, storyWrite,
        export.CreateWriteTemplate(headers), headers) is not null, "Exported story acknowledgement failed.");
    var title = export.CreateResponse(request, EncryptRequest(353, []), headers);
    var merged = merger.MergeReplayResponse(request, title, headers);
    Check(merged is not null, "Persisted writes were not overlaid.");
    Check(Encoding.UTF8.GetString(Nested(Tables(Decode(merged!, headers)), 312005933)
        .Single(field => field.Number == 3).Value) == "changed", "Export reset a persisted party name.");
    var mergedTables = Tables(Decode(merged!, headers));
    Check(ProtobufWire.ReadInt64(Nested(mergedTables, 242346576).Single(field => field.Number == 2)) == 7,
        "Export reset a persisted wallpaper.");
    Check(ProtobufWire.ReadInt64(Nested(mergedTables, 78231314).Single(field => field.Number == 3)) == 0,
        "Export reset story choice zero.");
    var reloadedMerger = new PartyStateMerger(new PartySettingsStore(NullLogger<PartySettingsStore>.Instance, root),
        new StoryStateStore(NullLogger<StoryStateStore>.Instance, root), NullLogger<PartyStateMerger>.Instance);
    Check(reloadedMerger.MergeReplayResponse(request, title, headers) is not null, "Overlay did not persist across restart.");

    foreach (var replacement in new[] { "{}", "null", "{", source.Replace("\"Exp\": 9223372036854775807", "\"NewField\": null"),
        source.Replace("\"UserPartyMemberList\": null", "\"UnknownList\": null"),
        source.Replace("\"WeaponId\": -1", "\"WeaponId\": 1.5"),
        source.Replace("\"Exp\": 9223372036854775807", "\"Exp\": 9223372036854775808"),
        source.Replace("\"UserPartyMemberList\": null", "\"UserPartyMemberList\": [{\"UserId\": 1}]") })
    {
        File.WriteAllText(jsonPath, replacement);
        ExpectFailure(() => Load());
    }
    File.WriteAllText(jsonPath, source);
    ExpectFailure(() => new AccountExportStore(NullLogger<AccountExportStore>.Instance, jsonPath, assembly, "game.test", 0));
    ExpectFailure(() => new AccountExportStore(NullLogger<AccountExportStore>.Instance, jsonPath, assembly, "game.test", long.MaxValue));
    ExpectFailure(() => new AccountExportStore(NullLogger<AccountExportStore>.Instance, jsonPath, jsonPath, "game.test", clock));
    foreach (string invalidSchema in new[] { "null", "{", schemaSource.Replace("\"formatVersion\": 1", "\"formatVersion\": 2"),
        schemaSource.Replace("\"clientBuild\": \"1\"", "\"clientBuild\": \"invalid\""),
        schemaSource.Replace("[1,\"int64\"]", "[0,\"int64\"]"),
        schemaSource.Replace("[1,\"int64\"]", "[1,\"unknown\"]"),
        schemaSource.Replace("[2,\"int64\"]", "[1,\"int64\"]"),
        schemaSource.Replace("\"messages\": {", "\"messages\": [], \"unexpected\": {") })
    {
        File.WriteAllText(fixtureSchemaPath, invalidSchema);
        ExpectFailure(() => new AccountExportStore(NullLogger<AccountExportStore>.Instance, jsonPath, fixtureSchemaPath, "game.test", clock));
    }
    File.WriteAllText(fixtureSchemaPath, schemaSource);
    ExpectFailure(() => AccountExportStore.WriteProtocolSchema(assembly, fixtureSchemaPath, "not-a-build"));
    ExpectFailure(() => AccountExportStore.WriteProtocolSchema(assembly, assembly, "1"));

    if (args.Length >= 2)
    {
        var actual = new AccountExportStore(NullLogger<AccountExportStore>.Instance, args[0], args[1], "game.test", clock);
        using var actualJson = JsonDocument.Parse(File.ReadAllText(args[0]));
        var account = actualJson.RootElement.GetProperty("AccountInfo");
        var actualRequest = Request("/api/pvt/user/title");
        actualRequest.QueryString = new QueryString("?user_id=" + actual.UserId);
        var actualTitle = actual.CreateResponse(actualRequest, EncryptRequest(353, []), headers);
        var actualTables = Tables(Decode(actualTitle, headers));
        var actualSchema = new ProtocolSchema(args[1]);
        int compareIndex = Array.IndexOf(args, "--compare-metadata");
        if (compareIndex >= 0)
        {
            var metadata = new ProtocolSchema(args[compareIndex + 1]);
            VerifyMessage(metadata, "Tables", account, actualTables);
            var fromMetadata = new AccountExportStore(NullLogger<AccountExportStore>.Instance, args[0],
                args[compareIndex + 1], "game.test", clock);
            var metadataTitle = fromMetadata.CreateResponse(actualRequest, EncryptRequest(353, []), headers);
            Check(ProtobufWire.Encode(Decode(metadataTitle, headers)).SequenceEqual(ProtobufWire.Encode(Decode(actualTitle, headers))),
                "Bundled and DLL-backed private account responses differ.");
        }
        VerifyMessage(actualSchema, "Tables", account, actualTables);
        VerifyMessage(actualSchema, "UserOtherInfo", actualJson.RootElement.GetProperty("OtherInfo"),
            Nested(Nested(Nested(Decode(actualTitle, headers), 101), 1), 3));
        Check(true, "Every exported account field was compared against decoded protobuf.");
        foreach (var (path, message, section, property) in new (string, string, string, string)[]
        {
            ("/api/pvt/gift/list", "PostPvtGiftList", "GiftsInfo", "RewardInfoList"),
            ("/api/pvt/gift/history", "PostPvtGiftHistory", "GiftHistoryInfo", "HistoryGiftInfoList"),
            ("/api/pvt/friend/list", "PostPvtFriendList", "FriendList", "UserListInfos"),
            ("/api/pvt/friend/request/list", "PostPvtFriendRequestList", "FriendRequestList", "UserListInfos"),
            ("/api/pvt/friend/receive/list", "PostPvtFriendReceiveList", "FriendReceiveList", "UserListInfos"),
            ("/api/pvt/user/deny/list", "PostPvtUserDenyList", "BlockList", "UserListInfos"),
            ("/api/pvt/guild/member/list", "PostPvtGuildMemberList", "GuildMemberList", "GuildMemberListInfos"),
            ("/api/pvt/guild/watch/list", "PostPvtGuildWatchList", "GuildWatchList", "GuildInfoList"),
        })
        {
            var list = actualJson.RootElement.GetProperty(section);
            var expected = new Dictionary<string, object?> { [property] = list };
            if (path == "/api/pvt/gift/list")
                expected["TotalGiftCount"] = list.ValueKind == JsonValueKind.Null ? 0 : list.GetArrayLength();
            actualRequest.Path = path;
            var requestField = actualSchema.GetField("ApiRequest", message);
            var responseField = actualSchema.GetField("ApiResponse", message);
            var response = actual.CreateResponse(actualRequest, EncryptRequest(requestField.Number, []), headers);
            VerifyMessage(actualSchema, responseField.Type, JsonSerializer.SerializeToElement(expected),
                Nested(Decode(response, headers), responseField.Number));
            Check(true, "Every exported list field was compared against decoded protobuf.");
        }
        int expectedRows = account.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Array)
            .Sum(property => property.Value.GetArrayLength());
        Check(actualTables.Count == expectedRows, "Private export lost table rows.");
        Check(actualTables.Select(field => field.Number).Distinct().Count() ==
            account.EnumerateObject().Count(property => property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0),
            "Private export lost populated table sections.");
        var actualWeapon = ProtobufWire.Parse(actualTables.First(field => field.Number == 231622239).Value);
        Check(actualWeapon.Count == account.GetProperty("UserWeaponList")[0].EnumerateObject().Count(property => property.Value.ValueKind != JsonValueKind.Null),
            "Private export lost weapon fields.");
        Console.WriteLine($"Private export: {account.EnumerateObject().Count()} table sections, {expectedRows} rows encoded.");
        if (args.Contains("--http"))
            await HttpChecks(actual, args[0], args[1], expectedRows, useDefaultSchema: args.Contains("--default-schema"));
    }
    else if (args.Contains("--http"))
        await HttpChecks(fileExport, jsonPath, fixtureSchemaPath, tables.Count);
    Check(File.Exists(AccountExportStore.DefaultProtocolSchemaPath), "Bundled schema missing from build output.");
    var bundledFixture = JsonNode.Parse(source)!.AsObject();
    var originalAccount = bundledFixture["AccountInfo"]!.AsObject();
    bundledFixture["AccountInfo"] = new JsonObject
    {
        ["UserStatusList"] = originalAccount["UserStatusList"]!.DeepClone(),
        ["UserPartyList"] = originalAccount["UserPartyList"]!.DeepClone(),
    };
    string bundledJsonPath = Path.Combine(root, "bundled-snapshot.json");
    File.WriteAllText(bundledJsonPath, bundledFixture.ToJsonString());
    var bundledExport = new AccountExportStore(NullLogger<AccountExportStore>.Instance, bundledJsonPath,
        AccountExportStore.DefaultProtocolSchemaPath, "game.test", clock);
    var bundledTitle = bundledExport.CreateResponse(Request("/api/pvt/user/title"), EncryptRequest(353, []), headers);
    Check(Tables(Decode(bundledTitle, headers)).Count == 2, "Bundled schema failed to encode source-only fixture.");
    if (args.Contains("--http"))
    {
        await HttpChecks(bundledExport, bundledJsonPath, AccountExportStore.DefaultProtocolSchemaPath, 2, useDefaultSchema: true);
        checks += await StandaloneChecks.Run(root, bundledJsonPath, 2);
        int installedGameIndex = Array.IndexOf(args, "--installed-game");
        if (installedGameIndex >= 0)
        {
            if (args.Length < 2 || installedGameIndex + 1 >= args.Length)
                throw new ArgumentException("--installed-game requires a private export, schema, and game directory.");
            using var privateJson = JsonDocument.Parse(File.ReadAllBytes(args[0]));
            int privateRows = privateJson.RootElement.GetProperty("AccountInfo").EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Array).Sum(property => property.Value.GetArrayLength());
            checks += await StandaloneChecks.Run(root, args[0], privateRows, args[installedGameIndex + 1]);
        }
    }
    Console.WriteLine($"Passed {checks} account-export checks.");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
    foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        Directory.Delete(directory);
    Directory.Delete(root);
}

async Task HttpChecks(AccountExportStore export, string snapshotPath, string protocolPath, int expectedRows, bool useDefaultSchema = false)
{
    var repository = new DirectoryInfo(AppContext.BaseDirectory);
    while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Ff7ec.Server.slnx")))
        repository = repository.Parent;
    if (repository is null) throw new InvalidOperationException("Repository root not found.");
    string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    string serverDirectory = Path.Combine(repository.FullName, "src", "Ff7ec.Server");
    string serverDll = Path.Combine(serverDirectory, "bin", configuration, "net8.0", "Ff7ec.Server.dll");
    int publishedIndex = Array.IndexOf(args, "--published-server");
    if (publishedIndex >= 0)
    {
        serverDll = Path.GetFullPath(args[publishedIndex + 1]);
        serverDirectory = Path.GetDirectoryName(serverDll)!;
    }
    string fixtureRoot = Path.Combine(root, useDefaultSchema ? "http-default" : "http-override");
    string captures = Path.Combine(fixtureRoot, "captures");
    Directory.CreateDirectory(captures);
    // No body file exists: HTTP account responses must be generated without reading
    // any captured account payload, even when the imported account is private.
    File.WriteAllText(Path.Combine(captures, "headers.meta.json"), JsonSerializer.Serialize(new
    {
        host = "game.test", method = "POST", pathAndQuery = "/api/pvt/user/title?user_id=" + export.UserId,
        statusCode = 200,
        responseHeaders = new[] { new[] { "Content-Type", "application/protobuf" },
            new[] { "X-Content-Encoding-Secure", "1" }, new[] { "X-Token", "offline-fixture" } },
    }));
    var staleHeaders = new HeaderDictionary();
    var staleRequest = Request("/api/pvt/user/title");
    staleRequest.QueryString = new QueryString("?user_id=" + export.UserId);
    var staleBody = export.CreateResponse(staleRequest, EncryptRequest(353, []), staleHeaders);
    File.WriteAllBytes(Path.Combine(captures, "stale.body.bin"), staleBody);
    File.WriteAllText(Path.Combine(captures, "stale.meta.json"), JsonSerializer.Serialize(new
    {
        host = "game.test", method = "POST", pathAndQuery = "/api/pvt/fixture/replay?user_id=" + export.UserId,
        statusCode = 200,
        responseHeaders = new[] { new[] { "Content-Type", "application/protobuf" },
            new[] { "X-Content-Encoding-Secure", "1" }, new[] { "X-Content-Hash", staleHeaders["X-Content-Hash"].ToString() } },
    }));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = serverDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (string argument in new[] { serverDll, "--Ff7ec:ListenPort=" + port,
        "--Ff7ec:CapturesDirectory=" + captures, "--Ff7ec:DataDirectory=" + Path.Combine(fixtureRoot, "data"),
        "--Ff7ec:CertDirectory=" + Path.Combine(fixtureRoot, "certs"),
        "--Ff7ec:GapsDirectory=" + Path.Combine(fixtureRoot, "gaps"),
        "--Ff7ec:AccountExport:JsonPath=" + snapshotPath,
        "--Ff7ec:AccountExport:ApiHost=game.test", "--Ff7ec:AccountExport:FrozenServerTime=" + clock })
        start.ArgumentList.Add(argument);
    start.Environment.Remove("Ff7ec__AccountExport__ProtocolAssemblyPath");
    start.Environment.Remove("Ff7ec__AccountExport__ProtocolSchemaPath");
    if (!useDefaultSchema)
        start.ArgumentList.Add("--Ff7ec:AccountExport:" +
            (Path.GetExtension(protocolPath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "ProtocolSchemaPath=" : "ProtocolAssemblyPath=") +
            protocolPath);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start HTTP fixture server.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        bool ready = false;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
                throw new InvalidOperationException("HTTP server startup failed: " + await stdout + await stderr);
            try
            {
                using var probe = await Send("/api/pvt/user/title", 353, export.UserId);
                if (probe.IsSuccessStatusCode) { ready = true; break; }
                throw new InvalidOperationException("HTTP readiness failed: " + probe.StatusCode);
            }
            catch (HttpRequestException) { await Task.Delay(200); }
        }
        Check(ready, "HTTP server did not become responsive.");
        foreach (var (path, field) in new (string, int)[]
        {
            ("/api/pvt/user/title", 353), ("/api/auth/session", 303), ("/api/pvt/gift/list", 376),
            ("/api/pvt/gift/history", 377), ("/api/pvt/friend/list", 481), ("/api/pvt/friend/request/list", 482),
            ("/api/pvt/friend/receive/list", 483), ("/api/pvt/user/deny/list", 587),
            ("/api/pvt/guild/member/list", 570), ("/api/pvt/guild/watch/list", 569),
        })
        {
            using var response = await Send(path, field, export.UserId);
            Check(response.IsSuccessStatusCode, "HTTP exported response failed: " + path + " " + response.StatusCode);
            var responseHeaders = new HeaderDictionary();
            responseHeaders["X-Content-Hash"] = response.Headers.GetValues("X-Content-Hash").Single();
            var decoded = Decode(await response.Content.ReadAsByteArrayAsync(), responseHeaders);
            Check(decoded.Any(item => item.Number == field), "HTTP response has the wrong endpoint field.");
            Check(Tables(decoded).Count == (field == 353 ? expectedRows : 0), "HTTP response has incorrect account rows.");
            Check(response.Headers.GetValues("X-Server-Time").Single() == clock.ToString(CultureInfo.InvariantCulture),
                "HTTP frozen clock not applied.");
        }
        using var wrong = await Send("/api/pvt/user/title", 353, "1");
        Check(wrong.StatusCode == HttpStatusCode.Forbidden, "HTTP another-account request not rejected.");
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/pvt/user/title?user_id=" + export.UserId);
        malformed.Headers.Host = "game.test";
        malformed.Content = new ByteArrayContent([]);
        using var rejected = await client.SendAsync(malformed);
        Check(rejected.StatusCode == HttpStatusCode.BadRequest, "HTTP malformed request not rejected.");
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/pvt/user/title?user_id=" + export.UserId);
        get.Headers.Host = "game.test";
        using var wrongMethod = await client.SendAsync(get);
        Check(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "HTTP GET bypassed generated title handling.");
        using var replay = await Send("/api/pvt/fixture/replay", 353, export.UserId);
        var replayHeaders = new HeaderDictionary();
        replayHeaders["X-Content-Hash"] = replay.Headers.GetValues("X-Content-Hash").Single();
        Check(Tables(Decode(await replay.Content.ReadAsByteArrayAsync(), replayHeaders)).Count == 0,
            "HTTP replay returned stale account tables.");

        using var party = new HttpRequestMessage(HttpMethod.Post, "/api/pvt/party/solo/set/upsert?user_id=" + export.UserId);
        party.Headers.Host = "game.test";
        party.Headers.Add("x-content-hash", "http-fixture");
        party.Content = new ByteArrayContent(EncryptRequest(321, ProtobufWire.Encode([
            ProtoField.Varint(1, 1), ProtoField.LengthDelimited(2, Encoding.UTF8.GetBytes("http-party")),
        ])));
        using var acknowledged = await client.SendAsync(party);
        Check(acknowledged.IsSuccessStatusCode, "HTTP exported party write failed.");
        var ackHeaders = new HeaderDictionary();
        ackHeaders["X-Content-Hash"] = acknowledged.Headers.GetValues("X-Content-Hash").Single();
        var ackTables = Tables(Decode(await acknowledged.Content.ReadAsByteArrayAsync(), ackHeaders));
        Check(Encoding.UTF8.GetString(Nested(ackTables, 312005933).Single(field => field.Number == 3).Value) == "http-party",
            "HTTP write acknowledgement omitted the changed setting.");
        using var refreshed = await Send("/api/pvt/user/title", 353, export.UserId);
        var refreshHeaders = new HeaderDictionary();
        refreshHeaders["X-Content-Hash"] = refreshed.Headers.GetValues("X-Content-Hash").Single();
        var refreshedTables = Tables(Decode(await refreshed.Content.ReadAsByteArrayAsync(), refreshHeaders));
        Check(refreshedTables.Where(field => field.Number == 312005933)
            .Select(field => ProtobufWire.Parse(field.Value)).Any(row =>
                ProtobufWire.ReadInt64(row.Single(field => field.Number == 2)) == 1 &&
                Encoding.UTF8.GetString(row.Single(field => field.Number == 3).Value) == "http-party"),
            "HTTP title reset a persisted write.");

        async Task<HttpResponseMessage> Send(string path, int field, string accountId)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path + "?user_id=" + accountId);
            message.Headers.Host = "game.test";
            message.Content = new ByteArrayContent(EncryptRequest(field, []));
            return await client.SendAsync(message);
        }
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await stdout;
        await stderr;
    }
}

AccountExportStore Load() => new(NullLogger<AccountExportStore>.Instance, jsonPath, assembly, "game.test", clock);
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void ExpectFailure(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or BadImageFormatException or KeyNotFoundException)
    { checks++; return; }
    throw new InvalidOperationException("Invalid input was accepted.");
}
HttpRequest Request(string path)
{
    var request = new DefaultHttpContext().Request;
    request.Method = HttpMethods.Post;
    request.Host = new HostString("game.test");
    request.Path = path;
    request.QueryString = new QueryString("?user_id=" + id);
    return request;
}
static List<ProtoField> Nested(List<ProtoField> fields, int number) =>
    ProtobufWire.Parse(fields.Single(field => field.Number == number && field.WireType == 2).Value);
static List<ProtoField> Tables(List<ProtoField> root) => Nested(Nested(Nested(root, 101), 1), 1);
static void VerifyMessage(ProtocolSchema schema, string message, JsonElement expected, List<ProtoField> actual)
{
    int expectedFields = 0;
    foreach (var property in expected.EnumerateObject())
    {
        var descriptor = schema.GetField(message, property.Name);
        var fields = actual.Where(field => field.Number == descriptor.Number).ToArray();
        var values = property.Value.ValueKind == JsonValueKind.Null ? []
            : descriptor.Repeated ? property.Value.EnumerateArray().ToArray() : new[] { property.Value };
        expectedFields += values.Length;
        if (fields.Length != values.Length) throw new InvalidOperationException("Round-trip field count differs: " + message + "." + property.Name);
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var field = fields[i];
            bool equal;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (field.WireType != 2) throw new InvalidOperationException("Nested field has incorrect wire type.");
                VerifyMessage(schema, descriptor.Type, value, ProtobufWire.Parse(field.Value));
                continue;
            }
            if (descriptor.Type == "System.String")
            {
                equal = field.WireType == 2 && Encoding.UTF8.GetString(field.Value) == value.GetString();
            }
            else if (descriptor.Type == "Google.Protobuf.ByteString")
            {
                equal = field.WireType == 2 && field.Value.SequenceEqual(value.GetBytesFromBase64());
            }
            else
            {
                ulong number = value.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    JsonValueKind.String when value.GetString()!.StartsWith('-') =>
                        unchecked((ulong)long.Parse(value.GetString()!, CultureInfo.InvariantCulture)),
                    JsonValueKind.String => ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    _ when value.TryGetInt64(out long signed) => unchecked((ulong)signed),
                    _ => value.GetUInt64(),
                };
                var offset = 0;
                equal = field.WireType == 0 && ProtobufWire.ReadVarint(field.Value, ref offset) == number;
            }
            if (!equal) throw new InvalidOperationException("Round-trip field value differs: " + message + "." + property.Name);
        }
    }
    if (actual.Count != expectedFields) throw new InvalidOperationException("Round-trip encoded extra fields: " + message);
}
static byte[] EncryptRequest(int field, byte[] value)
{
    var plain = ProtobufWire.Encode([ProtoField.LengthDelimited(field, value)]);
    using var output = new MemoryStream();
    using (var encoder = LZ4Stream.Encode(output)) encoder.Write(plain);
    using var aes = Aes.Create();
    aes.Key = Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4=");
    aes.GenerateIV();
    var compressed = output.ToArray();
    using var encrypted = new MemoryStream();
    encrypted.Write(aes.IV);
    using (var crypto = new CryptoStream(encrypted, aes.CreateEncryptor(), CryptoStreamMode.Write, true))
        crypto.Write(compressed);
    return encrypted.ToArray();
}
static List<ProtoField> Decode(byte[] body, IHeaderDictionary headers)
{
    using var aes = Aes.Create();
    aes.Key = Convert.FromBase64String("CMMnsenXvr7izAFborJvCZHwFrG40sykNgUSqgJ99+A=");
    aes.IV = body[..16];
    byte[] compressed = aes.DecryptCbc(body[16..], aes.IV);
    string hash = Convert.ToBase64String(SHA256.HashData(compressed)).Replace('+', '-').Replace('/', '_');
    if (hash != headers["X-Content-Hash"]) throw new InvalidOperationException("Response content hash mismatch.");
    using var input = new MemoryStream(compressed);
    using var decoder = LZ4Stream.Decode(input);
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return ProtobufWire.Parse(output.ToArray());
}
