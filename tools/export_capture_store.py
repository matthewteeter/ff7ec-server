"""
Converts one or more mitmproxy .mitm capture files into the FF7EC-Server
CaptureStore folder format the C# replay server reads at startup.

CaptureStore layout (under --out, default the repository's captures directory):
    captures/
      {host}/
        {METHOD}_{slug}_{hash8}.meta.json   -- status, headers, pathAndQuery, capturedAt, sourceFlow
        {METHOD}_{slug}_{hash8}.body.bin    -- raw response body bytes

Only real game traffic is imported (User-Agent containing "UnityPlayer"); manual
curl smoke-test flows are skipped. Later captures overwrite earlier ones for the
same (host, method, pathAndQuery) key, so re-running after a richer capture is
safe and additive - it never loses previously-captured breadth unless a newer
capture legitimately updates the same endpoint.

Usage:
    python export_capture_store.py capture.mitm --check-only --require-user-title
    python export_capture_store.py capture.mitm --require-user-title [--out DIR]
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import re
import sys
from pathlib import Path

from mitmproxy import io as mitm_io
from mitmproxy.exceptions import FlowReadException


def slugify(path: str) -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "_", path).strip("_")
    return slug[:60] if slug else "root"


def export_flow(flow, out_root: Path, stats: dict) -> None:
    req = flow.request
    resp = flow.response
    if resp is None:
        return
    ua = req.headers.get("User-Agent", "") or req.headers.get("user-agent", "")
    if "UnityPlayer" not in ua:
        stats["skipped_non_game"] += 1
        return

    # req.host reflects the rewritten upstream IP (our mitmproxy addon redirects
    # server_connect to the real CloudFront IP); the original hostname the game
    # actually asked for is in the Host header, preserved via keep_host_header=true.
    host = req.headers.get("Host", req.host)
    method = req.method.upper()
    path_and_query = req.path  # includes query string, e.g. /api/check?user_id=123

    host_dir = out_root / host
    host_dir.mkdir(parents=True, exist_ok=True)

    key_hash = hashlib.sha256(f"{method} {path_and_query}".encode("utf-8")).hexdigest()[:8]
    slug = slugify(path_and_query.split("?", 1)[0])
    base_name = f"{method}_{slug}_{key_hash}"

    meta = {
        "host": host,
        "method": method,
        "pathAndQuery": path_and_query,
        "statusCode": resp.status_code,
        # Request headers are not needed for replay and may contain account tokens,
        # Steam/device identifiers, or other private session metadata.
        "responseHeaders": list(resp.headers.items()),
        "capturedAt": flow.request.timestamp_start,
        "sourceFlow": str(flow.id),
    }

    (host_dir / f"{base_name}.meta.json").write_text(
        json.dumps(meta, indent=2), encoding="utf-8"
    )
    (host_dir / f"{base_name}.body.bin").write_bytes(resp.raw_content or b"")
    stats["exported"] += 1


def capture_summary(paths: list[Path]) -> dict:
    summary = {"game_responses": 0, "account_snapshots": []}
    for path in paths:
        with path.open("rb") as f:
            reader = mitm_io.FlowReader(f)
            for flow in reader.stream():
                if not hasattr(flow, "request") or flow.response is None:
                    continue
                req = flow.request
                if "UnityPlayer" not in req.headers.get("User-Agent", ""):
                    continue
                summary["game_responses"] += 1
                if (
                    req.headers.get("Host", req.host) == "game-q74z3cyn.app.gl.ffviiec.com"
                    and req.method.upper() == "POST"
                    and req.path.split("?", 1)[0] == "/api/pvt/user/title"
                    and flow.response.status_code == 200
                    and flow.response.raw_content
                ):
                    summary["account_snapshots"].append(req.timestamp_start)
    return summary


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("captures", nargs="+", help="One or more .mitm capture files")
    ap.add_argument(
        "--out",
        default=str(Path(__file__).resolve().parent.parent / "captures"),
        help="Output CaptureStore root directory",
    )
    ap.add_argument(
        "--require-user-title",
        action="store_true",
        help="Refuse to import unless a successful account snapshot was captured",
    )
    ap.add_argument(
        "--check-only",
        action="store_true",
        help="Inspect capture without writing to the replay store",
    )
    args = ap.parse_args()

    paths = [Path(p) for p in args.captures]
    for path in paths:
        if not path.is_file():
            ap.error(f"Capture file does not exist: {path}")
    try:
        summary = capture_summary(paths)
    except (FlowReadException, OSError) as e:
        ap.error(f"Could not read capture: {e}")
    snapshots = summary["account_snapshots"]
    print(f"Game responses: {summary['game_responses']}; successful account snapshots: {len(snapshots)}")
    if snapshots:
        captured = datetime.fromtimestamp(max(snapshots), tz=timezone.utc)
        print(f"Latest account snapshot (UTC): {captured:%Y-%m-%d %H:%M:%S}")
    if args.require_user_title and not snapshots:
        print("ERROR: No successful POST /api/pvt/user/title response; account state was not captured.", file=sys.stderr)
        return 1
    if args.check_only:
        return 0

    out_root = Path(args.out)
    out_root.mkdir(parents=True, exist_ok=True)

    stats = {"exported": 0, "skipped_non_game": 0, "total_flows": 0}

    for p in paths:
        print(f"Reading {p} ...")
        with p.open("rb") as f:
            reader = mitm_io.FlowReader(f)
            try:
                for flow in reader.stream():
                    stats["total_flows"] += 1
                    export_flow(flow, out_root, stats)
            except FlowReadException as e:
                print(f"ERROR: Flow read stopped early; replay store may be partially updated: {e}", file=sys.stderr)
                return 1

    print(
        f"Done. total_flows={stats['total_flows']} exported={stats['exported']} "
        f"skipped_non_game={stats['skipped_non_game']} -> {out_root}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
