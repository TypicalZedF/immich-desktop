"""Converts the NSFWJS MobileNetV2 (mid) TF.js graph model to ONNX (NHWC float input in [0,1], 5 softmax outputs).

Source: the npm package nsfwjs 4.4.0, dist/models/mobilenet_v2_mid (its .min.js files wrap model.json and
the weight shards; save those as model.json and <shard>.bin). Usage: python nsfwjs_to_onnx.py <dir> <out.onnx>
Only reads the model's JSON topology and raw weight bytes; nothing from the download is executed.
Weights stay 8-bit in the file and are expanded when the model loads (outputs match TF.js to ~1e-4).
"""
import json, base64, sys, os
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

src, out = sys.argv[1], sys.argv[2]
j = json.load(open(os.path.join(src, 'model.json')))
manifest = j['weightsManifest'][0]
blob = b''.join(open(os.path.join(src, p + '.bin'), 'rb').read() for p in manifest['paths'])
weights, quant, off = {}, {}, 0
for w in manifest['weights']:
    n = int(np.prod(w['shape'])) if w['shape'] else 1
    q = w.get('quantization')
    if q:
        assert q['dtype'] == 'uint8', q
        raw = np.frombuffer(blob, dtype=np.uint8, count=n, offset=off)
        val = raw.astype(np.float32) * np.float32(q['scale']) + np.float32(q['min'])
        quant[w['name']] = (raw.reshape(w['shape']), np.float32(q['scale']), np.float32(q['min']))
        off += n
    else:
        val = np.frombuffer(blob, dtype=np.float32, count=n, offset=off).copy()
        off += n * 4
    weights[w['name']] = val.reshape(w['shape'])

def attr(node, key):
    return node.get('attr', {}).get(key, {})
def strs(node, key):
    return [base64.b64decode(s).decode() for s in attr(node, key).get('list', {}).get('s', [])]
def ints(node, key):
    return [int(i) for i in attr(node, key).get('list', {}).get('i', [])]

inits, nodes = [], []
names = {}    # tf name -> onnx name
spatial = {}  # onnx name -> (h, w) of NCHW tensors
counter = [0]
def uniq(base):
    counter[0] += 1
    return f"{base}_{counter[0]}"
def const(name, arr):
    inits.append(numpy_helper.from_array(np.asarray(arr, dtype=np.float32), name))
    return name
def weight(tf_name, layout=lambda a: a):
    """A weight, kept as the original 8-bit values plus a scale (4x smaller; expanded when the model loads)."""
    if tf_name not in quant:
        return const(uniq('w'), layout(weights[tf_name]))
    raw, scale, low = quant[tf_name]
    q = uniq('q')
    inits.append(numpy_helper.from_array(np.ascontiguousarray(layout(raw)).astype(np.uint8), q))
    f, m, o = uniq('f'), uniq('m'), uniq('w')
    nodes.append(helper.make_node('Cast', [q], [f], to=TensorProto.FLOAT))
    nodes.append(helper.make_node('Mul', [f, const(uniq('s'), scale)], [m]))
    nodes.append(helper.make_node('Add', [m, const(uniq('z'), low)], [o]))
    return o
def ref(inp):
    inp = inp.split(':')[0]
    if inp in weights and inp not in names:
        names[inp] = weight(inp)
    return names[inp]

