"""Validate the Condor regression reference independently of Unity / a loaded map.

Checks binary layout, mesh and skinning indices, textures, hierarchy, animation
poses and EOF. This intentionally opens only the payload, not any game asset.
"""

import gzip
import io
import math
from pathlib import Path
import struct
from PIL import Image


def verify(path):
    data = io.BytesIO(gzip.decompress(path.read_bytes()))
    def integers(count=1): return struct.unpack('<%di' % count, data.read(count*4))
    def integer(): return integers()[0]
    def floats(count):
        v = struct.unpack('<%df' % count, data.read(count*4))
        assert all(math.isfinite(x) for x in v), 'Non-finite numeric value'
        return v
    def string(): return data.read(integer()).decode('utf-8')
    def vectors(components):
        count = integer()
        assert 0 < count < 100000
        return [floats(components) for _ in range(count)]

    assert data.read(4) == b'CND1'
    anchor = floats(3)
    assert max(abs(v) for v in anchor) < 10
    count = integer()
    nodes, defaults = [], []
    for i in range(count):
        parent, name = integer(), string()
        assert -1 <= parent < i
        assert (parent == -1) == (i == 0)
        nodes.append((parent, name))
        defaults.append(floats(10))
    renderer, root_bone = integers(2)
    assert 0 <= renderer < count and 0 <= root_bone < count
    bones = integers(integer())
    assert all(0 <= b < count for b in bones)
    vertices, normals, tangents, uv = vectors(3), vectors(3), vectors(4), vectors(2)
    assert len(vertices) == len(normals) == len(tangents) == len(uv)
    bind_count = integer()
    assert bind_count == len(bones)
    for _ in range(bind_count):
        matrix = floats(16)
        assert max(abs(a-b) for a,b in zip(matrix[-4:], (0,0,0,1))) < 0.0001
    weights = integer()
    assert weights == len(vertices)
    for _ in range(weights):
        indices, values = integers(4), floats(4)
        assert all(0 <= b < len(bones) for b in indices)
        assert min(values) >= 0 and abs(sum(values)-1) < 0.001
    submesh_count = integer()
    for _ in range(submesh_count):
        indices = integers(integer())
        assert len(indices) % 3 == 0
        assert all(0 <= i < len(vertices) for i in indices)
    mat_count = integer()
    assert mat_count == submesh_count
    textures = 0
    for _ in range(mat_count):
        name, shader = string(), string()
        assert shader in ('W/Peak_Standard', 'GD/Face Cards')
        for _ in range(integer()): string(); floats(1)
        for _ in range(integer()): string(); floats(4)
        for _ in range(integer()):
            string(); floats(4)
            image = Image.open(io.BytesIO(data.read(integer())))
            image.verify()
            textures += 1
    clips = {}
    for _ in range(integer()):
        name, duration, frame_count = string(), floats(1)[0], integer()
        assert frame_count == round(duration * 60) + 1
        frames = []
        for _ in range(frame_count):
            frame = floats(count * 10)
            for node in range(count):
                q = frame[node*10+3:node*10+7]
                assert abs(sum(v*v for v in q)-1) < 0.001, 'Non-unit animation rotation'
                scale = frame[node*10+7:node*10+10]
                assert all(0.01 < abs(v) < 10 for v in scale), 'Invalid bone scale'
            frames.append(frame)
        clips[name] = frames
    assert set(clips) == {'CondorFly','CondorFlap'}
    assert data.read() == b'', 'Payload layout differs from runtime reader'
    # Both clips must actually animate rather than just contain a static model.
    for name, frames in clips.items():
        assert max(abs(a-b) for a,b in zip(frames[0], frames[len(frames)//4])) > 0.01
    print(f'PASS: {count} hierarchy nodes, {len(vertices)} vertices, {len(bones)} bones, '
          f'{submesh_count} submeshes, {textures} valid PNG textures')
    print('PASS: original Fly/Flap poses are finite, have unit rotations, valid scales and moving bones')
    print('PASS: independent reader consumed the whole payload without loading a game scene')


if __name__ == '__main__':
    verify(Path(__file__).resolve().parents[1] / '.build/condor-reference.bin')
