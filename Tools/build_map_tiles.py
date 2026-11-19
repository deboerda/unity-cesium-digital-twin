# -*- coding: utf-8 -*-
"""Download + stitch one map level, split into <=8192 px tiles.

Usage:
  python Tools/build_map_tiles.py <zoom> <lonMin> <latMin> <lonMax> <latMax> <prefix> [maxEdge]

Every produced tile keeps its own lon/lat bbox so 2D and 3D can place it exactly.
Output: Assets/Resources/MapLayers/<prefix>_<r><c>.jpg (+ .json)
Tiles are written as a JSON manifest fragment at Assets/Resources/MapLayers/<prefix>_manifest.json
"""
from __future__ import annotations

import json
import math
import subprocess
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

META_TEMPLATE = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  serializedVersion: 12
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  ignoreMipmapLimit: 1
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 8192
  textureSettings:
    serializedVersion: 2
    filterMode: 2
    aniso: 16
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 0
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 100
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 8192
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""


def write_meta(jpg: Path) -> None:
    """Unity defaults new textures to 2048 px; write the settings we need."""
    import uuid
    meta = jpg.with_suffix(jpg.suffix + ".meta")
    if meta.exists():
        return
    guid = uuid.uuid4().hex
    meta.write_text(META_TEMPLATE.format(guid=guid), encoding="utf-8")


UPSTREAM = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
WESERV = "https://images.weserv.nl/?url={u}&output=jpg&q=92"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
WORKERS = 12
MIN_BYTES = 2000


def tile_frac(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = (lon + 180.0) / 360.0 * n
    r = math.radians(lat)
    y = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return x, y


def frac_to_lonlat(xf: float, yf: float, zoom: int) -> tuple[float, float]:
    n = 2.0 ** zoom
    lon = xf / n * 360.0 - 180.0
    lat = math.degrees(math.atan(math.sinh(math.pi * (1.0 - 2.0 * yf / n))))
    return lon, lat


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
    prefix = sys.argv[6]
    max_edge = int(sys.argv[7]) if len(sys.argv) > 7 else 8192

    CACHE.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    x0f, y0f = tile_frac(lon_min, lat_max, zoom)
    x1f, y1f = tile_frac(lon_max, lat_min, zoom)
    px_w = (x1f - x0f) * TILE
    px_h = (y1f - y0f) * TILE
    cols = max(1, math.ceil(px_w / max_edge))
    rows = max(1, math.ceil(px_h / max_edge))
    print(f"[{prefix}] z{zoom} {px_w:.0f}x{px_h:.0f}px -> {rows}x{cols} tiles", flush=True)

    # ---- download the union of tiles this level needs (resumable) ----
    tx0, ty0 = int(x0f), int(y0f)
    tx1, ty1 = int(x1f), int(y1f)
    jobs = [(zoom, tx, ty) for ty in range(ty0, ty1 + 1) for tx in range(tx0, tx1 + 1)]
    total = len(jobs)
    print(f"[{prefix}] tiles to cover: {total}", flush=True)
    done = failed = 0
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for good in pool.map(fetch, jobs):
            done += 1 if good else 0
            failed += 0 if good else 1
            n = done + failed
            if n % 200 == 0 or n == total:
                print(f"  {n}/{total} ok={done} fail={failed} {time.time() - t0:.0f}s", flush=True)

    # ---- cut each output tile ----
    manifest = []
    for r in range(rows):
        for c in range(cols):
            pix_x0 = px_w * c / cols
            pix_x1 = px_w * (c + 1) / cols
            pix_y0 = px_h * r / rows
            pix_y1 = px_h * (r + 1) / rows

            # global fractional tile coords of this sub-rect
            sx0 = x0f + pix_x0 / TILE
            sx1 = x0f + pix_x1 / TILE
            sy0 = y0f + pix_y0 / TILE
            sy1 = y0f + pix_y1 / TILE

            stx0, sty0 = int(math.floor(sx0)), int(math.floor(sy0))
            stx1, sty1 = int(math.ceil(sx1)), int(math.ceil(sy1))
            sw = (stx1 - stx0) * TILE
            sh = (sty1 - sty0) * TILE
            mosaic = Image.new("RGB", (sw, sh), (32, 38, 32))
            missing = 0
            for ty in range(sty0, sty1):
                for tx in range(stx0, stx1):
                    p = CACHE / f"{zoom}_{tx}_{ty}.jpg"
                    if not tile_ok(p):
                        missing += 1
                        continue
                    try:
                        im = Image.open(BytesIO(p.read_bytes())).convert("RGB")
                        if im.size != (TILE, TILE):
                            im = im.resize((TILE, TILE), Image.LANCZOS)
                        mosaic.paste(im, ((tx - stx0) * TILE, (ty - sty0) * TILE))
                    except Exception:
                        missing += 1

            left = int(round((sx0 - stx0) * TILE))
            top = int(round((sy0 - sty0) * TILE))
            right = int(round((sx1 - stx0) * TILE))
            bottom = int(round((sy1 - sty0) * TILE))
            cropped = mosaic.crop((left, top, right, bottom))
            out_w, out_h = cropped.size

            name = f"{prefix}_{r}{c}"
            jpg = OUT_DIR / f"{name}.jpg"
            cropped.save(jpg, "JPEG", quality=92, optimize=True, subsampling=1)
            write_meta(jpg)

            lon_a, lat_a = frac_to_lonlat(sx0, sy0, zoom)
            lon_b, lat_b = frac_to_lonlat(sx1, sy1, zoom)
            meta = {
                "name": f"{prefix} {r + 1}-{c + 1}",
                "image": f"MapLayers/{name}",
                "lonMin": round(min(lon_a, lon_b), 6),
                "lonMax": round(max(lon_a, lon_b), 6),
                "latMin": round(min(lat_a, lat_b), 6),
                "latMax": round(max(lat_a, lat_b), 6),
                "zoom": zoom,
                "width": out_w,
                "height": out_h,
                "missingTiles": missing,
            }
            (OUT_DIR / f"{name}.json").write_text(
                json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
            manifest.append(meta)
            print(f"  [{name}] {out_w}x{out_h} {jpg.stat().st_size/1048576:.1f}MB missing={missing}",
                  flush=True)

    (OUT_DIR / f"{prefix}_manifest.json").write_text(
        json.dumps({"layers": manifest}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[{prefix}] done {len(manifest)} tiles, total {time.time() - t0:.0f}s", flush=True)
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
