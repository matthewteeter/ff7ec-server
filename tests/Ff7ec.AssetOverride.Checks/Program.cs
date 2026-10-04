using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ff7ec.Octo;
using Ff7ec.Server;
using Microsoft.Extensions.Logging.Abstractions;

var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Ff7ec.Server.slnx")))
    repository = repository.Parent;
if (repository is null) throw new InvalidOperationException("Repository root not found.");
string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
string tool = Path.Combine(repository.FullName, "tools", "Ff7ec.AssetOverride", "bin", configuration, "net8.0", "Ff7ec.AssetOverride.dll");
string root = Path.Combine(Path.GetTempPath(), "ff7ec-check-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
int checks = 0;
try
{
    var fixture = new Fixture(Path.Combine(root, "two packages"));
    Cli(fixture, "status");
    Cli(fixture, "apply", game: false);
    Check(!Directory.Exists(fixture.State), "Empty installation must not create recovery state.");
    Check(!fixture.Store().TryGetManifest("manifest.test", "/list", out _), "Empty installation replaced the original manifest.");
    Check(!fixture.Store().TryGetAsset("assets.test", "/object-0", out _), "Empty installation replaced an original asset.");
    Directory.CreateDirectory(fixture.Packages);
    Cli(fixture, "apply", game: false);

    fixture.Install(0);
    Cli(fixture, "apply");
    Check(fixture.Store().TryGetManifest("manifest.test", "/list", out var one), "Applied manifest missing.");
    Check(Md5(one, fixture.Names[0]) == fixture.StateJson(0)["ReplacementMd5"]!.GetValue<string>(), "First asset not patched.");
    Check(Md5(one, fixture.Names[1]) == fixture.OriginalMd5[1], "Uninstalled asset changed.");
    fixture.Install(1);
    Cli(fixture, "apply");
    Cli(fixture, "apply");
    Check(fixture.Store().TryGetAsset("assets.test", "/object-1", out var blob), "Replacement asset missing.");
    Check(Hex(MD5.HashData(blob)) == fixture.StateJson(1)["ReplacementMd5"]!.GetValue<string>(), "Served blob hash mismatch.");

    Directory.Move(fixture.Package(0), Path.Combine(fixture.Packages, "renamed"));
    Cli(fixture, "apply");
    Check(Directory.GetDirectories(fixture.State).Length == 2, "Package rename duplicated state.");
    Directory.Move(Path.Combine(fixture.Packages, "renamed"), Path.Combine(fixture.Root, "removed-0"));
    Cli(fixture, "apply");
    Check(fixture.Store().TryGetManifest("manifest.test", "/list", out var mixed), "Mixed manifest missing.");
    Check(Md5(mixed, fixture.Names[0]) == fixture.OriginalMd5[0], "Removed asset not restored in served manifest.");
    Check(Md5(mixed, fixture.Names[1]) == fixture.StateJson(1)["ReplacementMd5"]!.GetValue<string>(), "Remaining asset disabled.");
    Check(File.Exists(fixture.OriginalBlob(0)), "Removed original cache blob not restored.");
    Directory.Move(Path.Combine(fixture.Root, "removed-0"), fixture.Package(0));
    Cli(fixture, "apply");
    Cli(fixture, "restore");
    Check(File.ReadAllBytes(fixture.Manifest).SequenceEqual(fixture.OriginalManifest), "Full restore did not recover original manifest.");
    Check(!fixture.Store().TryGetManifest("manifest.test", "/list", out _), "Restored store still overrides manifest.");

    Cli(fixture, "apply");
    foreach (string directory in Directory.GetDirectories(fixture.Packages))
        Directory.Move(directory, Path.Combine(fixture.Root, Path.GetFileName(directory) + "-removed"));
    Cli(fixture, "apply", game: false);
    Check(File.ReadAllBytes(fixture.Manifest).SequenceEqual(fixture.OriginalManifest), "Removal of all packages failed.");
    Cli(fixture, "restore", game: false);

    var invalid = new Fixture(Path.Combine(root, "invalid"));
    invalid.Install(0);
    string packageJson = Path.Combine(invalid.Package(0), "override.json");
    string validJson = File.ReadAllText(packageJson);
    foreach (string field in new[] { "OriginalCrc", "ReplacementCrc", "ObjectName", "SourceSha256" })
    {
        var document = JsonNode.Parse(validJson)!.AsObject();
        document.Remove(field);
        File.WriteAllText(packageJson, document.ToJsonString());
        Cli(invalid, "status", success: false);
    }
    foreach (var change in new (string Field, object? Value)[]
    {
        ("SourcePath", null), ("SourcePath", "..\\outside.d"),
        ("SourcePath", invalid.OriginalBlob(0)), ("SourceSha256", null),
        ("SourceSha256", new string('0', 64)), ("GameDirectory", invalid.Game),
        ("StateDirectory", invalid.State), ("AssetName", "bad\nINSTALLED: injected")
    })
    {
        var document = JsonNode.Parse(validJson)!.AsObject();
        document[change.Field] = JsonSerializer.SerializeToNode(change.Value);
        File.WriteAllText(packageJson, document.ToJsonString());
        Cli(invalid, "status", success: false);
    }
    foreach (string content in new[] { "{", "null", "[]", "{}" })
    {
        File.WriteAllText(packageJson, content);
        Cli(invalid, "apply", success: false);
    }
    File.WriteAllText(packageJson, validJson);
    string source = Path.Combine(invalid.Package(0), "replacement.d");
    byte[] sourceBytes = File.ReadAllBytes(source);
    File.Delete(source);
    Cli(invalid, "apply", success: false);
    File.WriteAllBytes(source, Encoding.ASCII.GetBytes("not a Unity bundle"));
    Cli(invalid, "status", success: false);
    File.WriteAllBytes(source, sourceBytes);
    invalid.Install(1);
    string secondJson = Path.Combine(invalid.Package(1), "override.json");
    var duplicate = JsonNode.Parse(File.ReadAllText(secondJson))!.AsObject();
    duplicate["ObjectName"] = "object-0";
    File.WriteAllText(secondJson, duplicate.ToJsonString());
    Cli(invalid, "status", success: false);
    duplicate["AssetName"] = invalid.Names[0];
    duplicate["ObjectName"] = "object-1";
    File.WriteAllText(secondJson, duplicate.ToJsonString());
    Cli(invalid, "status", success: false);
    Check(File.ReadAllBytes(invalid.Manifest).SequenceEqual(invalid.OriginalManifest), "Invalid package modified game files.");

    var rollback = new Fixture(Path.Combine(root, "rollback"));
    rollback.Install(0);
    rollback.Install(1);
    string badPath = Path.Combine(rollback.Package(1), "override.json");
    var bad = JsonNode.Parse(File.ReadAllText(badPath))!.AsObject();
    bad["OriginalCrc"] = 999;
    File.WriteAllText(badPath, bad.ToJsonString());
    Cli(rollback, "apply", success: false);
    Check(File.ReadAllBytes(rollback.Manifest).SequenceEqual(rollback.OriginalManifest), "Later package failure did not roll back earlier application.");
    Check(rollback.StateJson(0)["Applied"]!.GetValue<bool>() == false, "Rollback remained applied.");

    var recovery = new Fixture(Path.Combine(root, "recovery"));
    recovery.Install(0);
    Cli(recovery, "apply");
    var state = recovery.StateJson(0);
    state["Applied"] = false;
    state["RestoreRequired"] = true;
    recovery.WriteState(0, state);
    ExpectFailure(() => recovery.Store().TryGetManifest("manifest.test", "/list", out _), "Interrupted recovery served manifest.");
    Cli(recovery, "apply");
    Check(!recovery.StateJson(0)["RestoreRequired"]!.GetValue<bool>(), "Recovery journal not cleared.");
    File.Delete(Path.Combine(recovery.Package(0), "replacement.d"));
    Cli(recovery, "restore", game: false);
    Check(File.ReadAllBytes(recovery.Manifest).SequenceEqual(recovery.OriginalManifest), "Restore requires source package.");

    var partial = new Fixture(Path.Combine(root, "partial write"));
    partial.Install(0);
    Cli(partial, "apply");
    var pending = partial.StateJson(0);
    pending["Applied"] = false;
    pending["RestoreRequired"] = true;
    partial.WriteState(0, pending);
    File.WriteAllBytes(partial.Manifest, partial.OriginalManifest);
    Cli(partial, "restore");
    Check(File.Exists(partial.OriginalBlob(0)), "Interrupted pre-manifest write lost original cache blob.");
    Check(!partial.StateJson(0)["RestoreRequired"]!.GetValue<bool>(), "Partial restore left recovery flag set.");

    var writeFailure = new Fixture(Path.Combine(root, "write failure"));
    writeFailure.Install(0);
    string blocked = Path.Combine(writeFailure.State, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(writeFailure.Names[0]))),
        "replacement.blob");
    Directory.CreateDirectory(blocked);
    Cli(writeFailure, "apply", success: false);
    Check(!writeFailure.StateJson(0)["Applied"]!.GetValue<bool>() &&
        !writeFailure.StateJson(0)["RestoreRequired"]!.GetValue<bool>(), "Write failure did not complete rollback.");
    Check(File.ReadAllBytes(writeFailure.Manifest).SequenceEqual(writeFailure.OriginalManifest), "Write failure changed original manifest.");

    var integrity = new Fixture(Path.Combine(root, "integrity"));
    integrity.Install(0);
    Cli(integrity, "apply");
    var active = integrity.StateJson(0);
    var malformedState = JsonNode.Parse(active.ToJsonString())!.AsObject();
    malformedState["ObjectName"] = "";
    integrity.WriteState(0, malformedState);
    ExpectFailure(() => integrity.Store().TryGetAsset("assets.test", "/object-0", out _), "Invalid active state silently fell through.");
    integrity.WriteState(0, active);
    string served = active["ServedBlobPath"]!.GetValue<string>();
    byte[] saved = File.ReadAllBytes(served);
    File.Delete(served);
    ExpectFailure(() => integrity.Store().TryGetAsset("assets.test", "/object-0", out _), "Missing active blob silently ignored.");
    File.WriteAllBytes(served, [1, 2, 3]);
    ExpectFailure(() => integrity.Store().TryGetAsset("assets.test", "/object-0", out _), "Corrupt blob silently served.");
    File.WriteAllBytes(served, saved);
    string payload = active["PatchedManifestPayloadPath"]!.GetValue<string>();
    byte[] savedPayload = File.ReadAllBytes(payload);
    File.WriteAllBytes(payload, [1, 2, 3]);
    ExpectFailure(() => integrity.Store().TryGetManifest("manifest.test", "/list", out _), "Corrupt manifest silently served.");
    File.WriteAllBytes(payload, savedPayload);
    Cli(integrity, "restore");

    var legacy = new Fixture(Path.Combine(root, "legacy"));
    legacy.Install(0);
    string legacyDirectory = Path.Combine(legacy.State, "tifa-019");
    var legacyConfig = JsonNode.Parse(File.ReadAllText(Path.Combine(legacy.Package(0), "override.json")))!.AsObject();
    legacyConfig["GameDirectory"] = legacy.Game;
    legacyConfig["SourcePath"] = Path.Combine(legacy.Package(0), "replacement.d");
    legacyConfig["StateDirectory"] = legacyDirectory;
    string legacyPath = Path.Combine(legacy.Root, "legacy.json");
    File.WriteAllText(legacyPath, legacyConfig.ToJsonString());
    Run(["apply", "--config", legacyPath]);
    var oldState = legacy.StateJson(0);
    foreach (string field in new[] { "OriginalMd5", "OriginalSize", "OriginalCrc", "RestoreRequired" }) oldState.Remove(field);
    legacy.WriteState(0, oldState);
    Cli(legacy, "apply");
    Check(Directory.GetDirectories(legacy.State).Single() == legacyDirectory, "Legacy recovery state not reused.");
    legacy.Install(1);
    Cli(legacy, "apply");
    Run(["restore", "--state-root", legacy.State, "--asset", legacy.Names[0]]);
    Check(legacy.Store().TryGetManifest("manifest.test", "/list", out var oldMixed), "Legacy mixed manifest absent.");
    Check(Md5(oldMixed, legacy.Names[0]) == legacy.OriginalMd5[0], "Legacy original fallback failed.");
    Cli(legacy, "restore");

    var changed = new Fixture(Path.Combine(root, "changed manifest"));
    changed.Install(0);
    Cli(changed, "apply");
    var extra = ProtobufWire.Parse(OctoCrypto.DecryptSecureFile(changed.OriginalManifest));
    extra.Add(ProtoField.Varint(99, 123));
    byte[] managed = OctoCrypto.EncryptSecureFile(ProtobufWire.Encode(extra));
    File.WriteAllBytes(changed.Manifest, managed);
    Cli(changed, "restore");
    Check(File.ReadAllBytes(changed.Manifest).SequenceEqual(managed), "Game-managed manifest overwritten.");
    Check(File.Exists(changed.OriginalBlob(0)), "Game-managed manifest restore left original blob missing.");
    Cli(changed, "apply");
    Cli(changed, "restore");
    Check(File.ReadAllBytes(changed.Manifest).SequenceEqual(managed), "Reapplication lost unrelated manifest fields.");

    var loose = new Fixture(Path.Combine(root, "loose"));
    Directory.CreateDirectory(loose.Packages);
    File.WriteAllText(Path.Combine(loose.Packages, "loose.json"), "{}");
    Cli(loose, "status", success: false);
    File.Delete(Path.Combine(loose.Packages, "loose.json"));
    Directory.CreateDirectory(Path.Combine(loose.Packages, "missing-manifest"));
    Cli(loose, "status", success: false);
    Run(["status", "--packages", loose.Packages, "--state-root", Path.Combine(loose.Packages, "state")], false);
    Run(["status", "--packages"], false);
    Run(["restore", "--state-root", loose.State, "--asset", "not-tracked"], false);
    string fileRoot = Path.Combine(loose.Root, "root-file");
    File.WriteAllText(fileRoot, "");
    Run(["status", "--packages", fileRoot, "--state-root", loose.State], false);
    Run(["restore", "--state-root", fileRoot], false);

    var wrappers = new Fixture(Path.Combine(root, "wrapper [paths] ' test"));
    wrappers.Install(0);
    string wrapperScript = Path.Combine(repository.FullName, "tests", "Ff7ec.AssetOverride.Checks", "Check-Launchers.ps1");
    foreach (string shell in new[] { "pwsh", "powershell.exe" })
    {
        string result = Execute(shell, ["-NoProfile", "-File", wrapperScript, "-Repository", repository.FullName,
            "-FixtureRoot", wrappers.Root, "-Tool", tool]);
        Check(result.Contains("Launcher checks passed"), $"Launcher verification failed in {shell}.");
    }
    Console.WriteLine($"Passed {checks} synthetic override checks.");
}
finally
{
    Directory.Delete(root, true);
}

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
void Cli(Fixture fixture, string action, bool game = true, bool success = true)
{
    var arguments = new List<string> { action, "--state-root", fixture.State };
    if (action != "restore") arguments.AddRange(["--packages", fixture.Packages]);
    if (game) arguments.AddRange(["--game", fixture.Game]);
    Run(arguments.ToArray(), success);
}
void Run(string[] arguments, bool success = true)
{
    Execute("dotnet", new[] { tool }.Concat(arguments).ToArray(), success);
    checks++;
}
string Execute(string executable, string[] arguments, bool success = true)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    start.Environment.Remove("FF7EC_GAME_DIRECTORY");
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start check process.");
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
    if ((process.ExitCode == 0) != success)
        throw new InvalidOperationException($"Unexpected exit {process.ExitCode}: {string.Join(' ', arguments)}\n{output}");
    return output;
}
static string Md5(byte[] payload, string name)
{
    var item = ProtobufWire.Parse(payload).Where(field => field.Number == 2)
        .Select(field => ProtobufWire.Parse(field.Value))
        .Single(fields => Encoding.UTF8.GetString(fields.Single(field => field.Number == 3).Value) == name);
    return Encoding.ASCII.GetString(item.Single(field => field.Number == 10).Value);
}
static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

