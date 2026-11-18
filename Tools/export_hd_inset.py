# -*- coding: utf-8 -*-
"""Download a larger HD inset with batched tile fetch + resume cache."""
from __future__ import annotations

import json
import math
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from io import BytesIO
from pathlib import Path

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

LON_MIN = 103.66956
LAT_MIN = 30.46133
LON_MAX = 104.50156
LAT_MAX = 30.88204
ZOOM = 14
TILE = 256
BATCH = 8
WORKERS = 4
PROXY = "http://127.0.0.1:8848"
OUT_DIR = Path(r"E:\UNITYproject\HK132CWIN7\Assets\Pictures")
STEM = "飞行范围插图"
CACHE = OUT_DIR / "_hd_tile_cache"
URL = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"


def lonlat_to_tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    lat_rad = math.radians(lat)
    y = (1.0 - math.log(math.tan(lat_rad) + 1.0 / math.cos(lat_rad)) / math.pi) / 2.0 * n
    return x, y


def fetch_tile(z: int, x: int, y: int) -> bytes | None:
    cache = CACHE / f"{z}_{x}_{y}.jpg"
    if cache.exists() and cache.stat().st_size > 800:
        return cache.read_bytes()
    url = URL.format(z=z, x=x, y=y)
    cmds = [
        ["curl.exe", "-sS", "--connect-timeout", "8", "--max-time", "18", "-A", UA, "-o", str(cache), url],
        ["curl.exe", "-sS", "-x", PROXY, "--connect-timeout", "8", "--max-time", "18", "-A", UA, "-o", str(cache), url],
    ]
    for cmd in cmds:
        try:
            proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                proc.wait(timeout=16)
            except subprocess.TimeoutExpired:
                proc.kill()
                try:
                    proc.wait(timeout=3)
                except Exception:
                    pass
                continue
            if cache.exists() and cache.stat().st_size > 800:
                data = cache.read_bytes()
                if data[:2] == b"\xff\xd8":
                    return data
            if cache.exists():
                cache.unlink()
        except Exception:
            time.sleep(0.2)
    return None


def main() -> int:
    CACHE.mkdir(parents=True, exist_ok=True)
    x0, y0 = lonlat_to_tile_frac(LON_MIN, LAT_MAX, ZOOM)
    x1, y1 = lonlat_to_tile_frac(LON_MAX, LAT_MIN, ZOOM)
    tx0, ty0 = int(math.floor(x0)), int(math.floor(y0))
    tx1, ty1 = int(math.floor(x1)), int(math.floor(y1))
    jobs = [(tx, ty) for ty in range(ty0, ty1 + 1) for tx in range(tx0, tx1 + 1)]
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    print(f"bbox lon[{LON_MIN},{LON_MAX}] lat[{LAT_MIN},{LAT_MAX}]", flush=True)
    print(f"zoom {ZOOM} tiles {cols}x{rows} = {len(jobs)}", flush=True)

    mosaic = Image.new("RGB", (cols * TILE, rows * TILE))
    done = 0
    t0 = time.time()
    for tx, ty in jobs:
        data = fetch_tile(ZOOM, tx, ty)
        tile = Image.open(BytesIO(data)).convert("RGB") if data else Image.new("RGB", (TILE, TILE), (40, 50, 40))
        mosaic.paste(tile, ((tx - tx0) * TILE, (ty - ty0) * TILE))
        done += 1
        if done == 1 or done % 20 == 0 or done == len(jobs):
            print(f"  {done}/{len(jobs)}  {time.time() - t0:.1f}s", flush=True)

    left = int(round((x0 - tx0) * TILE))
    top = int(round((y0 - ty0) * TILE))
    right = int(round((x1 - tx0) * TILE))
    bottom = int(round((y1 - ty0) * TILE))
    cropped = mosaic.crop((max(0, left), max(0, top), min(mosaic.width, right), min(mosaic.height, bottom)))
    jpg_path = OUT_DIR / f"{STEM}.jpg"
    cropped.save(jpg_path, "JPEG", quality=90, optimize=True, subsampling=1)
    w, h = cropped.size
    dx = (LON_MAX - LON_MIN) / w
    dy = (LAT_MAX - LAT_MIN) / h
    (OUT_DIR / f"{STEM}.jgw").write_text(
        f"{dx:.12f}\n0.0\n0.0\n{-dy:.12f}\n{LON_MIN + dx / 2:.12f}\n{LAT_MAX - dy / 2:.12f}\n",
        encoding="utf-8",
    )
    meta = {
        "lonMin": LON_MIN, "lonMax": LON_MAX, "latMin": LAT_MIN, "latMax": LAT_MAX,
        "zoom": ZOOM, "width": w, "height": h, "image": jpg_path.name,
    }
    (OUT_DIR / f"{STEM}.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"saved {jpg_path} {jpg_path.stat().st_size} bytes {w}x{h}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
