"""Is the glass rectangle blurred? Sharpness inside it with and without glass, plus PNG crops to look at.
usage: python -I glass-check.py <results dir> <out dir>"""
import json, os, sys, zlib, struct
import numpy as np

res, outdir = sys.argv[1], sys.argv[2]

def load_sdr(name):
    with open(os.path.join(res, name + '.raw'), 'rb') as f:
        h = json.loads(f.readline()); d = f.read()
    return np.frombuffer(d, np.uint8).reshape(h['h'], h['w'], 4)[..., [2, 1, 0]].astype(np.float64)

def png(path, rgb):
    rgb = np.clip(rgb, 0, 255).astype(np.uint8)
    h, w, _ = rgb.shape
    raw = b''.join(b'\x00' + rgb[y].tobytes() for y in range(h))
    def chunk(t, b): return struct.pack('>I', len(b)) + t + b + struct.pack('>I', zlib.crc32(t + b) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))

a, b = load_sdr('dump-sdr-sdr-glass0'), load_sdr('dump-sdr-sdr-glass1')
h, w = a.shape[:2]
r = (slice(int(h * 0.36), int(h * 0.49)), slice(int(w * 0.36), int(w * 0.64)))
def sharp(x): g = x.mean(-1); return np.mean(np.abs(np.diff(g, axis=1))) + np.mean(np.abs(np.diff(g, axis=0)))
print(f'sharpness inside the glass: no glass {sharp(a[r]):.2f}, glass {sharp(b[r]):.2f} (lower = blurrier)')
outside = (slice(int(h * 0.05), int(h * 0.2)), slice(int(w * 0.05), int(w * 0.25)))
print(f'outside the glass, changed pixels: {np.mean(np.abs(a[outside] - b[outside]) > 1) * 100:.2f}%')
crop = (slice(int(h * 0.22), int(h * 0.63)), slice(int(w * 0.22), int(w * 0.78)))
png(os.path.join(outdir, 'glass-off.png'), a[crop]); png(os.path.join(outdir, 'glass-on.png'), b[crop])
