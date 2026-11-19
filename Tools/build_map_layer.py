# -*- coding: utf-8 -*-
"""Download + stitch one map layer (Esri World_Imagery via images.weserv.nl).

Usage:
  python Tools/build_map_layer.py <zoom> <lonMin> <latMin> <lonMax> <latMax> <name>

Writes Assets/Resources/MapLayers/<name>.jpg plus <name>.json next to it.
Tile cache is shared at Assets/Pictures/_hd_tile_cache/{z}_{x}_{y}.jpg (resumable).
"""
from __future__ import annotations

import json
import math
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO
from pathlib import Path

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

ROOT = Path(r"E:\UNITYproject\HK132CWIN7")
CACHE = ROOT / "Assets" / "Pictures" / "_hd_tile_cache"
OUT_DIR = ROOT / "Assets" / "Resources" / "MapLayers"
TILE = 256
UPSTREAM = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
WESERV = "https://images.weserv.nl/?url={u}&output=jpg&q=92"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
WORKERS = 12
MIN_BYTES = 2000
MAX_EDGE = 8192


def tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    r = math.radians(lat)
    y = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return x, y


def tile_ok(p: Path) -> bool:
    if not p.exists() or p.stat().st_size < MIN_BYTES:
        return False
    with p.open("rb") as f:
        return f.read(2) == b"\xff\xd8"


def fetch(job: tuple[int, int, int]) -> bool:
    z, tx, ty = job
    dest = CACHE / f"{z}_{tx}_{ty}.jpg"
    if tile_ok(dest):
        return True
    raw = UPSTREAM.format(z=z, x=tx, y=ty)
    url = WESERV.format(u=raw.replace(":", "%3A").replace("/", "%2F"))
    for attempt in range(3):
        cmd = ["curl.exe", "-sS", "--noproxy", "*", "--connect-timeout", "10",
               "--max-time", "45", "-A", UA, "-o", str(dest), url]
        try:
            import subprocess
            subprocess.run(cmd, timeout=60, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, check=False)
        except Exception:
            pass
        if tile_ok(dest):
            return True
        time.sleep(0.25 * (attempt + 1))
    return False


def main() -> int:
    if len(sys.argv) < 7:
        print(__doc__)
        return 2
    zoom = int(sys.argv[1])
    lon_min, lat_min, lon_max, lat_max = (float(v) for v in sys.argv[2:6])
    name = sys.argv[6]

    CACHE.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    x0, y0 = tile_frac(lon_min, lat_max, zoom)
    x1, y1 = tile_frac(lon_max, lat_min, zoom)
    tx0, ty0, tx1, ty1 = int(x0), int(y0), int(x1), int(y1)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    total = cols * rows
    print(f"[{name}] z{zoom} grid {cols}x{rows} = {total} tiles", flush=True)

    jobs = [(zoom, tx, ty) for ty in range(ty0, ty1 + 1) for tx in range(tx0, tx1 + 1)]
    done = failed = 0
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for good in pool.map(fetch, jobs):
            done += 1 if good else 0
            failed += 0 if good else 1
            n = done + failed
            if n % 100 == 0 or n == total:
                print(f"  {n}/{total} ok={done} fail={failed} {time.time() - t0:.0f}s", flush=True)

    mosaic = Image.new("RGB", (cols * TILE, rows * TILE), (32, 38, 32))
    missing = 0
    for ty in range(ty0, ty1 + 1):
        for tx in range(tx0, tx1 + 1):
            p = CACHE / f"{zoom}_{tx}_{ty}.jpg"
            if not tile_ok(p):
                missing += 1
                continue
            try:
                im = Image.open(BytesIO(p.read_bytes())).convert("RGB")
                if im.size != (TILE, TILE):
                    im = im.resize((TILE, TILE), Image.LANCZOS)
                mosaic.paste(im, ((tx - tx0) * TILE, (ty - ty0) * TILE))
            except Exception as exc:  # noqa: BLE001
                missing += 1
                print(f"  bad tile {tx},{ty}: {exc}", flush=True)

    left = int(round((x0 - tx0) * TILE))
    top = int(round((y0 - ty0) * TILE))
    right = int(round((x1 - tx0) * TILE))
    bottom = int(round((y1 - ty0) * TILE))
    cropped = mosaic.crop((left, top, right, bottom))

    w, h = cropped.size
    scale = min(1.0, MAX_EDGE / max(w, h))
    if scale < 1.0:
        w, h = int(w * scale), int(h * scale)
        cropped = cropped.resize((w, h), Image.LANCZOS)

    jpg = OUT_DIR / f"{name}.jpg"
    cropped.save(jpg, "JPEG", quality=92, optimize=True, subsampling=1)

    meta = {
        "name": name,
        "lonMin": lon_min, "latMin": lat_min, "lonMax": lon_max, "latMax": lat_max,
        "zoom": zoom, "width": w, "height": h,
        "source": "Esri World_Imagery via images.weserv.nl",
        "missingTiles": missing,
    }
    (OUT_DIR / f"{name}.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"[{name}] saved {w}x{h} {jpg.stat().st_size/1048576:.1f} MB "
          f"missing={missing} in {time.time() - t0:.0f}s", flush=True)
    return 0 if missing == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
