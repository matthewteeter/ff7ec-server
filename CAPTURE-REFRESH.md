# Refreshing the Offline Replay Data

Use this procedure while the official FF7EC servers are still available to
replace the offline server's replay data with the latest state of your account.

The capture pipeline stores two forms of data:

- Raw, complete mitmproxy captures under
  `E:\FF7EC_Preservation\captures\<timestamp>\`. These contain private
  account, Steam, device, token, and session data.
- Replay-ready records with request headers omitted under
  `E:\FF7EC-Server\captures\`. The C# server loads these files at startup.

Importing a new capture is additive. A later response replaces an earlier one
only when its `(host, method, path+query)` replay key is identical. **Do not
import until the capture contains a fresh `POST /api/pvt/user/title` response.**
That is the account snapshot the offline server uses for characters, costumes,
crystals, and other account state. An empty `gaps/` directory is not proof of
freshness: old responses can still satisfy every request.

## 1. Stop offline mode

Close FF7EC, the replay-server window, and any old mitmdump window. If the
offline launcher previously changed the Windows hosts file, remove its
redirects:

```powershell
E:\FF7EC-Server\launcher\Stop-Ff7ecOffline.ps1
```

This may request elevation. Do not capture while the ASP.NET replay server is
still using port 443.

## 2. Trust the mitmproxy CA

Live capture uses mitmproxy's CA, not the offline server's
`ff7ec-offline-ca.cer`. Install it in the current user's trusted root store.
This is a one-time command and does not require administrator privileges:

```powershell
certutil.exe -user -addstore -f Root "$HOME\.mitmproxy\mitmproxy-ca-cert.cer"
```

## 3. Create a private capture session

Run the following in PowerShell:

```powershell
$stamp = Get-Date -Format yyyyMMdd_HHmmss
$session = "E:\FF7EC_Preservation\captures\$stamp"
New-Item -ItemType Directory -Force $session | Out-Null
Copy-Item E:\FF7EC-Server\captures "$session\replay-store-before" -Recurse
$rawCapture = "$session\ff7ec-progress-refresh-$stamp.mitm"
$env:FF7EC_CAPTURE_STORE_OUT = "$session\staged-replay"
Write-Host "Raw capture: $rawCapture"
```

The copied replay store is a rollback snapshot. Keep the value printed for
`$rawCapture`; it is needed for the final import. The addon will write
replay-ready responses to `staged-replay`, **not** the active server store;
only the validated import in step 7 updates the active store.

## 4. Start the live capture proxy

In the same PowerShell window, run mitmdump and leave it open:

```powershell
mitmdump `
  --mode "reverse:https://game-q74z3cyn.app.gl.ffviiec.com@443" `
  --set keep_host_header=true `
  --ssl-insecure `
  --scripts E:\FF7EC_Preservation\work\mitm_ff7ec_addon.py `
  --save-stream-file $rawCapture
```

The addon forwards requests to the official hosts through their real IP
addresses. It also writes replay-ready records into the private staging folder
as responses arrive.

## 5. Redirect FF7EC to the capture proxy

**Reliable method (requires approval for an administrator prompt):** in a
second, **elevated** PowerShell window, enable the dedicated live-capture
hosts-file block *before* launching the game:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  E:\FF7EC-Server\launcher\Set-Ff7ecCaptureRouting.ps1 -Action Enable
```

This backs up the original hosts file privately and redirects only the five
configured game hostnames while capture is active. It does **not** start the
offline server or install its CA. Leave mitmdump on port 443. Launch the game
from another window:

```powershell
Start-Process 'steam://rungameid/2484110'
```

When finished with live capture, close the game and disable capture routing
in the elevated PowerShell window **before** starting offline mode:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  E:\FF7EC-Server\launcher\Set-Ff7ecCaptureRouting.ps1 -Action Disable
```

**No-admin fallback:** launch through Steam and attach Frida as soon as the
process appears in a second PowerShell window:

```powershell
if (Get-Process FF7EC -ErrorAction SilentlyContinue) {
  throw "Close FF7EC before starting a new capture."
}
Start-Process 'steam://rungameid/2484110'
do {
  $game = Get-Process FF7EC -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $game) { Start-Sleep -Milliseconds 100 }
} until ($game)
$gamePid = $game.Id
frida -p $gamePid -l E:\FF7EC_Preservation\work\redirect_dns.js
```

Do not use `frida -W FF7EC.exe` on Windows: its spawn-gating mode reports
`Failed to enable spawn gating: not yet supported on this OS` and does not
install the hook. Steam must launch the game; Frida then attaches by PID.

Wait for Frida to report:

```text
[redirect] FF7EC DNS/connect hooks installed
```

Successful traffic should produce messages such as the following in the
mitmdump window:

