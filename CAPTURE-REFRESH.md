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
This is a one-time script and does not require administrator privileges.
Only trust a mitmproxy CA that belongs to your own installation:

```powershell
& E:\FF7EC-Server\launcher\Trust-Ff7ecCaptureCa.ps1
```

## 3. Start a fresh capture

In a regular Windows PowerShell window, run this script and leave it open:

```powershell
& E:\FF7EC-Server\launcher\Start-Ff7ecLiveCapture.ps1
```

It creates a timestamped directory under `E:\FF7EC_Preservation\captures`,
backs up the current replay store to `replay-store-before`, sets the staging
destination **in the mitmdump process**, and starts mitmdump on port 443.
It prints the full paths to `capture.mitm` and `staged-replay`. No session
variables have to persist between commands or PowerShell windows. The addon
routes the captured requests to the real servers; no live responses go to
the active replay store until the verified import in step 6.

## 4. Redirect FF7EC to the capture proxy

**Reliable method (requires approval for an administrator prompt):** in a
second, **elevated** PowerShell window, enable the dedicated live-capture
hosts-file block *before* launching the game:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  E:\FF7EC-Server\launcher\Set-Ff7ecCaptureRouting.ps1 -Action Enable
```

This backs up the original hosts file privately and redirects only the five
configured game hostnames while capture is active. It does **not** start the
offline server or install its CA. With mitmdump still running, launch the
game from a third, regular PowerShell window:

```powershell
& E:\FF7EC-Server\launcher\Launch-Ff7ecLiveGame.ps1
```

The launch script checks that routing is active and port 443 is listening.
When finished with live capture, close the game and disable capture routing
in the elevated PowerShell window **before** starting offline mode:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  E:\FF7EC-Server\launcher\Set-Ff7ecCaptureRouting.ps1 -Action Disable
```

**No-admin fallback:** if you cannot approve the hosts-file change, start
the capture script in step 3, then run this in a second PowerShell window:

```powershell
& E:\FF7EC-Server\launcher\Launch-Ff7ecLiveGame.ps1 -UseFrida
```

Do not use `frida -W FF7EC.exe` on Windows: its spawn-gating mode reports
`Failed to enable spawn gating: not yet supported on this OS` and does not
install the hook. The launch script starts Steam, then attaches Frida by PID;
leave its window open while playing.

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

## 5. Exercise the live account

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

## 6. Verify, then import the completed raw capture

With mitmdump stopped and live-capture routing disabled, run:

```powershell
& E:\FF7EC-Server\launcher\Finish-Ff7ecLiveCapture.ps1
```

By default it selects the newest session **created by the start script**,
not an old `$rawCapture` variable. You can explicitly select a session with
`-SessionDirectory 'E:\FF7EC_Preservation\captures\YYYYMMDD_HHMMSS'`.
The finish script verifies the raw `.mitm` and that a successful nonempty
`POST /api/pvt/user/title` response was staged **after this session started**
before it imports anything. It then verifies that the imported account
snapshot matches the staged response. If it reports zero account snapshots,
your current account state was not captured; do not use that capture.

Do not remove older replay records merely because they were not requested in
this session. They may provide coverage for screens that were not revisited.

## 7. Handle local writable overlays

The replay server may have local offline changes in:

```text
E:\FF7EC-Server\data\party-settings.json
E:\FF7EC-Server\data\story-state.json
```

Those overlays intentionally take precedence over corresponding values in the
captured account snapshot. To test exactly what the official server returned,
move them to a timestamped private backup with:

```powershell
& E:\FF7EC-Server\launcher\Backup-Ff7ecLocalState.ps1
```

This step is optional. Keep the local files if the offline party, wallpaper,
or story selections should continue overriding the newly captured values.
These files do not explain stale crystals or costume ownership; for those,
verify the timestamp of the captured title response in step 6.
Leave `data\asset-overrides` in place.

## 8. Validate offline replay

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
