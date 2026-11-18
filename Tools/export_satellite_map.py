# -*- coding: utf-8 -*-
"""Export a WGS84-aligned Esri World Imagery mosaic for a lon/lat bbox."""
from __future__ import annotations

import json
import math
import sys
import time
import urllib.request
from concurrent.futures import ProcessPoolExecutor, as_completed
from io import BytesIO
from pathlib import Path

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

LON_MIN = 103.66956
LAT_MIN = 30.46133
LON_MAX = 104.50156
LAT_MAX = 30.88204
ZOOM = 15
TILE_SIZE = 256
MAX_WORKERS = 8
RETRIES = 3
OUT_DIR = Path(r"E:\UNITYproject\HK132CWIN7\Assets\Pictures")
STEM = "飞行范围插图"

TILE_URLS = [
    "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
    "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
]
UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)

def lonlat_to_tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    lat_rad = math.radians(lat)
    y = (1.0 - math.log(math.tan(lat_rad) + 1.0 / math.cos(lat_rad)) / math.pi) / 2.0 * n
    return x, y


def fetch_tile(z: int, x: int, y: int) -> bytes:
    last_err = None
    for attempt in range(RETRIES):
        url = TILE_URLS[attempt % len(TILE_URLS)].format(z=z, x=x, y=y)
        req = urllib.request.Request(
            url,
            headers={"User-Agent": UA, "Referer": "https://www.arcgis.com/"},
        )
        try:
            opener = urllib.request.build_opener(
                urllib.request.ProxyHandler({
                    "http": "http://127.0.0.1:8848",
                    "https": "http://127.0.0.1:8848",
                })
            )
            with opener.open(req, timeout=20) as resp:
                data = resp.read()
            if len(data) > 800 and data[:2] == b"\xff\xd8":
                return data
            last_err = RuntimeError(f"bad tile bytes={len(data)} magic={data[:8]!r}")
        except Exception as exc:  # noqa: BLE001
            last_err = exc
        time.sleep(0.2 * (attempt + 1))
    return None


def main() -> int:
    x0, y0 = lonlat_to_tile_frac(LON_MIN, LAT_MAX, ZOOM)  # NW
    x1, y1 = lonlat_to_tile_frac(LON_MAX, LAT_MIN, ZOOM)  # SE
    tx0, ty0 = int(math.floor(x0)), int(math.floor(y0))
    tx1, ty1 = int(math.floor(x1)), int(math.floor(y1))
    cols = tx1 - tx0 + 1
    rows = ty1 - ty0 + 1
    jobs = [(tx, ty) for ty in range(ty0, ty1 + 1) for tx in range(tx0, tx1 + 1)]
    print(f"bbox lon[{LON_MIN},{LON_MAX}] lat[{LAT_MIN},{LAT_MAX}]")
    print(f"zoom {ZOOM} tiles {cols}x{rows} = {len(jobs)}")

    mosaic = Image.new("RGB", (cols * TILE_SIZE, rows * TILE_SIZE))
    done = 0
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=MAX_WORKERS) as pool:
        futures = {pool.submit(fetch_tile, ZOOM, tx, ty): (tx, ty) for tx, ty in jobs}
        for fut in as_completed(futures):
            tx, ty = futures[fut]
            data = fut.result()
            if data:
                tile = Image.open(BytesIO(data)).convert("RGB")
            else:
                tile = Image.new("RGB", (TILE_SIZE, TILE_SIZE), (40, 50, 40))
            mosaic.paste(tile, ((tx - tx0) * TILE_SIZE, (ty - ty0) * TILE_SIZE))
            done += 1
            if done == 1 or done % 25 == 0 or done == len(jobs):
                elapsed = time.time() - t0
                print(f"  {done}/{len(jobs)}  {elapsed:.1f}s", flush=True)

    left = int(round((x0 - tx0) * TILE_SIZE))
    top = int(round((y0 - ty0) * TILE_SIZE))
    right = int(round((x1 - tx0) * TILE_SIZE))
    bottom = int(round((y1 - ty0) * TILE_SIZE))
    left = max(0, min(left, mosaic.width - 1))
    top = max(0, min(top, mosaic.height - 1))
    right = max(left + 1, min(right, mosaic.width))
    bottom = max(top + 1, min(bottom, mosaic.height))
    cropped = mosaic.crop((left, top, right, bottom))
    print(f"cropped {cropped.size[0]}x{cropped.size[1]} from mosaic {mosaic.size}")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    jpg_path = OUT_DIR / f"{STEM}.jpg"
    json_path = OUT_DIR / f"{STEM}.json"
    jgw_path = OUT_DIR / f"{STEM}.jgw"
    preview_path = OUT_DIR / f"{STEM}_preview.jpg"

    cropped.save(jpg_path, "JPEG", quality=92, optimize=True, subsampling=1)
    w, h = cropped.size
    dx = (LON_MAX - LON_MIN) / w
    dy = (LAT_MAX - LAT_MIN) / h
    jgw = (
        f"{dx:.12f}\n"
        "0.0\n"
        "0.0\n"
        f"{-dy:.12f}\n"
        f"{LON_MIN + dx / 2:.12f}\n"
        f"{LAT_MAX - dy / 2:.12f}\n"
    )
    jgw_path.write_text(jgw, encoding="utf-8")
    meta = {
        "source": "Esri World Imagery",
        "crs": "EPSG:4326",
        "southWest": {"lon": LON_MIN, "lat": LAT_MIN},
        "northEast": {"lon": LON_MAX, "lat": LAT_MAX},
        "lonMin": LON_MIN,
        "lonMax": LON_MAX,
        "latMin": LAT_MIN,
        "latMax": LAT_MAX,
        "zoom": ZOOM,
        "width": w,
        "height": h,
        "metersPerPixelApprox": 156543.03392
        * math.cos(math.radians((LAT_MIN + LAT_MAX) / 2.0))
        / (2 ** ZOOM),
        "image": jpg_path.name,
        "worldFile": jgw_path.name,
        "note": "North is up. Pixel (0,0) is northwest. Do not apply GCJ-02 offset.",
    }
    json_path.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    preview = cropped.copy()
    preview.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
    preview.save(preview_path, "JPEG", quality=88, optimize=True)

    print(f"saved {jpg_path}  {jpg_path.stat().st_size} bytes")
    print(f"saved {json_path}")
    print(f"saved {jgw_path}")
    print(f"saved {preview_path}  {preview.size}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
