# -*- coding: utf-8 -*-
"""Probe Bing Virtual Earth aerial tiles over the target bbox (WGS84/WebMercator)."""
from __future__ import annotations

import math
import subprocess
import sys
import time
from io import BytesIO
from pathlib import Path

from PIL import Image

Image.MAX_IMAGE_PIXELS = None

OUT = Path(r"E:\UNITYproject\HK132CWIN7\Tools\_probe")
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"


def quadkey(x: int, y: int, z: int) -> str:
    digits = []
    for i in range(z, 0, -1):
        d = 0
        mask = 1 << (i - 1)
        if x & mask:
            d += 1
        if y & mask:
            d += 2
        digits.append(str(d))
    return "".join(digits)


def lonlat_to_tile(lon: float, lat: float, z: int) -> tuple[float, float]:
    n = 2.0 ** z
    x = (lon + 180.0) / 360.0 * n
    r = math.radians(lat)
    y = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return x, y


def fetch(url: str, dest: Path, try_proxy: bool = False) -> bool:
    cmds = [["curl.exe", "-sS", "--connect-timeout", "6", "--max-time", "20", "-A", UA, "-o", str(dest), url]]
    if try_proxy:
        cmds.append(["curl.exe", "-sS", "-x", "http://127.0.0.1:8848", "--connect-timeout", "6", "--max-time", "20", "-A", UA, "-o", str(dest), url])
    for cmd in cmds:
        try:
            subprocess.run(cmd, timeout=24, check=False)
        except Exception:
            continue
        if dest.exists() and dest.stat().st_size > 1500 and dest.read_bytes()[:2] == b"\xff\xd8":
            return True
        if dest.exists():
            dest.unlink()
    return False


def mosaic(z: int, cx: int, cy: int, half: int, tag: str) -> None:
    tiles = {}
    ok = 0
    t0 = time.time()
    for ty in range(cy - half, cy + half + 1):
        for tx in range(cx - half, cx + half + 1):
            dest = OUT / f"{z}_{tx}_{ty}.jpg"
            if not (dest.exists() and dest.stat().st_size > 1500):
                url = f"https://ecn.t{ok % 4}.tiles.virtualearth.net/tiles/a{quadkey(tx, ty, z)}.jpeg?g=1"
                if not fetch(url, dest, try_proxy=True):
                    continue
            try:
                tiles[(tx, ty)] = Image.open(BytesIO(dest.read_bytes())).convert("RGB")
                ok += 1
            except Exception:
                pass
    n = half * 2 + 1
    img = Image.new("RGB", (n * 256, n * 256), (20, 20, 20))
    for (tx, ty), im in tiles.items():
        img.paste(im, ((tx - (cx - half)) * 256, (ty - (cy - half)) * 256))
    p = OUT / f"bing_z{z}_{tag}.jpg"
    img.save(p, quality=88)
    print(f"z{z} {tag}: ok={ok}/{n*n} {time.time()-t0:.1f}s -> {p} {img.size}")


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    for z in (14, 15, 16, 17):
        x, y = lonlat_to_tile(104.08, 30.67, z)
        print(f"z{z} center tile {int(x)},{int(y)}")
        mosaic(z, int(x), int(y), 1, "center")
    return 0


if __name__ == "__main__":
    sys.exit(main())