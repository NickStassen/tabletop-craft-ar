"""Offline render of a mesh dumped by quest-app/Tests~ (`dotnet run -- --jar CLIENT.jar --scene MESH.json`).

Orthographic view that follows the Quest block shaders: one texture per layer, point sampling with repeat,
vertex colour tint, face shading from the normal, alpha test for the opaque submesh and alpha blending for the
transparent one. For checking block shapes and texture orientation without a headset. Needs Pillow and numpy:
    uv run --no-project --with pillow --with numpy python tools/render_mesh.py MESH.json CLIENT.jar OUT.png [--view se]
The client jar is only read for textures; never commit it or the output of a real world.
"""
from __future__ import annotations

import argparse
import io
import json
import zipfile

import numpy as np
from PIL import Image

VIEWS = {"se": (1.0, 1.0), "sw": (-1.0, 1.0), "ne": (1.0, -1.0), "nw": (-1.0, -1.0)}


def load_textures(jar: str, names: list[str]) -> list[np.ndarray]:
    z = zipfile.ZipFile(jar)
    out = []
    for n in names:
        ns, p = n.split(":", 1)
        im = Image.open(io.BytesIO(z.read(f"assets/{ns}/textures/{p}.png"))).convert("RGBA")
        w, h = im.size
        out.append(np.asarray(im.crop((0, 0, w, min(w, h))), dtype=np.float32) / 255.0)   # first animation frame
    return out


def shade(n: np.ndarray) -> float:
    a = np.abs(n)
    if a[1] > 0.5:
        return 1.0 if n[1] > 0 else 0.5
    return 0.8 if a[0] > 0.5 else 0.65


def render(mesh: dict, tex: list[np.ndarray], view: str, px: int, box: list[float] | None = None) -> Image.Image:
    v = np.array(mesh["verts"], dtype=np.float64).reshape(-1, 3)
    keep = np.ones(len(v), dtype=bool)
    if box:                                                  # only triangles inside x0,y0,z0,x1,y1,z1 (block units)
        keep = np.all((v >= np.array(box[:3]) - 1e-3) & (v <= np.array(box[3:]) + 1e-3), axis=1)
    uv = np.array(mesh["uvs"], dtype=np.float64).reshape(-1, 3)
    col = np.array(mesh["colors"], dtype=np.float64).reshape(-1, 4) / 255.0
    sx, sz = VIEWS[view]
    f = -np.array([sx, 1.15, sz])
    f /= np.linalg.norm(f)                                   # view direction (camera -> scene)
    r = np.cross(f, [0.0, 1.0, 0.0])
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    x, y, d = v @ r, v @ u, v @ f
    xk, yk = x[keep], y[keep]
    w = int((xk.max() - xk.min()) * px) + 20
    h = int((yk.max() - yk.min()) * px) + 20
    X, Y = (x - xk.min()) * px + 10, (yk.max() - y) * px + 10
    img = np.zeros((h, w, 3))
    img[:] = (0.16, 0.17, 0.2)
    depth = np.full((h, w), np.inf)

    def tri(i0: int, i1: int, i2: int, blend: bool) -> None:
        if not (keep[i0] and keep[i1] and keep[i2]):
            return
        n = np.cross(v[i1] - v[i0], v[i2] - v[i0])
        if np.dot(n, f) >= 0:                                # back face (the shaders cull back faces)
            return
        s = shade(n / np.linalg.norm(n))
        xs, ys = np.array([X[i0], X[i1], X[i2]]), np.array([Y[i0], Y[i1], Y[i2]])
        x0, x1 = max(int(xs.min()), 0), min(int(np.ceil(xs.max())), w - 1)
        y0, y1 = max(int(ys.min()), 0), min(int(np.ceil(ys.max())), h - 1)
        if x1 < x0 or y1 < y0:
            return
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        area = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
        if abs(area) < 1e-9:
            return
        b0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / area
        b1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / area
        b2 = 1 - b0 - b1
        inside = (b0 >= -1e-6) & (b1 >= -1e-6) & (b2 >= -1e-6)
        if not inside.any():
            return
        z = b0 * d[i0] + b1 * d[i1] + b2 * d[i2]
        sub = depth[y0:y1 + 1, x0:x1 + 1]
        vis = inside & (z < sub - 1e-7)
        if not vis.any():
            return
        c = (col[i0] + col[i1] + col[i2]) / 3
        layer = int(round(uv[i0, 2]))
        if layer >= 0:
            t = tex[layer]
            size = t.shape[0]
            tu = b0 * uv[i0, 0] + b1 * uv[i1, 0] + b2 * uv[i2, 0]
            tv = b0 * uv[i0, 1] + b1 * uv[i1, 1] + b2 * uv[i2, 1]
            cx = np.clip(np.floor((tu - np.floor(tu)) * size), 0, size - 1).astype(int)
            cy = np.clip(size - 1 - np.floor((tv - np.floor(tv)) * size), 0, size - 1).astype(int)
            rgba = t[cy, cx] * c
        else:
            rgba = np.broadcast_to(c, gx.shape + (4,)).copy()
        rgba[..., :3] *= s
        a = rgba[..., 3]
        vis &= a > (0.01 if blend else 0.3)
        dst = img[y0:y1 + 1, x0:x1 + 1]
        if blend:
            dst[vis] = rgba[vis, :3] * a[vis, None] + dst[vis] * (1 - a[vis, None])
        else:
            dst[vis] = rgba[vis, :3]
            sub[vis] = z[vis]

    o = mesh["opaque"]
    for i in range(0, len(o), 3):
        tri(o[i], o[i + 1], o[i + 2], False)
    t = mesh["transparent"]
    order = sorted(range(0, len(t), 3), key=lambda i: -(d[t[i]] + d[t[i + 1]] + d[t[i + 2]]))
    for i in order:
        tri(t[i], t[i + 1], t[i + 2], True)
    return Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("mesh")
    ap.add_argument("jar")
    ap.add_argument("out")
    ap.add_argument("--view", choices=sorted(VIEWS), default="se")
    ap.add_argument("--px", type=int, default=48, help="pixels per block")
    ap.add_argument("--box", help="x0,y0,z0,x1,y1,z1: only draw what lies inside (block units)")
    a = ap.parse_args()
    with open(a.mesh) as fh:
        mesh = json.load(fh)
    box = [float(c) for c in a.box.split(",")] if a.box else None
    render(mesh, load_textures(a.jar, mesh["layers"]), a.view, a.px, box).save(a.out)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
