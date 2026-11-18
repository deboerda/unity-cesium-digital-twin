# -*- coding: utf-8 -*-
"""Sanity checks for the new HD inset: content stats + georeference cross-check vs the
older full-res export that covers the previous bbox exactly."""
from __future__ import annotations

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

NEW = r"E:\UNITYproject\HK132CWIN7\Assets\Pictures\飞行范围插图.jpg"
REF = r"E:\UNITYproject\HK132CWIN7\Assets\Pictures\卫星地图_SW103.91916_NE104.25196.jpg"
LB = (103.66956, 30.46133, 104.50156, 30.88204)   # new bbox W,S,E,N
OB = (103.91916083, 30.58753045, 104.25195738, 30.75583896)  # old bbox

new = np.asarray(Image.open(NEW).convert("L"))
print("new", new.shape, "mean %.1f std %.1f" % (new.mean(), new.std()))
h, w = new.shape
for name, sl in {
    "TL": (slice(0, h // 2), slice(0, w // 2)), "TR": (slice(0, h // 2), slice(w // 2, w)),
    "BL": (slice(h // 2, h), slice(0, w // 2)), "BR": (slice(h // 2, h), slice(w // 2, w)),
}.items():
    q = new[sl]
    print(f"  {name} mean {q.mean():.1f} std {q.std():.1f}")

# crop of new image that corresponds to the old bbox
def px(lon, lat):
    return (int((lon - LB[0]) / (LB[2] - LB[0]) * w), int((LB[3] - lat) / (LB[3] - LB[1]) * h))

x0, y0 = px(OB[0], OB[3])
x1, y1 = px(OB[2], OB[1])
crop = Image.fromarray(new[y0:y1, x0:x1])
ref = Image.open(REF).convert("L")
print("crop", crop.size, "ref", ref.size)

# normalise both to 480 px wide and correlate over small shifts
size = (480, 300)
a = np.asarray(crop.resize(size, Image.LANCZOS), dtype=np.float32)
b = np.asarray(ref.resize(size, Image.LANCZOS), dtype=np.float32)
a = (a - a.mean()) / (a.std() + 1e-6)
b = (b - b.mean()) / (b.std() + 1e-6)
best = (-2.0, 0, 0)
for dy in range(-6, 7):
    for dx in range(-6, 7):
        bb = np.roll(np.roll(b, dy, axis=0), dx, axis=1)
        c = float((a * bb).mean())
        if c > best[0]:
            best = (c, dx, dy)
print("best corr %.4f at shift dx=%d dy=%d (of 480x300 grid)" % best)
print("=> px-equivalent offset: %.1f x, %.1f y" % (best[1] * crop.size[0] / 480, best[2] * crop.size[1] / 300))