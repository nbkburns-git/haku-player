"""Turns the frame dumps into light levels (nits) and compares the modes.
usage: python -I analyze.py <results dir> <SDR white nits from the log>"""
import json, sys, os
import numpy as np

res, sdr_white = sys.argv[1], float(sys.argv[2])

BT2020_TO_709 = np.array([[1.6605, -0.5876, -0.0728], [-0.1246, 1.1329, -0.0083], [-0.0182, -0.1006, 1.1187]])

def load(name):
    p = os.path.join(res, name + '.raw')
    if not os.path.exists(p): return None
    with open(p, 'rb') as f:
        h = json.loads(f.readline()); data = f.read()
    w, hgt = h['w'], h['h']
    if h['mode'] == 'fp16':
        px = np.frombuffer(data, np.float16).reshape(hgt, w, 4)[..., :3].astype(np.float64)
        return px * 80.0  # scRGB: 1.0 = 80 nits
    if h['mode'] == 'pq':
        v = np.frombuffer(data, np.uint32).reshape(hgt, w)
        rgb = np.stack([(v >> s) & 1023 for s in (0, 10, 20)], -1) / 1023.0
        m1, m2, c1, c2, c3 = 0.1593017578125, 78.84375, 0.8359375, 18.8515625, 18.6875
        e = rgb ** (1 / m2)
        nits = 10000 * (np.maximum(e - c1, 0) / (c2 - c3 * e)) ** (1 / m1)
        return nits @ BT2020_TO_709.T
    px = np.frombuffer(data, np.uint8).reshape(hgt, w, 4)[..., [2, 1, 0]] / 255.0  # BGRA
    lin = np.where(px <= 0.04045, px / 12.92, ((px + 0.055) / 1.055) ** 2.4)  # how Windows reads SDR content
    return lin * sdr_white

def lum(x): return x @ np.array([0.2126, 0.7152, 0.0722])

def report(label, a, b):
    """b against a: overall brightness ratio and how far apart the pixels are"""
    if a is None or b is None or a.shape != b.shape: print(f'  {label}: missing or different sizes'); return
    la, lb = lum(a), lum(b)
    mask = la > 2
    ratio = np.median(lb[mask] / la[mask])
    diff = np.abs(b - a)[mask]
    rel = np.median(diff / np.maximum(a[mask], 1))
    print(f'  {label}: brightness x{ratio:.3f}, typical colour difference {rel * 100:.1f}%')

for clip in ('sdr', 'hdr'):
    print(f'== {clip.upper()} clip ==')
    frames = {m: load(f'dump-{clip}-{m}-glass0') for m in ('sdr', 'pq', 'fp16')}
    for m, f in frames.items():
        if f is None: print(f'  {m}: no dump'); continue
        l = lum(f)
        h = f.shape[0]
        ramp = l[int(h * 0.80):int(h * 0.86)]  # the 0..10,000-nit ramp along the bottom (inside the letterboxed picture)
        print(f'  {m:5}: brightest 99.9% {np.percentile(l, 99.9):7.0f} nits, ramp max {ramp.max():7.0f} nits, median {np.median(l):6.1f} nits')
    report('pq vs sdr', frames['sdr'], frames['pq'])
    report('fp16 vs sdr', frames['sdr'], frames['fp16'])

print('== glass (SDR clip): brightness change inside the glass rectangle ==')
for m in ('sdr', 'pq', 'fp16'):
    a, b = load(f'dump-sdr-{m}-glass0'), load(f'dump-sdr-{m}-glass1')
    if a is None or b is None: print(f'  {m}: missing'); continue
    h, w = a.shape[:2]
    # glass rectangle in 0..1 of the window: x .3-.7, y .3-.55 (shader opts in Video.cs); the middle of it
    r = (slice(int(h * 0.36), int(h * 0.49)), slice(int(w * 0.36), int(w * 0.64)))
    print(f'  {m:5}: inside glass x{np.mean(lum(b[r])) / max(np.mean(lum(a[r])), 1e-6):.3f} brighter, '
          f'mean {np.mean(lum(b[r])):.1f} nits (no glass {np.mean(lum(a[r])):.1f})')