sealed class Fixture
{
    public string Root { get; }
    public string Game => Path.Combine(Root, "game");
    public string Packages => Path.Combine(Root, "packages");
    public string State => Path.Combine(Root, "state");
    public string Manifest => Path.Combine(Game, "octo", "pdb", "octocacheevai");
    public string[] Names { get; } = ["character/003/model/019.d", "character/006/model/008.d"];
    public string[] OriginalMd5 { get; } = new string[2];
    public byte[] OriginalManifest { get; }
    private readonly byte[][] _originals = [Encoding.ASCII.GetBytes("UnityFS\0original-0"), Encoding.ASCII.GetBytes("UnityFS\0original-1")];
    private readonly byte[] _bucket = [0, 1, 2, 3, 4, 5, 6, 7];

    public Fixture(string root)
    {
        Root = root;
        var items = new List<ProtoField> { ProtoField.Varint(1, 123) };
        for (int i = 0; i < 2; i++)
        {
            OriginalMd5[i] = Hex(MD5.HashData(_originals[i]));
            items.Add(ProtoField.LengthDelimited(2, ProtobufWire.Encode([
                ProtoField.LengthDelimited(3, Encoding.UTF8.GetBytes(Names[i])),
                ProtoField.Varint(4, (ulong)_originals[i].Length), ProtoField.Varint(5, (ulong)(123 + i)),
                ProtoField.LengthDelimited(10, Encoding.ASCII.GetBytes(OriginalMd5[i])),
                ProtoField.LengthDelimited(11, Encoding.ASCII.GetBytes($"object-{i}")),
                ProtoField.Varint(12, 456), ProtoField.LengthDelimited(13, [7, 8, 9])
            ])));
            Directory.CreateDirectory(Path.GetDirectoryName(OriginalBlob(i))!);
            File.WriteAllBytes(OriginalBlob(i), _originals[i]);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(OriginalBlob(i))!, ".meta"), _bucket);
        }
        OriginalManifest = OctoCrypto.EncryptSecureFile(ProtobufWire.Encode(items));
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        File.WriteAllBytes(Manifest, OriginalManifest);
    }
    public string OriginalBlob(int index) => Path.Combine(Game, "octo", "v1", "bucket-" + index, OriginalMd5[index]);
    public string Package(int index) => Path.Combine(Packages, "package-" + index);
    public void Install(int index)
    {
        Directory.CreateDirectory(Package(index));
        byte[] replacement = Encoding.ASCII.GetBytes("UnityFS\0synthetic replacement " + index);
        File.WriteAllBytes(Path.Combine(Package(index), "replacement.d"), replacement);
        File.WriteAllText(Path.Combine(Package(index), "override.json"), JsonSerializer.Serialize(new
        {
            AssetName = Names[index], ObjectName = "object-" + index,
            SourceSha256 = Hex(SHA256.HashData(replacement)),
            OriginalMd5 = OriginalMd5[index], OriginalSize = _originals[index].Length,
            OriginalCrc = 123 + index, OriginalBucketMetaHex = Hex(_bucket), ReplacementCrc = 987 + index
        }));
    }
    public JsonObject StateJson(int index) => JsonNode.Parse(File.ReadAllText(StateFile(index)))!.AsObject();
    public void WriteState(int index, JsonObject value) => File.WriteAllText(StateFile(index), value.ToJsonString());
    private string StateFile(int index) => Directory.GetDirectories(State).Select(directory => Path.Combine(directory, "state.json"))
        .Where(File.Exists)
        .Single(path => JsonNode.Parse(File.ReadAllText(path))!["AssetName"]!.GetValue<string>() == Names[index]);
    public LocalAssetOverrideStore Store() => new(NullLogger<LocalAssetOverrideStore>.Instance, State,
        "manifest.test", "/list", "assets.test");
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
