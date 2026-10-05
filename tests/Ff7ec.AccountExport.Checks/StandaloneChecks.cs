using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ff7ec.Octo;
using Ff7ec.Server;
using K4os.Compression.LZ4.Streams;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

internal static class StandaloneChecks
{
    private const string ApiHost = "game-q74z3cyn.app.gl.ffviiec.com";
    private const string ManifestHost = "resources-api-c9ps53g2.app.gl.ffviiec.com";
    private const string AssetHost = "resources-data-w6d4k7cz.app.gl.ffviiec.com";
    private const string MasterHost = "client-masterdata-c9ps53g2.app.gl.ffviiec.com";

    public static async Task<int> Run(string root, string snapshot, int rows, string? installedGame = null)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        string fixtureRoot = Path.Combine(root, installedGame is null ? "standalone-fixture" : "standalone-installed");
        Directory.CreateDirectory(fixtureRoot);
        if (installedGame is null)
        {
            var required = new Dictionary<string, IReadOnlySet<long>>
            {
                ["m_skill_special"] = new HashSet<long> { 100101, 100104 },
                ["m_character"] = new HashSet<long> { 1001 },
                ["m_weapon"] = new HashSet<long> { 101001 },
            };
            string selectionGame = CreateGame(Path.Combine(fixtureRoot, "catalog-selection"), required);
            var selected = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                selectionGame, MasterHost, ManifestHost, AssetHost, required);
            string selectionMaster = Path.Combine(selectionGame, "FF7EC_Data", "StreamingAssets", "MasterData");
            string indexPath = Path.Combine(selectionMaster, "index.json");
            string originalIndex = File.ReadAllText(indexPath);
            var selectionIndex = JsonNode.Parse(originalIndex)!;
            Check(selected.MasterCatalogPath != selectionIndex["master_data_catalog"]!.GetValue<string>(),
                "Catalog selection used a base dataset missing the referenced skill ID.");
            selectionIndex["master_data_catalog"] = selected.MasterCatalogPath;
            File.WriteAllText(indexPath, selectionIndex.ToJsonString());
            var indexed = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                selectionGame, MasterHost, ManifestHost, AssetHost, required);
            Check(indexed.MasterCatalogPath == selected.MasterCatalogPath,
                "A compatible indexed catalog was not preferred.");
            File.WriteAllText(indexPath, originalIndex);
            var unmatched = new Dictionary<string, IReadOnlySet<long>>(required)
            {
                ["m_skill_special"] = new HashSet<long> { 400401 },
            };
            try
            {
                _ = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                    selectionGame, MasterHost, ManifestHost, AssetHost, unmatched);
                throw new InvalidOperationException("Missing master skill data was silently accepted.");
            }
            catch (InvalidDataException) { checks++; }
            string selectedFile = Path.Combine(selectionMaster,
                selected.MasterCatalogPath[1..].Replace('/', Path.DirectorySeparatorChar));
            byte[] selectedBytes = File.ReadAllBytes(selectedFile);
            using var catalogAes = Aes.Create();
            catalogAes.Key = Convert.FromBase64String("ZtV6ceJZqRqChLynCi0GBnl6llNbRoSZoT2QabU+SJA=");
            byte[] duplicateBytes = EncryptMaster(catalogAes.DecryptCbc(selectedBytes[16..],
                selectedBytes[..16], PaddingMode.PKCS7));
            string duplicateFile = Path.Combine(selectionMaster, "catalogs",
                LocalGameContentStore.Sha256Name(duplicateBytes) + ".json");
            File.WriteAllBytes(duplicateFile, duplicateBytes);
            try
            {
                _ = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                    selectionGame, MasterHost, ManifestHost, AssetHost, required);
                throw new InvalidOperationException("Ambiguous compatible master catalogs were silently selected.");
            }
            catch (InvalidDataException) { checks++; }
            File.Delete(duplicateFile);
            selectedBytes[^1] ^= 0xff;
            File.WriteAllBytes(selectedFile, selectedBytes);
            try
            {
                _ = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                    selectionGame, MasterHost, ManifestHost, AssetHost, required);
                throw new InvalidOperationException("A corrupt compatible master catalog passed verification.");
            }
            catch (InvalidDataException) { checks++; }
        }
        var account = new AccountExportStore(NullLogger<AccountExportStore>.Instance, snapshot,
            AccountExportStore.DefaultProtocolSchemaPath, ApiHost);
        string game = installedGame ?? CreateGame(Path.Combine(fixtureRoot, "game"), account.RequiredMasterIds);
        var content = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
            game, MasterHost, ManifestHost, AssetHost, account.RequiredMasterIds);
        using var installedIndex = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(game, "FF7EC_Data", "StreamingAssets",
            "MasterData", "index.json")));
        Check((content.MasterCatalogPath != installedIndex.RootElement.GetProperty("master_data_catalog").GetString()) ==
              (account.RequiredMasterIds.Count > 0),
            "The incompatible indexed master catalog was selected instead of the account-compatible installed catalog.");
        try
        {
            _ = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance, game, MasterHost, ManifestHost,
                AssetHost, new Dictionary<string, IReadOnlySet<long>> { ["m_skill_special"] = new HashSet<long> { long.MaxValue } });
            throw new InvalidOperationException("A missing exported master skill ID was accepted.");
        }
        catch (InvalidDataException) { checks++; }
        Check(content.OctoVersion == 323173179 && content.Revision == 366, "Wrong installed Octo version/revision.");
        Check(content.TryGet(MasterHost, content.MasterCatalogPath, out var catalog, out _), "Local master catalog missing.");
        Check(LocalGameContentStore.Sha256Name(catalog) == Path.GetFileNameWithoutExtension(content.MasterCatalogPath),
            "Local master catalog was changed.");
        Check(content.TryGet(ManifestHost, $"/v1/list/{content.OctoVersion}/0", out var manifest, out _), "Local manifest missing.");
        byte[] steamCache = OctoCrypto.EncryptSecureFile(manifest, OctoCrypto.SteamDatabaseAppKey);
        Check(OctoCrypto.DecryptSecureFile(steamCache, OctoCrypto.SteamDatabaseAppKey).SequenceEqual(manifest),
            "Steam Octo cache encryption changed the manifest.");
        if (installedGame is null)
        {
            string fixtureManifest = Path.Combine(game, "octo", "pdb", "3001", "323173179", "octocacheevai");
            byte[] legacyCache = File.ReadAllBytes(fixtureManifest);
            File.WriteAllBytes(fixtureManifest, steamCache);
            var steamContent = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                game, MasterHost, ManifestHost, AssetHost);
            Check(steamContent.TryGet(ManifestHost, $"/v1/list/{content.OctoVersion}/0", out var steamManifest, out _) &&
                  steamManifest.SequenceEqual(manifest), "Standalone cannot read a manifest rewritten by the Steam client.");
            steamCache[^1] ^= 0xff;
            File.WriteAllBytes(fixtureManifest, steamCache);
            try
            {
                _ = new LocalGameContentStore(NullLogger<LocalGameContentStore>.Instance,
                    game, MasterHost, ManifestHost, AssetHost);
                throw new InvalidOperationException("A corrupted Steam Octo cache was accepted.");
            }
            catch (CryptographicException) { checks++; }
            catch (InvalidDataException) { checks++; }
            File.WriteAllBytes(fixtureManifest, legacyCache);
        }
        string? installedAssetPath = null;
        byte[] installedAsset = [];
        string? installedResourcePath = null;
        byte[] installedResource = [];
        if (installedGame is not null)
        {
            int missingCandidates = 0;
            foreach (var entry in ProtobufWire.Parse(manifest).Where(field => field.Number is 2 or 4).Take(100))
            {
                installedAssetPath = "/" + Encoding.UTF8.GetString(ProtobufWire.Parse(entry.Value)
                    .Single(field => field.Number == 11).Value);
                try
                {
                    if (content.TryGet(AssetHost, installedAssetPath, out installedAsset, out _)) break;
                }
                catch (FileNotFoundException) { missingCandidates++; }
            }
            Check(installedAsset.Length > 0,
                $"No representative installed bundle was available ({missingCandidates} missing candidates).");
            var resource = ProtobufWire.Parse(manifest).First(field => field.Number == 4);
            installedResourcePath = "/" + Encoding.UTF8.GetString(ProtobufWire.Parse(resource.Value)
                .Single(field => field.Number == 11).Value);
            Check(content.TryGet(AssetHost, installedResourcePath, out installedResource, out _),
                "Representative installed resource was unavailable.");
        }
        Check(content.TryGet(ManifestHost, $"/v1/list/{content.OctoVersion}/{content.Revision}", out var delta, out _) &&
              ProtobufWire.Parse(delta).All(field => field.Number is not (2 or 4)), "Current revision redownloads every object.");
        Check(!content.TryGet(ManifestHost, $"/v1/list/{content.OctoVersion}/{content.Revision + 1}", out _, out _),
            "Newer client revision was silently downgraded.");
        Check(content.TryGet(ManifestHost, $"/v1/revision/{content.OctoVersion}", out var revisionBody, out var revisionType) &&
              revisionType == "application/x-protobuf", "Revision lookup does not advertise protobuf.");
        Check(revisionBody.SequenceEqual(new byte[] { 0x08, 0xee, 0x02 }),
            "Revision lookup does not encode Octo.Database revision 366 in varint field 1.");
        Check(!content.TryGet(ManifestHost, $"/v1/revision/{content.OctoVersion + 1}", out _, out _),
            "An unavailable Octo version received the installed revision.");
        Check(!content.TryGet(MasterHost, "/index.json", out _, out _), "Unlisted local files were exposed.");
        Check(!content.TryGet(MasterHost, "/../index.json", out _, out _), "Traversal exposed local files.");
        try { content.LanguageCatalogPath("unsupported"); throw new InvalidOperationException("Unknown language accepted."); }
        catch (InvalidDataException) { checks++; }

        string snapshotHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(snapshot)));
        var headers = new StandaloneResponseHeaders(account, content);
        var request = new DefaultHttpContext().Request;
        request.Headers["x-language"] = "en";
        var secureHeaders = new HeaderDictionary();
        headers.Apply(request, secureHeaders);
        Check(secureHeaders["X-Content-Encoding-Secure"] == "1" && secureHeaders["X-Token"].ToString().Length == 64,
            "Capture-independent secure headers missing.");
        var secondHeaders = new HeaderDictionary();
        headers.Apply(request, secondHeaders);
        Check(secureHeaders["X-Token"] == secondHeaders["X-Token"], "Offline token changes between responses.");

        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Ff7ec.Server.slnx")))
            repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Repository root not found.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string serverDirectory = Path.Combine(repository.FullName, "src", "Ff7ec.Server");
        string serverDll = Path.Combine(serverDirectory, "bin", configuration, "net8.0", "Ff7ec.Server.dll");
        string captures = Path.Combine(fixtureRoot, "captures");
        string data = Path.Combine(fixtureRoot, "data");
        Directory.CreateDirectory(captures);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "party-settings.json"), "old replay settings must not be read");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseProxy = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30),
        };
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = serverDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            serverDll, "--Ff7ec:ListenPort=" + port, "--Ff7ec:Standalone:Enabled=true",
            "--Ff7ec:Standalone:GameDirectory=" + game, "--Ff7ec:CapturesDirectory=" + captures,
            "--Ff7ec:DataDirectory=" + data, "--Ff7ec:CertDirectory=" + Path.Combine(fixtureRoot, "certs"),
            "--Ff7ec:GapsDirectory=" + Path.Combine(fixtureRoot, "gaps"),
            "--Ff7ec:AccountExport:JsonPath=" + Path.GetFullPath(snapshot),
            "--Ff7ec:AccountExport:ProtocolAssemblyPath=",
        }) start.ArgumentList.Add(argument);

        // First boot has no captures. Restart adds an invalid file that must never be read.
        await Server(async () =>
        {
            using var boot = await Get(ApiHost, "/api/check");
            Check(boot.IsSuccessStatusCode, "Capture-free boot check failed.");
            var bootRoot = Decode(await boot.Content.ReadAsByteArrayAsync(), boot);
            Check(bootRoot.All(field => field.Number != 101),
                "Public boot contains Common without User, which stalls the client's non-silent success callback.");
            var bootFields = Nested(bootRoot, 201);
            Check(ProtobufWire.ReadInt64(bootFields.Single(field => field.Number == 7)) == content.OctoVersion,
                "Boot check advertises the manifest revision instead of the Octo app version.");
            foreach (var (field, expectedHost) in new (int, string)[]
            {
                (5, ApiHost), (8, ManifestHost), (9, "webview-w62j4u3y.app.gl.ffviiec.com"), (10, ApiHost),
            })
            {
                string destination = Encoding.UTF8.GetString(bootFields.Single(value => value.Number == field).Value);
                Check(destination == expectedHost,
                    $"Boot destination {field} is not the local hostname expected by the client.");
                Check(new Uri("https://" + destination).Host == expectedHost,
                    $"Client HTTPS prefix produced an invalid boot destination for field {field}.");
            }
            Check(boot.Headers.GetValues("X-Master-Path").Single() == content.MasterCatalogPath, "Boot master path missing.");
            Check(boot.Headers.GetValues("X-Master-Language-Path").Single() == content.LanguageCatalogPath("en"),
                "Boot language path missing.");
            foreach (var (path, field) in new (string, int)[]
            {
                ("/api/auth/session", 303), ("/api/pvt/user/title", 353), ("/api/pvt/notice/check", 488),
                ("/api/pvt/store/purchase/restart/steam", 2001), ("/api/announcement/list", 203),
                ("/api/pvt/gift/list", 376),
            })
            {
                using var response = await Post(path, field, []);
                Check(response.IsSuccessStatusCode, "Capture-free endpoint failed: " + path + " " + response.StatusCode);
                var decoded = Decode(await response.Content.ReadAsByteArrayAsync(), response);
                Check(decoded.Any(item => item.Number == field), "Wrong capture-free endpoint envelope: " + path);
                var common = decoded.SingleOrDefault(item => item.Number == 101);
                Check(field == 203 ? common is null : common is not null,
                    "Wrong public/account Common presence at " + path);
                if (common is not null)
                {
                    var commonFields = ProtobufWire.Parse(common.Value);
                    Check(commonFields.Any(item => item.Number == 1 && item.WireType == 2),
                        "Common.User is missing; the client dereferences it before completing " + path);
                    var userFields = Nested(commonFields, 1);
                    Check(userFields.Any(item => item.Number == 1 && item.WireType == 2) &&
                          userFields.Any(item => item.Number == 2 && item.WireType == 2),
                        "Common.User must supply Update and Delete messages at " + path);
                }
                Check(response.Headers.GetValues("X-Content-Encoding-Secure").Single() == "1", "Secure encoding flag missing.");
                Check(response.Headers.GetValues("X-Server-Time").Single() ==
                      account.FrozenServerTime.ToString(CultureInfo.InvariantCulture), "Standalone clock advanced.");
                if (field == 353) Check(Tables(decoded).Count == rows, "Standalone title lost exported account rows.");
            }
            using var wrongUser = await Post("/api/pvt/user/title", 353, [], "1");
            Check(wrongUser.StatusCode == HttpStatusCode.Forbidden, "Standalone accepted another account.");
            using var wrongHost = await Send(HttpMethod.Post, MasterHost, "/api/pvt/party/solo/set/upsert?user_id=" + account.UserId,
                Encrypt(321, []));
            Check(wrongHost.StatusCode == HttpStatusCode.NotImplemented, "A write on a non-API host was accepted.");
            using var unknown = await Post("/api/pvt/unknown", 353, []);
            Check(unknown.StatusCode == HttpStatusCode.NotImplemented, "Unknown APIs returned fake success.");
            using var battle = await Post("/api/pvt/story/battle/end", 336, []);
            Check(battle.StatusCode == HttpStatusCode.NotImplemented, "Unimplemented battle progression returned fake success.");
            using var multiplayer = await Post("/api/pvt/party/multi/set/upsert", 319, []);
            Check(multiplayer.StatusCode == HttpStatusCode.NotImplemented, "Multiplayer write accepted in single-player mode.");
            using var wrongMethod = await Get(ApiHost, "/api/pvt/party/solo/set/upsert?user_id=" + account.UserId);
            Check(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "Standalone write allowed GET.");
            using var malformed = await Send(HttpMethod.Post, ApiHost, "/api/pvt/party/solo/set/upsert?user_id=" + account.UserId, []);
            Check(malformed.StatusCode == HttpStatusCode.BadRequest, "Standalone accepted malformed write.");
            Check(!File.Exists(Path.Combine(data, "standalone", "party-settings.json")), "Rejected write changed persistent data.");
            using var malformedSecure = await Send(HttpMethod.Post, ApiHost,
                "/api/pvt/party/solo/set/upsert?user_id=" + account.UserId, new byte[32]);
            Check(malformedSecure.StatusCode == HttpStatusCode.BadRequest, "Malformed encrypted write was accepted.");
            Check(!File.Exists(Path.Combine(data, "standalone", "party-settings.json")), "Malformed ciphertext was persisted.");
            using var localManifest = await Get(ManifestHost, $"/v1/list/{content.OctoVersion}/0");
            Check((await localManifest.Content.ReadAsByteArrayAsync()).SequenceEqual(manifest), "HTTP manifest changed installed data.");
            if (installedAssetPath is not null)
            {
                using var installedBundle = await Get(AssetHost, installedAssetPath);
                Check(installedBundle.IsSuccessStatusCode &&
                      (await installedBundle.Content.ReadAsByteArrayAsync()).SequenceEqual(installedAsset),
                    "HTTP serving changed the representative installed bundle.");
            }
            if (installedResourcePath is not null)
            {
                using var resource = await Get(AssetHost, installedResourcePath);
                Check(resource.IsSuccessStatusCode &&
                      (await resource.Content.ReadAsByteArrayAsync()).SequenceEqual(installedResource),
                    "HTTP serving changed the representative installed resource.");
            }
            using var revisionResponse = await Get(ManifestHost, $"/v1/revision/{content.OctoVersion}");
            Check(revisionResponse.IsSuccessStatusCode &&
                  revisionResponse.Content.Headers.ContentType?.MediaType == "application/x-protobuf",
                "HTTP revision lookup does not advertise protobuf.");
            var revisionFields = ProtobufWire.Parse(await revisionResponse.Content.ReadAsByteArrayAsync());
            Check(revisionFields.Count == 1 && revisionFields[0].Number == 1 && revisionFields[0].WireType == 0 &&
                  ProtobufWire.ReadInt64(revisionFields[0]) == content.Revision,
                "HTTP revision lookup does not match the client's Octo.Database protobuf contract.");
            using var localCatalog = await Get(MasterHost, content.MasterCatalogPath);
            Check((await localCatalog.Content.ReadAsByteArrayAsync()).SequenceEqual(catalog), "HTTP catalog changed encrypted content.");
            using var traversal = await Get(MasterHost, "/%2e%2e/index.json");
            Check(traversal.StatusCode == HttpStatusCode.NotImplemented, "HTTP traversal exposed local files.");
            using var party = await Post("/api/pvt/party/solo/set/upsert", 321, ProtobufWire.Encode([
                ProtoField.Varint(1, 1), ProtoField.LengthDelimited(2, Encoding.UTF8.GetBytes("standalone-party")),
            ]));
            Check(party.IsSuccessStatusCode, "Standalone party write failed.");
            Check(File.Exists(Path.Combine(data, "standalone", "party-settings.json")), "Standalone write was not persisted separately.");
            var parties = Tables(Decode(await party.Content.ReadAsByteArrayAsync(), party));
            Check(parties.Any(field => field.Number == 312005933 && ProtobufWire.Parse(field.Value)
                .Any(value => value.Number == 3 && Encoding.UTF8.GetString(value.Value) == "standalone-party")),
                "Standalone acknowledgement omitted the party update.");
            if (installedGame is null)
            {
                using var bundle = await Get(AssetHost, "/A10");
                Check((await bundle.Content.ReadAsByteArrayAsync()).SequenceEqual(Encoding.UTF8.GetBytes("fixture-bundle")),
                    "Local bundle changed.");
                using var missing = await Get(AssetHost, "/A11");
                Check(missing.StatusCode == HttpStatusCode.ServiceUnavailable, "Missing local bundle was silently ignored.");
                string path = Path.Combine(game, "FF7EC_Data", "StreamingAssets", "MasterData",
                    content.MasterCatalogPath[1..].Replace('/', Path.DirectorySeparatorChar));
                byte[] original = File.ReadAllBytes(path);
                File.WriteAllBytes(path, [1, 2, 3]);
                using var corrupt = await Get(MasterHost, content.MasterCatalogPath);
                Check(corrupt.StatusCode == HttpStatusCode.ServiceUnavailable, "Changed catalog passed integrity verification.");
                File.WriteAllBytes(path, original);
            }
        });
        File.WriteAllText(Path.Combine(captures, "poison.meta.json"), "This capture must never be opened.");
        await Server(async () =>
        {
            using var title = await Post("/api/pvt/user/title", 353, []);
            Check(title.IsSuccessStatusCode, "Capture-free restart failed.");
            Check(Tables(Decode(await title.Content.ReadAsByteArrayAsync(), title)).Any(field => field.Number == 312005933 &&
                ProtobufWire.Parse(field.Value).Any(value => value.Number == 3 &&
                    Encoding.UTF8.GetString(value.Value) == "standalone-party")), "Standalone restart lost the party setting.");
        });
        if (installedGame is null)
        {
            string savedPath = Path.Combine(data, "standalone", "party-settings.json");
            string saved = File.ReadAllText(savedPath);
            var invalid = JsonNode.Parse(saved)!;
            invalid["records"]![0]!["bodyBase64"] = "invalid-base64";
            File.WriteAllText(savedPath, invalid.ToJsonString());
            await Server(async () =>
            {
                using var title = await Post("/api/pvt/user/title", 353, []);
                Check(title.StatusCode == HttpStatusCode.InternalServerError,
                    "Unreadable persisted settings silently reverted to the original export.");
                Check(!title.Headers.Contains("X-Content-Encoding-Secure"), "Failure retained success-shaped secure headers.");
            });
            File.WriteAllText(savedPath, saved);
            string invalidRoot = Path.Combine(fixtureRoot, "invalid-settings");
            Directory.CreateDirectory(invalidRoot);
            File.WriteAllText(Path.Combine(invalidRoot, "party-settings.json"), "{");
            File.WriteAllText(Path.Combine(invalidRoot, "story-state.json"), "{");
            try
            {
                new PartySettingsStore(NullLogger<PartySettingsStore>.Instance, invalidRoot, failOnInvalidData: true);
                throw new InvalidOperationException("Malformed standalone party JSON was discarded.");
            }
            catch (InvalidDataException) { checks++; }
            try
            {
                new StoryStateStore(NullLogger<StoryStateStore>.Instance, invalidRoot, failOnInvalidData: true);
                throw new InvalidOperationException("Malformed standalone story JSON was discarded.");
            }
            catch (InvalidDataException) { checks++; }
        }
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(snapshot))) == snapshotHash, "The original JSON export was modified.");
        return checks;

        async Task Server(Func<Task> verify)
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start standalone fixture server.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                bool ready = false;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException("Standalone startup failed: " + await stdout + await stderr);
                    try
                    {
                        using var probe = await Get(ApiHost, "/api/check");
                        Check(probe.IsSuccessStatusCode, "Standalone readiness failed: " + probe.StatusCode);
                        ready = true;
                        break;
                    }
                    catch (HttpRequestException) { await Task.Delay(200); }
                }
                Check(ready, "Standalone server did not become responsive.");
                await verify();
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                string output = await stdout + await stderr;
                Check(output.Contains("replay loading is disabled", StringComparison.Ordinal), "Standalone loaded the replay store.");
                Check(!output.Contains("Failed to load capture", StringComparison.Ordinal), "Standalone inspected ignored captures.");
            }
        }

        Task<HttpResponseMessage> Get(string host, string path) => Send(HttpMethod.Get, host, path, null);
        Task<HttpResponseMessage> Post(string path, int field, byte[] payload, string? user = null) =>
            Send(HttpMethod.Post, ApiHost, path + "?user_id=" + (user ?? account.UserId), Encrypt(field, payload));
        async Task<HttpResponseMessage> Send(HttpMethod method, string host, string path, byte[]? body)
        {
            using var message = new HttpRequestMessage(method, path);
            message.Headers.Host = host;
            message.Headers.Add("x-language", "en");
            if (body is not null)
            {
                message.Headers.Add("x-content-hash", "standalone-fixture");
                message.Content = new ByteArrayContent(body);
            }
            return await client.SendAsync(message);
        }
    }

    private static string CreateGame(string game, IReadOnlyDictionary<string, IReadOnlySet<long>> requiredMasterIds)
    {
        string manifest = Path.Combine(game, "octo", "pdb", "3001", "323173179", "octocacheevai");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        byte[] bundle = Encoding.UTF8.GetBytes("fixture-bundle");
        string md5 = Convert.ToHexString(MD5.HashData(bundle)).ToLowerInvariant();
        var item = ProtobufWire.Encode([
            ProtoField.Varint(1, 1), ProtoField.LengthDelimited(3, Encoding.UTF8.GetBytes("fixture.d")),
            ProtoField.Varint(4, (ulong)bundle.Length), ProtoField.LengthDelimited(10, Encoding.ASCII.GetBytes(md5)),
            ProtoField.LengthDelimited(11, Encoding.UTF8.GetBytes("A10")),
        ]);
        var missing = ProtobufWire.Encode([
            ProtoField.Varint(1, 2), ProtoField.LengthDelimited(3, Encoding.UTF8.GetBytes("missing.d")),
            ProtoField.Varint(4, 1), ProtoField.LengthDelimited(10, Encoding.ASCII.GetBytes(new string('0', 32))),
            ProtoField.LengthDelimited(11, Encoding.UTF8.GetBytes("A11")),
        ]);
        File.WriteAllBytes(manifest, OctoCrypto.EncryptSecureFile(ProtobufWire.Encode([
            ProtoField.Varint(1, 366), ProtoField.LengthDelimited(2, item), ProtoField.LengthDelimited(4, missing),
        ])));
        string cache = Path.Combine(game, "octo", "v1", "3001", "1", "4131");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, md5), bundle);
        string master = Path.Combine(game, "FF7EC_Data", "StreamingAssets", "MasterData");
        string part = Store(EncryptMaster(Encoding.UTF8.GetBytes("fixture-master")), "/assets/fixture/", ".dat");
        var records = new List<object> { new { table = "fixture", path = part,
            size = new FileInfo(Path.Combine(master, part[1..].Replace('/', Path.DirectorySeparatorChar))).Length } };
        var compatibleRecords = new List<object>(records);
        foreach (var (table, ids) in requiredMasterIds)
        {
            records.Add(Record(table, []));
            compatibleRecords.Add(Record(table, ids));
        }
        string catalog = Store(EncryptMaster(JsonSerializer.SerializeToUtf8Bytes(new { tables = records })),
            "/catalogs/", ".json");
        Store(EncryptMaster(JsonSerializer.SerializeToUtf8Bytes(new { tables = compatibleRecords })),
            "/catalogs/", ".json");
        string language = Store(EncryptMaster(JsonSerializer.SerializeToUtf8Bytes(new { files = Array.Empty<object>() })),
            "/language_catalogs/en/", ".json");
        File.WriteAllText(Path.Combine(master, "index.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["master_data_catalog"] = catalog, ["language_catalog_en"] = language,
        }));
        return game;

        object Record(string table, IEnumerable<long> ids)
        {
            long[] values = ids.Order().ToArray();
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(values.Length);
            foreach (long id in values)
            {
                writer.WriteArrayHeader(1);
                writer.Write(id);
            }
            writer.Flush();
            using var compressed = new MemoryStream();
            using (var encoder = LZ4Stream.Encode(compressed)) encoder.Write(buffer.WrittenSpan);
            string path = Store(EncryptMaster(compressed.ToArray()), "/assets/fixture/", ".dat");
            return new { table, path, size = new FileInfo(Path.Combine(master,
                path[1..].Replace('/', Path.DirectorySeparatorChar))).Length };
        }

        string Store(byte[] bytes, string prefix, string suffix)
        {
            string path = prefix + LocalGameContentStore.Sha256Name(bytes) + suffix;
            string file = Path.Combine(master, path[1..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
            return path;
        }
    }

    private static byte[] EncryptMaster(byte[] plain) =>
        EncryptAes(plain, Convert.FromBase64String("ZtV6ceJZqRqChLynCi0GBnl6llNbRoSZoT2QabU+SJA="));

    private static byte[] Encrypt(int field, byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var encoder = LZ4Stream.Encode(stream))
            encoder.Write(ProtobufWire.Encode([ProtoField.LengthDelimited(field, payload)]));
        return EncryptAes(stream.ToArray(), Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4="));
    }

    private static byte[] EncryptAes(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        return aes.IV.Concat(aes.EncryptCbc(plain, aes.IV, PaddingMode.PKCS7)).ToArray();
    }

    private static List<ProtoField> Decode(byte[] body, HttpResponseMessage response)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String("CMMnsenXvr7izAFborJvCZHwFrG40sykNgUSqgJ99+A=");
        byte[] compressed = aes.DecryptCbc(body.AsSpan(16), body.AsSpan(0, 16), PaddingMode.PKCS7);
        string hash = Convert.ToBase64String(SHA256.HashData(compressed)).Replace('+', '-').Replace('/', '_');
        if (response.Headers.GetValues("X-Content-Hash").Single() != hash)
            throw new InvalidDataException("Standalone response hash does not match its body.");
        using var input = new MemoryStream(compressed);
        using var decoder = LZ4Stream.Decode(input);
        using var plain = new MemoryStream();
        decoder.CopyTo(plain);
        return ProtobufWire.Parse(plain.ToArray());
    }

    private static List<ProtoField> Nested(List<ProtoField> fields, int number)
    {
        var field = fields.SingleOrDefault(field => field.Number == number && field.WireType == 2);
        return field is null ? [] : ProtobufWire.Parse(field.Value);
    }

    private static List<ProtoField> Tables(List<ProtoField> fields) => Nested(Nested(Nested(fields, 101), 1), 1);
}
