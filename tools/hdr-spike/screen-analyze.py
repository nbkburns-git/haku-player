"""What Windows showed (FP16 scRGB desktop captures): light levels in nits, and SDR vs PQ mode.
usage: python -I screen-analyze.py <results dir> [tag]"""
import json, os, sys
import numpy as np

res = sys.argv[1]; tag = sys.argv[2] if len(sys.argv) > 2 else ''

def load(name):
    p = os.path.join(res, name + '.raw')
    if not os.path.exists(p): return None
    with open(p, 'rb') as f:
        h = json.loads(f.readline()); d = f.read()
    return np.frombuffer(d, np.float16).reshape(h['h'], h['w'], 4)[..., :3].astype(np.float64) * 80.0

def lum(x): return x @ np.array([0.2126, 0.7152, 0.0722])

for clip in ('sdr', 'hdr'):
    print(f'== {clip.upper()} clip, as shown on screen ==')
    f = {m: load(f'screen-{clip}-{m}{tag}') for m in ('sdr', 'pq')}
    for m, x in f.items():
        if x is None: print(f'  {m}: missing'); continue
        l = lum(x); h = l.shape[0]
        ramp = l[int(h * 0.80):int(h * 0.86)]
        print(f'  {m:4} mode: brightest {np.percentile(l, 99.9):6.0f} nits, ramp max {ramp.max():6.0f}, median {np.median(l):6.1f}')
    a, b = f['sdr'], f['pq']
    if a is not None and b is not None and a.shape == b.shape:
        la, lb = lum(a), lum(b); m = la > 2
        rel = np.median(np.abs(b - a)[m] / np.maximum(a[m], 1))
        print(f'  pq vs sdr mode: brightness x{np.median(lb[m] / la[m]):.3f}, typical colour difference {rel * 100:.1f}%')
