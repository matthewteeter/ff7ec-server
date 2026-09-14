# FF7EC Offline Server

A personal, non-commercial offline replay server for **Final Fantasy VII Ever Crisis**,
built because the real game servers are shutting down. It does not understand or
re-implement the game's business logic - it plays back exactly what the real servers
once returned for the same request, captured while they were still live.

## How it works

1. **Capture** (while the real servers are up): [mitmproxy](https://mitmproxy.org/) +
   a custom addon (`../FF7EC_Preservation/work/mitm_ff7ec_addon.py`) intercept the game's
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

`POST /api/pvt/story/select/drama` is also acknowledged with a generated secure response.
Its API response is empty and does not update user tables, so the selection is not added
to the settings store; acknowledging it lets the current story dialogue continue.

## Scope: "boot + roam"

This targets booting into the game and browsing already-downloaded content (menus,
roster, inventory) using the account's real captured data - not battles, gacha, or any
flow that requires the server to react to new player actions with novel responses.
Extending coverage just means capturing more real traffic and re-running the importer;
no code changes needed for that.

## Layout

```
Ff7ec.Server.sln
src/Ff7ec.Server/       - the replay server (see Program.cs)
tools/export_capture_store.py  - .mitm -> captures/ importer
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

## Running it

**One-click (recommended):**
```powershell
E:\FF7EC-Server\launcher\Start-Ff7ecOffline.ps1 [-LaunchGame]
```
Starts the server (generating certs on first run), then prompts once for elevation to
install the CA into Windows Trusted Root and redirect the tracked hostnames to
`127.0.0.1` in the hosts file. Pass `-LaunchGame` to also launch FF7EC via Steam
afterward.

```powershell
E:\FF7EC-Server\launcher\Stop-Ff7ecOffline.ps1
```
Removes the hosts redirect (one more elevation prompt) so the machine can reach the real
servers again - useful while both this tool and the real servers still exist. The CA is
left installed (harmless) and the server window must be closed manually.

**Manual:**
```powershell
cd E:\FF7EC-Server\src\Ff7ec.Server
dotnet run
```
Then separately install `certs\ff7ec-offline-ca.cer` into `Cert:\LocalMachine\Root` and
add hosts file entries for the hostnames listed in `appsettings.json` -> `Ff7ec:Hostnames`.

## Importing a new capture

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

- `E:\FF7EC_Preservation\work\capture_final_24813881.mitm`
- `E:\FF7EC_Preservation\work\capture_final_24813881_second.mitm`

They contain private account/session traffic and should not be published. The imported
response store is likewise intended only for the account that produced the capture.

### Final IL2CPP dump

The final-build dump is at
`E:\FF7EC_Preservation\work\il2cpp_dump_final_24813881\` and contains `dump.cs` plus
157 `DummyDll` files (156 assemblies and `__Generated`). The source memory image was
captured from the live process at base `0x7ff92fa20000` with zero unreadable pages.
See `DUMP_NOTES.txt` in that directory for hashes and exact build metadata.

### No-admin validation path

For validation, machine-wide hosts changes were avoided. The final client was launched
through Steam and `E:\FF7EC_Preservation\work\redirect_dns.js` was injected with Frida;
it redirects only the four known FF7EC API host resolutions/connections to localhost.
The offline CA was installed in the current user's root store with:

```powershell
certutil.exe -user -addstore -f Root E:\FF7EC-Server\certs\ff7ec-offline-ca.cer
```

The regular `Start-Ff7ecOffline.ps1` launcher remains the simpler permanent setup when an
administrator can approve its hosts-file update. The Frida path is useful for testing
without changing system-wide DNS resolution.
