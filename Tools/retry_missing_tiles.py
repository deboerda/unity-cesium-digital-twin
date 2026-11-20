# -*- coding: utf-8 -*-
"""Re-fetch undersized/failed tiles slowly (weserv rate-limits bursts).

Usage: python Tools/retry_missing_tiles.py <zoom> [workers] [rounds]
"""
from __future__ import annotations

import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

CACHE = Path(r"E:\UNITYproject\HK132CWIN7\Assets\Pictures\_hd_tile_cache")
UPSTREAM = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
WESERV = "https://images.weserv.nl/?url={u}&output=jpg&q=92"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
MIN_BYTES = 2000


def ok(p: Path) -> bool:
    if not p.exists() or p.stat().st_size < MIN_BYTES:
        return False
    with p.open("rb") as f:
        return f.read(2) == b"\xff\xd8"


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    zoom = int(sys.argv[1])
    workers = int(sys.argv[2]) if len(sys.argv) > 2 else 2
    rounds = int(sys.argv[3]) if len(sys.argv) > 3 else 4

    bad = []
    for p in CACHE.glob(f"{zoom}_*.jpg"):
        if not ok(p):
            bad.append(p)
    print(f"z{zoom}: {len(bad)} bad tiles", flush=True)

    total_fixed = 0
    for rnd in range(rounds):
        if not bad:
            break
        print(f"round {rnd + 1}: trying {len(bad)} with {workers} workers", flush=True)
        results: list[bool] = []
        counter = {"n": 0}
        lock = threading.Lock()

        def work(p: Path) -> bool:
            parts = p.stem.split("_")
            tx, ty = parts[1], parts[2]
            raw = UPSTREAM.format(z=zoom, x=tx, y=ty)
            url = WESERV.format(u=raw.replace(":", "%3A").replace("/", "%2F"))
            time.sleep(0.2)
            subprocess.run(
                ["curl.exe", "-sS", "--noproxy", "*", "--connect-timeout", "10",
                 "--max-time", "45", "-A", UA, "-o", str(p), url],
                timeout=60, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            good = ok(p)
            with lock:
                counter["n"] += 1
                if counter["n"] % 100 == 0:
                    print(f"  {counter['n']}/{len(bad)}", flush=True)
            return good

        with ThreadPoolExecutor(max_workers=workers) as pool:
            results = list(pool.map(work, bad))
        still = [p for p, good in zip(bad, results) if not good]
        total_fixed += len(bad) - len(still)
        bad = still
        print(f"round {rnd + 1}: fixed so far={total_fixed} remaining={len(bad)}", flush=True)
        if bad:
            time.sleep(20 + rnd * 20)

    print(f"done: fixed={total_fixed} remaining={len(bad)}", flush=True)
    if bad:
        for p in bad[:10]:
            print("  still bad:", p.name, flush=True)
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