def tf_pads(h, w, kh, kw, sh, sw, padding):
    if padding == 'VALID':
        return [0, 0, 0, 0], ((h - kh) // sh + 1, (w - kw) // sw + 1)
    oh, ow = -(-h // sh), -(-w // sw)
    th = max((oh - 1) * sh + kh - h, 0)
    tw = max((ow - 1) * sw + kw - w, 0)
    return [th // 2, tw // 2, th - th // 2, tw - tw // 2], (oh, ow)

for n in j['modelTopology']['node']:
    op, name, ins = n['op'], n['name'], n.get('input', [])
    if op == 'Const':
        continue
    if op == 'Placeholder':
        names[name] = 'input'
        continue
    if op in ('Mul', 'Sub', 'AddV2'):
        a, b = ref(ins[0]), ref(ins[1])
        o = uniq(op)
        nodes.append(helper.make_node({'Mul': 'Mul', 'Sub': 'Sub', 'AddV2': 'Add'}[op], [a, b], [o]))
        names[name] = o
        spatial[o] = spatial.get(a) or spatial.get(b)
        # The hub preprocessing (x*2-1) runs on the NHWC input; switch to NCHW right after it.
        if op == 'Sub' and 'hub_input' in name:
            t = uniq('nchw')
            nodes.append(helper.make_node('Transpose', [o], [t], perm=[0, 3, 1, 2]))
            names[name] = t
            spatial[t] = (224, 224)
        continue
    if op in ('_FusedConv2D', 'FusedDepthwiseConv2dNative'):
        x = ref(ins[0])
        wt = weights[ins[1].split(':')[0]]
        kh, kw = wt.shape[0], wt.shape[1]
        st = ints(n, 'strides')
        pad = base64.b64decode(attr(n, 'padding')['s']).decode()
        h, w = spatial[x]
        pads, (oh, ow) = tf_pads(h, w, kh, kw, st[1], st[2], pad)
        if op == '_FusedConv2D':
            layout, group = (lambda a: a.transpose(3, 2, 0, 1)), 1
        else:
            cin, mult = wt.shape[2], wt.shape[3]
            layout, group = (lambda a, cin=cin, mult=mult, kh=kh, kw=kw: a.transpose(2, 3, 0, 1).reshape(cin * mult, 1, kh, kw)), cin
        o = uniq('conv')
        nodes.append(helper.make_node('Conv', [x, weight(ins[1].split(':')[0], layout), weight(ins[2].split(':')[0])], [o],
                                      kernel_shape=[kh, kw], strides=[st[1], st[2]], pads=pads, group=group))
        fused = strs(n, 'fused_ops')
        assert fused[0] == 'BiasAdd' and set(fused[1:]) <= {'Relu6'}, fused
        if 'Relu6' in fused:
            r = uniq('relu6')
            nodes.append(helper.make_node('Clip', [o, const(uniq('lo'), 0.0), const(uniq('hi'), 6.0)], [r]))
            o = r
        names[name] = o
        spatial[o] = (oh, ow)
        continue
    if op == 'AvgPool':
        k = ints(n, 'ksize')
        o = uniq('pool')
        nodes.append(helper.make_node('AveragePool', [ref(ins[0])], [o], kernel_shape=[k[1], k[2]]))
        names[name] = o
        continue
    if op == 'Squeeze':
        o = uniq('flatten')
        nodes.append(helper.make_node('Flatten', [ref(ins[0])], [o], axis=1))  # [N,C,1,1] -> [N,C]
        names[name] = o
        continue
    if op == '_FusedMatMul':
        o, o2 = uniq('mm'), uniq('fc')
        nodes.append(helper.make_node('MatMul', [ref(ins[0]), weight(ins[1].split(':')[0])], [o]))
        nodes.append(helper.make_node('Add', [o, weight(ins[2].split(':')[0])], [o2]))
        names[name] = o2
        continue
    if op == 'Softmax':
        o = uniq('softmax')
        nodes.append(helper.make_node('Softmax', [ref(ins[0])], [o], axis=1))
        names[name] = o
        continue
    if op == 'Identity':
        nodes.append(helper.make_node('Identity', [ref(ins[0])], ['probabilities']))
        names[name] = 'probabilities'
        continue
    raise ValueError(f"unsupported op {op}")

graph = helper.make_graph(nodes, 'nsfw_mobilenet_v2',
    [helper.make_tensor_value_info('input', TensorProto.FLOAT, ['batch', 224, 224, 3])],
    [helper.make_tensor_value_info('probabilities', TensorProto.FLOAT, ['batch', 5])], inits)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid('', 13)], producer_name='immich-desktop')
model.ir_version = 8
for k, v in {'labels': 'Drawing,Hentai,Neutral,Porn,Sexy',
             'source': 'NSFWJS MobileNetV2 (mid) by Infinite Red, MIT license',
             'preprocessing': 'RGB, resized to 224x224 (stretched, bilinear), values 0..1, NHWC'}.items():
    entry = model.metadata_props.add()
    entry.key, entry.value = k, v
onnx.checker.check_model(model)
onnx.save(model, out)
print('saved', out, os.path.getsize(out))
