"""Export an independent Condor reference fixture for resource-reader regression tests.

Test-time dependencies: UnityPy. This output is NOT embedded in the mod.
Only model, material and sampled flight animation data is exported; no scene or AI.
Usage: python Tools/export_condor_visual.py --peak-root D:/Steam/steamapps/common/PEAK
"""

import argparse
import bisect
import gzip
import io
import math
from pathlib import Path
import struct
import zlib

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler


def values(v, names='xyz'):
    return tuple(getattr(v, n) for n in names)


def quaternion(euler):
    # Unity's Transform Euler order is Z, X, Y (qy * qx * qz).
    x, y, z = [math.radians(v) / 2 for v in euler]
    sx, cx, sy, cy, sz, cz = math.sin(x), math.cos(x), math.sin(y), math.cos(y), math.sin(z), math.cos(z)
    return (cy * sx * cz + sy * cx * sz, sy * cx * cz - cy * sx * sz,
            cy * cx * sz - sy * sx * cz, cy * cx * cz + sy * sx * sz)


def sample_clip(ptr, paths, defaults):
    clip = ptr.read_typetree()
    muscle = clip['m_MuscleClip']
    data = muscle['m_Clip']['data']
    streamed = data['m_StreamedClip']
    raw = struct.pack('<%dI' % len(streamed['data']), *streamed['data'])
    curves = [[] for _ in range(streamed['curveCount'])]
    offset = 0
    while offset < len(raw):
        time, count = struct.unpack_from('<fI', raw, offset)
        offset += 8
        for _ in range(count):
            index, a, b, c, d = struct.unpack_from('<I4f', raw, offset)
            offset += 20
            curves[index].append((time, a, b, c, d))
    assert data['m_DenseClip']['m_CurveCount'] == 0, 'Unexpected dense animation curves'
    constant = data['m_ConstantClip']['data']
    times = [[k[0] for k in curve] for curve in curves]

    def at(index, time):
        if index >= len(curves):
            return constant[index - len(curves)]
        key = curves[index][max(0, bisect.bisect_right(times[index], time) - 1)]
        # The -FLT_MAX sentinel supplies defaults before the first actual key.
        dt = 0 if key[0] < -1e20 else time - key[0]
        return ((key[1] * dt + key[2]) * dt + key[3]) * dt + key[4]

    bindings = []
    index = 0
    for b in clip['m_ClipBindingConstant']['genericBindings']:
        count = (4 if b['attribute'] == 2 else 3) if b['typeID'] == 4 else 1
        if b['typeID'] == 4:
            assert b['path'] in paths
            bindings.append((paths[b['path']], b['attribute'], index, count))
        index += count
    assert index == len(curves) + len(constant)
    duration = muscle['m_StopTime'] - muscle['m_StartTime']
    frame_count = round(duration * 60) + 1
    frames = []
    for frame in range(frame_count):
        time = muscle['m_StartTime'] + min(duration, frame / 60)
        pose = [[list(p), list(q), list(s)] for p, q, s in defaults]
        for node, attr, first, count in bindings:
            v = [at(first + k, time) for k in range(count)]
            if attr == 1:
                pose[node][0] = v
            elif attr == 2:
                mag = math.sqrt(sum(x * x for x in v))
                pose[node][1] = [x / mag for x in v]
            elif attr == 3:
                pose[node][2] = v
            elif attr == 4:
                pose[node][1] = quaternion(v)
            else:
                raise AssertionError('Unexpected transform binding')
        flat = [x for node in pose for part in node for x in part]
        assert all(math.isfinite(x) for x in flat)
        frames.append(flat)
    return clip['m_Name'], duration, frames


