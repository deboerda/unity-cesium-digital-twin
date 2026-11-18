# -*- coding: utf-8 -*-
"""Download Esri World_Imagery z14 tiles for the HD inset bbox via images.weserv.nl.

Direct access to server.arcgisonline.com is intermittently blocked on this host;
https://images.weserv.nl/?url=<upstream> works reliably and fast (parallel 8 ok).
Cache: Assets/Pictures/_hd_tile_cache/{z}_{x}_{y}.jpg   (resumable)
"""
from __future__ import annotations

import math
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

LON_MIN, LAT_MIN = 103.66956, 30.46133
LON_MAX, LAT_MAX = 104.50156, 30.88204
ZOOM = 14
CACHE = Path(r"E:\UNITYproject\HK132CWIN7\Assets\Pictures\_hd_tile_cache")
UPSTREAM = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
WESERV = "https://images.weserv.nl/?url={u}&output=jpg&q=92"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
WORKERS = 8
MIN_BYTES = 2000


def lonlat_to_tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    r = math.radians(lat)
    y = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return x, y


def ok(path: Path) -> bool:
    if not path.exists() or path.stat().st_size < MIN_BYTES:
        return False
    with path.open("rb") as f:
        return f.read(2) == b"\xff\xd8"


def fetch(job: tuple[int, int, int]) -> tuple[int, int, int, bool]:
    z, tx, ty = job
    dest = CACHE / f"{z}_{tx}_{ty}.jpg"
    if ok(dest):
        return (z, tx, ty, True)
    raw = UPSTREAM.format(z=z, x=tx, y=ty)
    url = WESERV.format(u=raw.replace(":", "%3A").replace("/", "%2F"))
    attempts = [
        ["curl.exe", "-sS", "--noproxy", "*", "--connect-timeout", "10", "--max-time", "45", "-A", UA, "-o", str(dest), url],
        ["curl.exe", "-sS", "--noproxy", "*", "--connect-timeout", "10", "--max-time", "45", "-A", UA, "-o", str(dest), url],
        ["curl.exe", "-sS", "--connect-timeout", "10", "--max-time", "45", "-A", UA, "-o", str(dest), url],
    ]
    for cmd in attempts:
        try:
            subprocess.run(cmd, timeout=60, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        except Exception:
            pass
        if ok(dest):
            return (z, tx, ty, True)
        time.sleep(0.3)
    return (z, tx, ty, False)


def main() -> int:
    CACHE.mkdir(parents=True, exist_ok=True)
    x0, y0 = lonlat_to_tile_frac(LON_MIN, LAT_MAX, ZOOM)
    x1, y1 = lonlat_to_tile_frac(LON_MAX, LAT_MIN, ZOOM)
    tx0, ty0, tx1, ty1 = int(x0 // 1), int(y0 // 1), int(x1 // 1), int(y1 // 1)
    jobs = [(ZOOM, tx, ty) for ty in range(ty0, ty1 + 1) for tx in range(tx0, tx1 + 1)]
    print(f"grid tx[{tx0},{tx1}] ty[{ty0},{ty1}] -> {len(jobs)} tiles", flush=True)

    done = failed = 0
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for z, tx, ty, good in pool.map(fetch, jobs):
            if good:
                done += 1
            else:
                failed += 1
                print(f"  FAIL {z}/{tx}/{ty}", flush=True)
            if (done + failed) % 50 == 0:
                print(f"  {done + failed}/{len(jobs)} ok={done} fail={failed} {time.time() - t0:.0f}s", flush=True)
    print(f"done ok={done} fail={failed} in {time.time() - t0:.0f}s", flush=True)
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())