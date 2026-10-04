# FF7EC Offline Server

A personal, non-commercial offline replay server for **Final Fantasy VII Ever Crisis**,
built because the real game servers are shutting down. It does not understand or
re-implement the game's business logic - it plays back exactly what the real servers
once returned for the same request, captured while they were still live.

## How it works

1. **Capture** (while the real servers are up): [mitmproxy](https://mitmproxy.org/) +
   a custom addon (`tools/mitm_ff7ec_addon.py`) intercept the game's
   HTTPS traffic via a hosts-file redirect, recording full request/response pairs to a
   `.mitm` file.
2. **Import**: `tools/export_capture_store.py` converts a `.mitm` capture into this repo's
   `captures/` folder format - one `.meta.json` (status, headers, path) + `.body.bin`
   (raw response bytes) pair per unique `(host, method, path+query)`.
3. **Replay**: `src/Ff7ec.Server` is a small ASP.NET Core/Kestrel app that terminates TLS
   itself (using a self-generated, self-signed CA - see `certs/`) and serves back the
   captured response, verbatim, for any request matching a captured key.
4. **Writable user settings**: solo/co-op party upserts and home-wallpaper changes are
   handled before replay lookup. Their encrypted protobuf request bodies are persisted
   to `data/party-settings.json`, then acknowledged with a generated encrypted cache
   update. Later incoming replay requests are rewritten from that local state before
   their response is returned to the game.

   Story-mode drama selections and milestone results are also persisted to
   `data/story-state.json`. Drama writes immediately update the client's story-selection
   table, and later account snapshots are overlaid with each selection's latest choice.
   This prevents chapter 7/8 branches from reverting to the choices in the old capture.

### Why verbatim replay is enough (no decryption needed)

The real API wraps request/response bodies in an application-layer encryption scheme on
top of TLS (`x-content-encoding-secure`, `x-content-hash`, a rotating `x-token`) that has
not been reverse-engineered. This turns out not to matter for replay: because the server
sends back the **exact bytes and exact headers** the real server sent for that call, any
client-side integrity check the game performs still passes - nothing was tampered with,
it's the same (body, hash) pair the client already saw once during capture. The tradeoff
is that this server cannot generate *new* responses to reflect state changes (completing
a quest, spending currency, etc.) - see Scope below.

### Writable-setting limits

`POST /api/pvt/party/multi/set/upsert`, `POST /api/pvt/party/solo/set/upsert`, and
`POST /api/pvt/user/home/background/setting` are narrow exceptions to read-only replay.
The server stores each accepted request's exact bytes losslessly as Base64, together with
its `x-content-hash`, host, route, user ID, and timestamp. The write is acknowledged with
an immediate client-cache update, then later full user-snapshot replay requests are
decoded and rewritten from the latest local state. The overlay replaces matching party,
party-member, and home-background-setting records in the replayed user tables. Smaller
boot-time responses are left byte-for-byte unchanged. No other game state is changed.

Writes are serialized and the JSON file is replaced atomically after its temporary file
has been flushed to disk. An immediate retry with the same hash and payload as the latest
record for that host/endpoint/user is acknowledged without appending another copy.
Malformed existing JSON is treated as an error rather than discarded. The storage
location is configured by `Ff7ec:DataDirectory`.

Because the client rejects an unencrypted empty response, successful writes reuse the
secure headers from the captured `POST /api/pvt/store/purchase/restart/steam` response.
The server generates a fresh encrypted protobuf body whose `CommonResponse.User.Update`
contains the changed setting rows, with party timestamps capped to the captured replay
clock. This refreshes party and wallpaper screens without triggering the game's daily
rollover check or requiring a restart. That capture must be present for the same user ID;
otherwise the write remains on disk but the server returns HTTP 503.

`POST /api/pvt/dungeon/story/start`, `POST /api/pvt/dungeon/story/end`,
`POST /api/pvt/story/battle/start`, `POST /api/pvt/story/battle/end`,
`POST /api/pvt/character/story/battle/start`, `POST /api/pvt/character/story/battle/end`,
`POST /api/pvt/event/solo/battle/start`, `POST /api/pvt/event/solo/battle/end`,
`POST /api/pvt/story/select/drama`, `POST /api/pvt/story/result`, and
`POST /api/pvt/character/story/result` are also acknowledged with generated secure
responses. They return only the minimal endpoint payload required by the client and do
not update user tables here, so they are not added to the settings store. This lets
replayed story dungeons, battles, dialogue, and already-completed episodes continue
without a captured write.

## Scope: "boot + roam"

This targets booting into the game and browsing already-downloaded content (menus,
roster, inventory) using the account's real captured data - not battles, gacha, or any
flow that requires the server to react to new player actions with novel responses.
Extending coverage just means capturing more real traffic and re-running the importer;
no code changes needed for that.

## Layout

```
Ff7ec.Server.slnx
src/Ff7ec.Octo/         - shared Octo manifest encryption/decryption
src/Ff7ec.Server/       - the replay server (see Program.cs)
tools/export_capture_store.py  - .mitm -> captures/ importer
tools/mitm_ff7ec_addon.py - code-only capture add-on (no embedded account data or keys)
captures/{host}/        - captured response store (see below)
certs/                  - auto-generated CA + leaf cert (created on first run)
gaps/                   - requests with no captured response are logged here for later capture
data/                   - writable opaque party-setting history
launcher/               - one-click scripts (see below)
```

## Source-control safety

Do **not** commit the local `captures/`, `certs/`, `gaps/`, or `data/` directories.
Captures and writable data contain an account-specific numeric user ID, hashes, and
encrypted account-state payloads. The `.pfx` files contain private TLS keys. A root
`.gitignore` excludes these, along with build output and logs. The C# source, launcher
scripts, importer, solution, and README are safe to commit after confirming ignored files
are not force-added.

A clone of the source repository will therefore not contain a playable account snapshot;
each user must privately import their own capture and let the server generate local
certificates.

`src\Ff7ec.Octo` preserves the existing game-wide manifest app key and IV seed,
not account credentials or TLS private keys. Both the server and asset-override
tool use this in-repository project; no CostumeViewer checkout or live service is
needed for manifest encryption/decryption. The library supports the encrypted
AES-with-MD5 SecureFile format used by the override tool's manifest backups and
retains the server's raw SecureFile reading support. The override tool still
requires encrypted manifests.

## Running it

Run commands from the repository root; the repository can be cloned to any drive.
Server storage paths in `appsettings.json` are relative to `src\Ff7ec.Server`, not
the shell's current directory. The launcher resolves its certificate path the same way.
The importer defaults to this repository's `captures` folder.

Private preservation storage is selected automatically by
`launcher\Get-Ff7ecPreservationRoot.ps1`: it tries `E:\FF7EC_Preservation`, then
`C:\FF7EC_Preservation`, then `%LOCALAPPDATA%\FF7EC_Preservation`. Missing drives
are skipped; unwritable locations produce a warning before trying the next folder.
To choose another writable folder, set `FF7EC_PRESERVATION_ROOT` in your user
environment or in every PowerShell window used for capture. An unwritable explicit
choice fails rather than silently storing private data elsewhere. Capture and backup
scripts still accept their explicit `-CaptureRoot`, `-SessionDirectory`, and
`-BackupRoot` parameters. The selected preservation root must be outside the
source repository. Relative `-CaptureRoot` paths are normalized before session
manifests are written, so the finish script can locate the same files.

**One-click (recommended):**
```powershell
.\launcher\Start-Ff7ecOffline.ps1 [-LaunchGame]
```
Starts the server (generating certs on first run), then prompts once for elevation to
install the CA into Windows Trusted Root and redirect the tracked hostnames to
`127.0.0.1` in the hosts file. Pass `-LaunchGame` to also launch FF7EC via Steam
afterward.

```powershell
.\launcher\Stop-Ff7ecOffline.ps1
```
Removes the hosts redirect (one more elevation prompt) so the machine can reach the real
servers again - useful while both this tool and the real servers still exist. The CA is
left installed (harmless) and the server window must be closed manually.

### Tifa 019 local asset override

The launcher is configured to replace `character/003/model/019.d` with the finished local
bundle at:

```text
<preservationRoot>\viewerdata\bundles\character\003\model\019.d
```

The launcher expands `%FF7EC_PRESERVATION_ROOT%` in the configured source paths.
It discovers FF7EC in Steam's registered libraries; set `FF7EC_GAME_DIRECTORY`
to override that discovery. Custom absolute paths in the JSON remain supported;
relative game and source paths are resolved against the configuration file's folder.
When invoking the asset-override .NET tool directly, set both environment variables
first. Use the same preservation root as the costume viewer; existing data is not
copied or moved automatically.

`Start-Ff7ecOffline.ps1` applies this override before starting the server. The override tool:

- refuses to run while `FF7EC.exe` is open;
- verifies the source bundle's registered SHA-256;
- wraps the plaintext UnityFS bundle using FF7EC's Octo XOR format;
- preserves the asset's identity, object name, dependencies, and generation;
- updates only its local manifest size, Unity CRC, and MD5 fields;
- keeps the original manifest, bucket metadata, and cache blob under `data\asset-overrides\tifa-019`;
- makes the replay server return the patched full manifest and serve the replacement bundle
  from the original asset host if the client needs to download it again.

`Stop-Ff7ecOffline.ps1` restores the originals before removing the hosts redirect. Manual
control and status checks are also available:

```powershell
.\launcher\Set-Ff7ecAssetOverride.ps1 Status
.\launcher\Set-Ff7ecAssetOverride.ps1 Apply
.\launcher\Set-Ff7ecAssetOverride.ps1 Restore
```

Use `Start-Ff7ecOffline.ps1 -SkipAssetOverrides` to launch without applying it, or
`Stop-Ff7ecOffline.ps1 -KeepAssetOverrides` to leave it installed. Configuration and the
registered hashes are in `launcher\asset-overrides.json`. If the model is edited again, its
SHA-256 and Unity AssetBundle CRC must be re-measured before changing that file; this prevents
an unverified bundle from being installed into the client cache.

**Manual:**
```powershell
cd .\src\Ff7ec.Server
dotnet run
```
Then separately install `..\..\certs\ff7ec-offline-ca.cer` into `Cert:\LocalMachine\Root` and
add hosts file entries for the hostnames listed in `appsettings.json` -> `Ff7ec:Hostnames`.

## Importing a new capture

For the complete live-server refresh procedure, including mitmproxy, Frida,
private storage, import, writable-overlay handling, and offline validation, see
[CAPTURE-REFRESH.md](CAPTURE-REFRESH.md). It uses the PowerShell scripts under
`launcher/` to create a fresh capture session and verify its account snapshot
before importing; no capture paths or session variables need to be copied
between terminal windows.

For the capture checklist, continue to Home until it has loaded, then open the
co-op party screen and wait for it to load before exiting the game. Home alone
refreshes the title account snapshot, but does not establish co-op party coverage:
offline testing of a Home-only capture encountered a missing
`POST /api/pvt/notice/check` response when opening co-op party. Browsing other
screens is optional and captures extra endpoint coverage. The finish script
verifies the fresh title account snapshot before importing; separately validate
Home and co-op party offline and check for new replay gaps.

```powershell
python tools\export_capture_store.py path\to\capture.mitm --out captures
```
Safe to re-run after every capture session - later captures overwrite the stored
response for the same `(host, method, path+query)` key, so this only ever adds or
refreshes coverage. Restart the server (or re-run `dotnet run`) to pick up new captures.

## Filling gaps

Any request the server doesn't have a captured response for is logged to `gaps/` (full
headers + body) and returns HTTP 404. Check that folder to see what's still missing,
then capture more real traffic covering those calls before the real servers disappear.