def export(peak_root, output):
    env = UnityPy.load(str(peak_root / 'PEAK_Data' / 'level4'))
    model = next(o.read() for o in env.objects if o.assets_file.name == 'level4'
                 and o.type.name == 'GameObject' and o.read().m_Name == 'Condor1')
    model_tf = next(p.component.read() for p in model.m_Component if p.component.type.name == 'Transform')
    controller = next(p.component.read().m_Controller.read() for p in model.m_Component if p.component.type.name == 'Animator')
    nodes, paths, defaults, transforms, renderers = [], {}, [], {}, []

    def walk(tf, parent, path):
        go = tf.m_GameObject.read()
        index = len(nodes)
        nodes.append((parent, go.m_Name, path))
        paths[zlib.crc32(path.encode()) & 0xffffffff] = index
        transforms[tf.object_reader.path_id] = index
        defaults.append((values(tf.m_LocalPosition), values(tf.m_LocalRotation, 'xyzw'), values(tf.m_LocalScale)))
        for pair in go.m_Component:
            if pair.component.type.name == 'SkinnedMeshRenderer':
                renderers.append((index, pair.component.read()))
        for child in tf.m_Children:
            child = child.read()
            name = child.m_GameObject.read().m_Name
            walk(child, index, path + '/' + name if path else name)

    walk(model_tf, -1, '')
    assert len(renderers) == 1
    renderer_index, renderer = renderers[0]
    mesh = renderer.m_Mesh.read()
    handler = MeshHandler(mesh)
    handler.process()
    clips = [sample_clip(p, paths, defaults) for p in controller.m_AnimationClips
             if p.read().m_Name in ('CondorFly', 'CondorFlap')]
    assert {c[0] for c in clips} == {'CondorFly', 'CondorFlap'}

    # The talon anchor is below Armature/Root, relative to the scene Condor root.
    anchors = [i for i, (_, name, _) in enumerate(nodes) if name == 'DragToPoint']
    assert len(anchors) == 1, 'Condor drag point not found'
    def cross(a, b):
        return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
    anchor = (0, 0, 0)
    index = anchors[0]
    while index >= 0:
        p, q, scale = defaults[index]
        v = tuple(anchor[i] * scale[i] for i in range(3))
        uv = cross(q[:3], v)
        uuv = cross(q[:3], uv)
        anchor = tuple(p[i] + v[i] + 2*(q[3]*uv[i]+uuv[i]) for i in range(3))
        index = nodes[index][0]

    out = io.BytesIO()
    def ints(*v): out.write(struct.pack('<%di' % len(v), *v))
    def floats(*v): out.write(struct.pack('<%df' % len(v), *v))
    def string(v):
        b = v.encode('utf-8'); ints(len(b)); out.write(b)
    def vector_array(vectors, components):
        ints(len(vectors))
        for v in vectors: floats(*v[:components])

    out.write(b'CND1')
    floats(*anchor)
    ints(len(nodes))
    for (parent, name, _), default in zip(nodes, defaults):
        ints(parent); string(name)
        for v in default: floats(*v)
    ints(renderer_index, transforms[renderer.m_RootBone.path_id])
    ints(len(renderer.m_Bones), *(transforms[p.path_id] for p in renderer.m_Bones))
    vector_array(handler.m_Vertices, 3)
    vector_array(handler.m_Normals, 3)
    vector_array(handler.m_Tangents, 4)
    vector_array(handler.m_UV0, 2)
    ints(len(mesh.m_BindPose))
    for m in mesh.m_BindPose:
        floats(*(getattr(m, f'e{r}{c}') for r in range(4) for c in range(4)))
    ints(handler.m_VertexCount)
    for indices, weights in zip(handler.m_BoneIndices, handler.m_BoneWeights):
        assert abs(sum(weights) - 1) < 0.001
        assert all(0 <= i < len(renderer.m_Bones) for i in indices)
        ints(*indices); floats(*weights)
    triangles = handler.get_triangles()
    ints(len(triangles))
    for sub in triangles:
        flat = [i for tri in sub for i in tri]
        assert all(0 <= i < handler.m_VertexCount for i in flat)
        ints(len(flat), *flat)
    ints(len(renderer.m_Materials))
    for ptr in renderer.m_Materials:
        mat = ptr.read()
        string(mat.m_Name); string(mat.m_Shader.read().m_ParsedForm.m_Name)
        props = mat.m_SavedProperties
        ints(len(props.m_Floats))
        for name, value in props.m_Floats: string(name); floats(value)
        ints(len(props.m_Colors))
        for name, value in props.m_Colors: string(name); floats(*values(value, 'rgba'))
        textures = [(n, t) for n, t in props.m_TexEnvs if t.m_Texture.path_id != 0]
        ints(len(textures))
        for name, tex in textures:
            string(name); floats(*values(tex.m_Scale, 'xy'), *values(tex.m_Offset, 'xy'))
            png = io.BytesIO()
            tex.m_Texture.read().image.save(png, format='PNG')
            blob = png.getvalue(); ints(len(blob)); out.write(blob)
    ints(len(clips))
    for name, duration, frames in clips:
        string(name); floats(duration); ints(len(frames))
        for frame in frames: floats(*frame)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(gzip.compress(out.getvalue(), mtime=0))
    print(f'Exported {len(nodes)} transforms, {handler.m_VertexCount} vertices, '
          f'{len(renderer.m_Bones)} bones, {len(clips)} original flight animations, anchor={anchor}')
    print(f'{output}: {len(out.getvalue()):,} bytes raw, {output.stat().st_size:,} bytes compressed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--peak-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / '.build/condor-reference.bin')
    args = parser.parse_args()
    export(args.peak_root, args.output)
