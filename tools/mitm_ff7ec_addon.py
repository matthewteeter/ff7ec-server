"""
mitmproxy addon for FF7 Ever Crisis capture.

Problem: the Windows hosts file redirects the game's API hostnames to
127.0.0.1 so the game connects to our local mitmproxy reverse-mode listener
instead of the real AWS/CloudFront-backed servers. But mitmproxy's OWN
upstream connection would ALSO be poisoned by that same hosts file if we
let it resolve the hostname normally -> self-connect loop.

Fix: resolve the real IPs ourselves via Google's DNS-over-HTTPS JSON API
(a plain HTTPS call to dns.google, a host we never redirect, so it resolves
normally), cache them, and rewrite `data.server.address` in the
`server_connect` hook to the real IP - bypassing the poisoned OS resolver
for mitmproxy's own connection while leaving the client-facing SNI/Host
untouched (so CloudFront still routes correctly).
"""
import hashlib
import json
import os
import re
import urllib.request
from pathlib import Path
from mitmproxy import ctx

# Hostnames routed to this proxy by capture hosts entries or the Frida hook.
TRACKED_HOSTS = [
    "game-q74z3cyn.app.gl.ffviiec.com",
    "resources-api-c9ps53g2.app.gl.ffviiec.com",
    "resources-data-w6d4k7cz.app.gl.ffviiec.com",
    "webview-w62j4u3y.app.gl.ffviiec.com",
    "client-masterdata-c9ps53g2.app.gl.ffviiec.com",
]


def resolve_via_doh(hostname: str) -> str | None:
    url = f"https://dns.google/resolve?name={hostname}&type=A"
    try:
        with urllib.request.urlopen(url, timeout=5) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        for answer in data.get("Answer", []):
            if answer.get("type") == 1:  # A record
                return answer["data"]
    except Exception as e:
        ctx.log.error(f"[ff7ec] DoH resolve failed for {hostname}: {e}")
    return None


class RealUpstreamRedirect:
    def __init__(self):
        self.real_ip_cache: dict[str, str] = {}

    def running(self):
        ctx.log.info("[ff7ec] Resolving real IPs for tracked hosts via DoH...")
        for host in TRACKED_HOSTS:
            ip = resolve_via_doh(host)
            if ip:
                self.real_ip_cache[host] = ip
                ctx.log.info(f"[ff7ec] {host} -> {ip}")
            else:
                ctx.log.error(f"[ff7ec] Could not resolve {host}; requests to it will fail")

    def server_connect(self, data):
        # data.client.sni is what the game actually asked for (its TLS
        # ClientHello SNI), regardless of what our reverse-mode spec says
        # or what data.server.address currently holds.
        sni = data.client.sni
        host = sni or (data.server.address[0] if data.server.address else None)
        if host in self.real_ip_cache:
            real_ip = self.real_ip_cache[host]
            port = data.server.address[1] if data.server.address else 443
            ctx.log.info(f"[ff7ec] Redirecting connection for {host} -> {real_ip}:{port}")
            data.server.address = (real_ip, port)
        elif host in TRACKED_HOSTS:
            ctx.log.error(f"[ff7ec] No cached IP for tracked host {host}; connection will likely fail")


class CaptureStoreExporter:
    def __init__(self):
        out_root = os.environ.get("FF7EC_CAPTURE_STORE_OUT")
        if not out_root:
            raise RuntimeError(
                "FF7EC_CAPTURE_STORE_OUT is not set; start mitmdump in the same "
                "PowerShell window as the new capture session setup."
            )
        self.out_root = Path(out_root)

    def running(self):
        ctx.log.info(f"[ff7ec] Replay records will be staged in {self.out_root}")

    def response(self, flow):
        req = flow.request
        resp = flow.response
        if resp is None or "UnityPlayer" not in req.headers.get("User-Agent", ""):
            return

        host = req.headers.get("Host", req.host)
        method = req.method.upper()
        path_and_query = req.path
        host_dir = self.out_root / host
        host_dir.mkdir(parents=True, exist_ok=True)
        slug = re.sub(r"[^A-Za-z0-9]+", "_", path_and_query.split("?", 1)[0]).strip("_")[:60] or "root"
        key_hash = hashlib.sha256(f"{method} {path_and_query}".encode()).hexdigest()[:8]
        base_name = f"{method}_{slug}_{key_hash}"
        meta = {
            "host": host,
            "method": method,
            "pathAndQuery": path_and_query,
            "statusCode": resp.status_code,
            "responseHeaders": list(resp.headers.items()),
            "capturedAt": req.timestamp_start,
            "sourceFlow": str(flow.id),
        }
        (host_dir / f"{base_name}.meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
        (host_dir / f"{base_name}.body.bin").write_bytes(resp.raw_content or b"")
        ctx.log.info(f"[ff7ec] exported {method} {host}{path_and_query}")


addons = [RealUpstreamRedirect(), CaptureStoreExporter()]
