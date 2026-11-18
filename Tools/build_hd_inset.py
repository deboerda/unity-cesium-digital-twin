# -*- coding: utf-8 -*-
"""Stitch cached z14 tiles into the HD inset and crop exactly to the bbox."""
from __future__ import annotations

import json
import math
import sys
from io import BytesIO
from pathlib import Path

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

LON_MIN, LAT_MIN = 103.66956, 30.46133
LON_MAX, LAT_MAX = 104.50156, 30.88204
ZOOM = 14
TILE = 256
OUT_DIR = Path(r"E:\UNITYproject\HK132CWIN7\Assets\Pictures")
STEM = "飞行范围插图"
CACHE = OUT_DIR / "_hd_tile_cache"


def lonlat_to_tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    r = math.radians(lat)
    y = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return x, y


def main() -> int:
    x0, y0 = lonlat_to_tile_frac(LON_MIN, LAT_MAX, ZOOM)
    x1, y1 = lonlat_to_tile_frac(LON_MAX, LAT_MIN, ZOOM)
    tx0, ty0, tx1, ty1 = int(x0), int(y0), int(x1), int(y1)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    print(f"grid {cols}x{rows} tiles, z{ZOOM}", flush=True)

    mosaic = Image.new("RGB", (cols * TILE, rows * TILE), (32, 38, 32))
    missing = []
    for ty in range(ty0, ty1 + 1):
        for tx in range(tx0, tx1 + 1):
            p = CACHE / f"{ZOOM}_{tx}_{ty}.jpg"
            if not p.exists() or p.stat().st_size < 2000:
                missing.append(f"{tx},{ty}")
                continue
            try:
                im = Image.open(BytesIO(p.read_bytes())).convert("RGB")
                if im.size != (TILE, TILE):
                    im = im.resize((TILE, TILE), Image.LANCZOS)
                mosaic.paste(im, ((tx - tx0) * TILE, (ty - ty0) * TILE))
            except Exception as exc:  # noqa: BLE001
                missing.append(f"{tx},{ty}")
                print(f"  bad tile {tx},{ty}: {exc}", flush=True)
    if missing:
        print(f"WARN missing {len(missing)} tiles: {missing[:10]}", flush=True)

    left = int(round((x0 - tx0) * TILE))
    top = int(round((y0 - ty0) * TILE))
    right = int(round((x1 - tx0) * TILE))
    bottom = int(round((y1 - ty0) * TILE))
    cropped = mosaic.crop((left, top, right, bottom))

    jpg = OUT_DIR / f"{STEM}.jpg"
    cropped.convert("RGB").save(jpg, "JPEG", quality=90, optimize=True, subsampling=1)
    w, h = cropped.size
    dx = (LON_MAX - LON_MIN) / w
    dy = (LAT_MAX - LAT_MIN) / h
    (OUT_DIR / f"{STEM}.jgw").write_text(
        f"{dx:.12f}\n0.0\n0.0\n{-dy:.12f}\n{LON_MIN + dx / 2:.12f}\n{LAT_MAX - dy / 2:.12f}\n",
        encoding="utf-8",
    )
    (OUT_DIR / f"{STEM}.json").write_text(
        json.dumps(
            {
                "lonMin": LON_MIN, "lonMax": LON_MAX, "latMin": LAT_MIN, "latMax": LAT_MAX,
                "zoom": ZOOM, "width": w, "height": h, "image": jpg.name,
                "source": "Esri World_Imagery (WGS84/WebMercator) via images.weserv.nl",
            },
            ensure_ascii=False, indent=2,
        ),
        encoding="utf-8",
    )
    print(f"saved {jpg} {jpg.stat().st_size/1048576:.1f} MB {w}x{h} missing={len(missing)}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())