## Current status (2026-09-11)

The final Steam client (**version 4.0.0, build 24813881**) has been captured and validated
against this server. The capture store now contains **12 responses** across three hosts:

- Main API: boot check, authentication/session, title/account snapshot, notice check,
  announcements, and Steam purchase recovery.
- Octo API: initial manifest, final revision **366** delta, and revision lookup.
- Master data: the final global and English content-addressed JSON catalogs.

End-to-end validation succeeded with the real final client: cold boot, title-to-home, and
Growth -> Characters -> Character Stream all worked against the local server with **zero
replay gaps**. The application-layer encrypted API responses were replayed unchanged.

The definitive raw captures are stored outside this repo at:

- `<preservationRoot>\work\capture_final_24813881.mitm`
- `<preservationRoot>\work\capture_final_24813881_second.mitm`

They contain private account/session traffic and should not be published. The imported
response store is likewise intended only for the account that produced the capture.

### Final IL2CPP dump

The final-build dump is at
`<preservationRoot>\work\il2cpp_dump_final_24813881\` and contains `dump.cs` plus
157 `DummyDll` files (156 assemblies and `__Generated`). The source memory image was
captured from the live process at base `0x7ff92fa20000` with zero unreadable pages.
See `DUMP_NOTES.txt` in that directory for hashes and exact build metadata.

### No-admin validation path

For validation, machine-wide hosts changes were avoided. The final client was launched
through Steam and `<preservationRoot>\work\redirect_dns.js` was injected with Frida;
it redirects only the five configured FF7EC API/asset host resolutions and connections to localhost.
The offline CA was installed in the current user's root store with:

```powershell
certutil.exe -user -addstore -f Root .\certs\ff7ec-offline-ca.cer
```

The regular `Start-Ff7ecOffline.ps1` launcher remains the simpler permanent setup when an
administrator can approve its hosts-file update. The Frida path is useful for testing
without changing system-wide DNS resolution.