```text
[ff7ec] exported POST ...
```

Wait for the hook before continuing past the title screen. **Frida attaches
after process start:** if boot/login or title requests happen earlier, they
will bypass mitmdump even though the hook subsequently reports "installed".
Do not assume the hook message alone means that the account was captured.
If `/api/pvt/user/title` is absent in mitmdump, use the reliable routing
method on the next attempt.

## 6. Exercise the live account

At minimum, continue from the title screen to Home. The
`POST /api/pvt/user/title` response contains the large account snapshot.

Visit every screen whose latest server-side data should be retained. Useful
coverage includes:

- Characters, weapons, inventory, and Growth
- Solo and co-op parties
- Missions, gifts, notices, and season-pass screens
- Story, events, expeditions, and guild
- Shop and other menus that load data lazily

Opening Home refreshes a large portion of account state, but visiting
individual screens captures any additional endpoints they request. Avoid
purchases, currency spending, reward claims, or other unwanted state-changing
actions.

When finished:

1. Exit FF7EC normally.
2. If using Frida, press **Ctrl+C** in its window.
3. Press **Ctrl+C** in the mitmdump window.
4. If using the hosts-file method, run the `-Action Disable` command above.

Do not start the offline server until mitmdump has released port 443 and
live-capture routing has been disabled.

## 7. Verify, then import the completed raw capture

Use the actual timestamped path printed in step 3. **Verify without changing
the replay store first:**

```powershell
$rawCapture = "E:\FF7EC_Preservation\captures\YYYYMMDD_HHMMSS\ff7ec-progress-refresh-YYYYMMDD_HHMMSS.mitm"
python E:\FF7EC-Server\tools\export_capture_store.py `
  $rawCapture --check-only --require-user-title
if ($LASTEXITCODE -ne 0) { throw "Account snapshot missing; do not import this capture." }
```

The check must report at least one successful account snapshot and a timestamp
from this capture session. If it reports zero, your catalog requests were
captured but your current account state was **not**. Restart live capture
with routing enabled *before* the Steam launch, then repeat.

When verification succeeds, import into the active replay store:

```powershell
python E:\FF7EC-Server\tools\export_capture_store.py `
  $rawCapture --require-user-title --out E:\FF7EC-Server\captures
if ($LASTEXITCODE -ne 0) { throw "Import failed; leave the offline server stopped." }
```

Review the most recently refreshed records:

```powershell
Get-ChildItem E:\FF7EC-Server\captures -Recurse -Filter *.meta.json |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 30 FullName, LastWriteTime
```

Do not remove older replay records merely because they were not requested in
this session. They may provide coverage for screens that were not revisited.

## 8. Handle local writable overlays

The replay server may have local offline changes in:

```text
E:\FF7EC-Server\data\party-settings.json
E:\FF7EC-Server\data\story-state.json
```

Those overlays intentionally take precedence over corresponding values in the
captured account snapshot. To test exactly what the official server returned,
move the files to a timestamped private backup:

```powershell
$backup = "E:\FF7EC_Preservation\state-backups\$(Get-Date -Format yyyyMMdd_HHmmss)"
New-Item -ItemType Directory -Force $backup | Out-Null
$stateFiles = @(
  "E:\FF7EC-Server\data\party-settings.json"
  "E:\FF7EC-Server\data\story-state.json"
)
Get-Item $stateFiles -ErrorAction SilentlyContinue |
  Move-Item -Destination $backup
```

This step is optional. Keep the local files if the offline party, wallpaper,
or story selections should continue overriding the newly captured values.
These files do not explain stale crystals or costume ownership; for those,
verify the timestamp of the captured title response in step 7.
Leave `data\asset-overrides` in place.

## 9. Validate offline replay

Start offline mode and launch the game:

```powershell
E:\FF7EC-Server\launcher\Start-Ff7ecOffline.ps1 -LaunchGame
```

Browse the same screens used during capture and inspect:

```text
E:\FF7EC-Server\gaps
```

An empty gaps directory means every request made during that validation run
matched a replay record; it does **not** prove that the record is new. Check
the title snapshot's `capturedAt` value, and compare the actual crystals and
costumes visible in the client. A gap identifies an endpoint that must be
exercised during another live capture.

When validation is complete:

```powershell
E:\FF7EC-Server\launcher\Stop-Ff7ecOffline.ps1
```

## Privacy and source control

Never publish either the raw `.mitm` files or imported replay records. They are
account-specific and can contain private identifiers, tokens, hashes, and
encrypted account state. Keep raw captures and rollback snapshots under
`E:\FF7EC_Preservation`.

The repository `.gitignore` excludes `captures/`, `data/`, `certs/`, `gaps/`,
and `*.mitm`. Do not bypass those exclusions with `git add --force`.
