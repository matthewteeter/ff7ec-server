"""
Converts one or more mitmproxy .mitm capture files into the FF7EC-Server
CaptureStore folder format the C# replay server reads at startup.

CaptureStore layout (under --out, default E:\\FF7EC-Server\\captures):
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
    python export_capture_store.py capture_rehearsal.mitm [more.mitm ...] [--out DIR]
"""
import argparse
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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("captures", nargs="+", help="One or more .mitm capture files")
    ap.add_argument(
        "--out",
        default=r"E:\FF7EC-Server\captures",
        help="Output CaptureStore root directory",
    )
    args = ap.parse_args()

    out_root = Path(args.out)
    out_root.mkdir(parents=True, exist_ok=True)

    stats = {"exported": 0, "skipped_non_game": 0, "total_flows": 0}

    for capture_path in args.captures:
        p = Path(capture_path)
        if not p.exists():
            print(f"WARNING: {p} does not exist, skipping", file=sys.stderr)
            continue
        print(f"Reading {p} ...")
        with p.open("rb") as f:
            reader = mitm_io.FlowReader(f)
            try:
                for flow in reader.stream():
                    stats["total_flows"] += 1
                    export_flow(flow, out_root, stats)
            except FlowReadException as e:
                print(f"  Flow read stopped early: {e}", file=sys.stderr)

    print(
        f"Done. total_flows={stats['total_flows']} exported={stats['exported']} "
        f"skipped_non_game={stats['skipped_non_game']} -> {out_root}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
