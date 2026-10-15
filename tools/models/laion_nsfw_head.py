"""Exports LAION's CLIP-based NSFW detector for ViT-B/32 to the plain weights file the app reads
(src/ImmichSplash.Core/Models/nsfw-clip-b32-head.bin), without needing TensorFlow.

Source: https://github.com/LAION-AI/CLIP-based-NSFW-Detector (MIT), clip_autokeras_nsfw_b32.zip.
Usage: python laion_nsfw_head.py <extracted clip_autokeras_nsfw_b32 folder> <out.bin>
Only reads the checkpoint's index and raw weight bytes; nothing from the download is executed.
Layout (little-endian float32): normalisation mean[512], variance[512], dense kernel[512x32], bias[32],
dense_1 kernel[32x32], bias[32], dense_2 kernel[32x1], bias[1]. The final activation is a sigmoid.
"""
import struct, sys, numpy as np

def varint(b, i):
    r = s = 0
    while True:
        c = b[i]; i += 1
        r |= (c & 0x7f) << s; s += 7
        if c < 0x80: return r, i

def block_entries(b):
    nrest = struct.unpack('<I', b[-4:])[0]
    end = len(b) - 4 - 4 * nrest
    i, key = 0, b''
    while i < end:
        shared, i = varint(b, i); nons, i = varint(b, i); vlen, i = varint(b, i)
        key = key[:shared] + b[i:i + nons]; i += nons
        yield key, b[i:i + vlen]; i += vlen

def handle(b, i):
    off, i = varint(b, i); size, i = varint(b, i); return off, size, i

def proto(b):
    i, out = 0, {}
    while i < len(b):
        tag, i = varint(b, i); f, wt = tag >> 3, tag & 7
        if wt == 0: v, i = varint(b, i)
        elif wt == 2: n, i = varint(b, i); v = b[i:i + n]; i += n
        elif wt == 5: v = b[i:i + 4]; i += 4
        elif wt == 1: v = b[i:i + 8]; i += 8
        else: raise ValueError(wt)
        out.setdefault(f, []).append(v)
    return out

def read(index_path, data_path):
    idx = open(index_path, 'rb').read(); data = open(data_path, 'rb').read()
    footer = idx[-48:]
    _, _, j = handle(footer, 0); ioff, isize, _ = handle(footer, j)
    tensors = {}
    for _, hv in block_entries(idx[ioff:ioff + isize]):
        boff, bsize, _ = handle(hv, 0)
        blk = idx[boff:boff + bsize]
        for key, val in block_entries(blk):
            if not key: continue
            e = proto(val)
            dtype = e.get(1, [0])[0]
            if dtype != 1: continue  # float32 only
            shape = [proto(d).get(1, [0])[0] for d in proto(e[2][0]).get(2, [])] if 2 in e else []
            off = e.get(4, [0])[0]; size = e.get(5, [0])[0]
            tensors[key.decode()] = np.frombuffer(data, '<f4', size // 4, off).reshape(shape)
    return tensors

if __name__ == '__main__':
    src, out = sys.argv[1], sys.argv[2]
    t = read(src + '/variables/variables.index', src + '/variables/variables.data-00000-of-00001')
    def w(layer, name): return t[f'layer_with_weights-{layer}/{name}/.ATTRIBUTES/VARIABLE_VALUE'].astype('<f4')
    parts = [w(1, 'mean'), w(1, 'variance'), w(2, 'kernel'), w(2, 'bias'), w(3, 'kernel'), w(3, 'bias'), w(4, 'kernel'), w(4, 'bias')]
    open(out, 'wb').write(b''.join(p.ravel(order='C').tobytes() for p in parts))
