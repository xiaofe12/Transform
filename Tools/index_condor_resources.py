"""Index installed Condor assets without distributing meshes, textures or animation.

Requires UnityPy at build time only. Runtime reads checked byte ranges from the
user's PEAK_Data directory; the small manifest contains hierarchy and read layouts.
Regenerate after game resource changes; hashes reject incompatible asset versions.
"""
import argparse
import gzip
import hashlib
import io
from pathlib import Path
import struct
import zlib
from copy import copy

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
from UnityPy.streams import EndianBinaryWriter


def audio_bundle_layout(data_dir, sources):
    """Return a tiny native bundle header and checked stream ranges, never audio payload.

    Unity's Vorbis clips use FSB rather than Ogg. Let Unity decode its own AudioClips
    by assembling a transient uncompressed bundle from the installed bytes at runtime.
    The map manifest supplies only the matching native AssetBundle serialization layout.
    """
    from UnityPy.helpers.UnityVersion import UnityVersion
    # PEAK's map pipeline appends a build marker to the serialized Unity version.
    original = UnityVersion.from_str.__func__
    UnityVersion.from_str = classmethod(lambda cls, value: original(cls, value.splitlines()[0]))
    try:
        maps = UnityPy.load(str(data_dir / 'StreamingAssets/maps/maps'))
    finally:
        UnityVersion.from_str = classmethod(original)
    bundle_object = next(o for o in maps.objects if o.type.name == 'AssetBundle')
    clips = [source.m_Resource.deref() for _, source in sources]
    assets = copy(clips[0].assets_file)
    assets.name = 'CAB-transform-condor-audio'
    assets.objects = {}
    assets.types = [copy(clips[0].serialized_type), copy(bundle_object.serialized_type)]
    assets._enable_type_tree = False
    assets.externals, assets.script_types, assets.ref_types = [], [], []
    assets.unity_version = assets.unity_version.splitlines()[0]
    ranges, container, offset = [], [], 0
    for i, original_clip in enumerate(clips, 2):
        clip = copy(original_clip)
        clip.assets_file, clip.type_id, clip.serialized_type, clip.path_id = assets, 0, assets.types[0], i
        tree = original_clip.read_typetree()
        resource = tree['m_Resource']
        ranges.append((Path(resource['m_Source']).name, resource['m_Offset'], resource['m_Size']))
        resource['m_Source'] = f'archive:/{assets.name}/{assets.name}.resource'
        resource['m_Offset'] = offset
        offset += resource['m_Size']
        tree['m_PreloadAudioData'] = True
        clip.save_typetree(tree)
        assets.objects[i] = clip
        container.append([tree['m_Name'].lower(), {'preloadIndex': i-2, 'preloadSize': 1,
                          'asset': {'m_FileID': 0, 'm_PathID': i}}])
    obj = copy(bundle_object)
    obj.assets_file, obj.type_id, obj.serialized_type, obj.path_id = assets, 1, assets.types[1], 1
    tree = bundle_object.read_typetree()
    tree.update(m_Name='transform-condor-audio', m_AssetBundleName='transform-condor-audio',
                m_Container=container, m_PreloadTable=[{'m_FileID': 0, 'm_PathID': i} for i in range(2, 2+len(clips))])
    obj.save_typetree(tree)
    assets.objects[1] = obj
    serialized = assets.save()
    # One uncompressed UnityFS block: serialized clip metadata followed by native FSBs.
    info = EndianBinaryWriter(b'\0'*16)
    info.write_int(1)
    info.write_u_int(len(serialized)+offset); info.write_u_int(len(serialized)+offset); info.write_u_short(0)
    info.write_int(2)
    for name, start, size, flag in ((assets.name, 0, len(serialized), 4),
                                   (assets.name+'.resource', len(serialized), offset, 0)):
        info.write_long(start); info.write_long(size); info.write_u_int(flag); info.write_string_to_null(name)
    header = EndianBinaryWriter()
    header.write_string_to_null('UnityFS'); header.write_u_int(7)
    header.write_string_to_null('5.x.x'); header.write_string_to_null(assets.unity_version)
    size_position = header.Position
    header.write_long(0); header.write_u_int(len(info.bytes)); header.write_u_int(len(info.bytes)); header.write_u_int(64)
    header.align_stream(16); header.write_bytes(info.bytes); header.write_bytes(serialized)
    total = header.Position + offset
    header.Position = size_position; header.write_long(total)
    return header.bytes, ranges


def index(peak_root, output):
    data_dir = peak_root / 'PEAK_Data'
    env = UnityPy.load(str(data_dir / 'level4'))
    model = next(o.read() for o in env.objects if o.assets_file.name == 'level4'
                 and o.type.name == 'GameObject' and o.read().m_Name == 'Condor1')
    tf = next(p.component.read() for p in model.m_Component if p.component.type.name == 'Transform')
    controller = next(p.component.read().m_Controller.read() for p in model.m_Component
                      if p.component.type.name == 'Animator')
    nodes, paths, transforms, defaults, renderers, trails, audio = [], {}, {}, [], [], [], []
    def vec(v, fields='xyz'): return tuple(getattr(v, c) for c in fields)
    def walk(t, parent, path):
        go = t.m_GameObject.read()
        i = len(nodes)
        nodes.append((parent, go.m_Name))
        paths[zlib.crc32(path.encode()) & 0xffffffff] = i
        transforms[t.object_reader.path_id] = i
        defaults.append((vec(t.m_LocalPosition), vec(t.m_LocalRotation, 'xyzw'), vec(t.m_LocalScale)))
        for pair in go.m_Component:
            if pair.component.type.name == 'SkinnedMeshRenderer': renderers.append((i, pair.component.read()))
            if pair.component.type.name == 'TrailRenderer': trails.append((i, pair.component.read_typetree(), pair.component.read()))
            if pair.component.type.name == 'AudioSource': audio.append((i, pair.component.read()))
        for ptr in t.m_Children:
            child = ptr.read(); name = child.m_GameObject.read().m_Name
            walk(child, i, path + '/' + name if path else name)
    walk(tf, -1, '')
    assert len(renderers) == 1
    renderer_index, renderer = renderers[0]
    mesh = renderer.m_Mesh.read()
    handler = MeshHandler(mesh); handler.process()
    streams = handler.get_streams(mesh.m_VertexData.m_Channels, handler.m_VertexCount)
    assert mesh.m_IndexFormat == 0 and mesh.m_CompressedMesh.m_Vertices.m_NumItems == 0
    anchor = next(i for i, (_, name) in enumerate(nodes) if name == 'DragToPoint')
    # Accumulate the original transform chain in double precision, same as exporter.
    p = (0, 0, 0); i = anchor
    def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
    while i >= 0:
        pos, q, scale = defaults[i]; v = tuple(p[k]*scale[k] for k in range(3))
        uv = cross(q[:3], v); uuv = cross(q[:3], uv)
        p = tuple(pos[k]+v[k]+2*(q[3]*uv[k]+uuv[k]) for k in range(3)); i = nodes[i][0]
    out = io.BytesIO(); ranges = []
    def ints(*v): out.write(struct.pack('<%di' % len(v), *v))
    def floats(*v): out.write(struct.pack('<%df' % len(v), *v))
    def string(v):
        b = v.encode('utf-8'); ints(len(b)); out.write(b)
    def blob(filename, offset, data):
        assert filename == Path(filename).name and len(data) > 0
        with (data_dir / filename).open('rb') as f:
            f.seek(offset); assert f.read(len(data)) == data
        string(filename); out.write(struct.pack('<qi', offset, len(data)))
        out.write(hashlib.sha256(data).digest()); ranges.append((filename, offset, len(data)))
    def field(obj, data):
        raw = obj.object_reader.get_raw_data()
        start = raw.find(data)
        assert start >= 0 and raw.find(data, start + 1) < 0, 'Field must have a unique byte range'
        blob(obj.object_reader.assets_file.name, obj.object_reader.byte_start + start, data)
    out.write(b'CNR3'); floats(*p); ints(len(nodes))
    for (parent, name), pose in zip(nodes, defaults):
        ints(parent); string(name)
        for v in pose: floats(*v)
    ints(renderer_index, transforms[renderer.m_RootBone.path_id])
    ints(len(renderer.m_Bones), *(transforms[b.path_id] for b in renderer.m_Bones))
    ints(handler.m_VertexCount)
    field(mesh, bytes(mesh.m_VertexData.m_DataSize))
    for ch in (0, 1, 2, 4, 12, 13):
        c = mesh.m_VertexData.m_Channels[ch]; s = streams[c.stream]
        assert c.format in (0, 1, 10)
        ints(s.offset+c.offset, s.stride, c.format, c.dimension)
    ints(len(mesh.m_BindPose))
    field(mesh, struct.pack('<%df' % (len(mesh.m_BindPose)*16),
                           *(getattr(m, f'e{r}{c}') for m in mesh.m_BindPose for r in range(4) for c in range(4))))
    field(mesh, bytes(mesh.m_IndexBuffer))
    ints(len(mesh.m_SubMeshes))
    for sub in mesh.m_SubMeshes:
        assert sub.topology == 0
        ints(sub.firstByte, sub.indexCount, sub.baseVertex)
    def material(ptr):
        mat = ptr.read(); string(mat.m_Name); string(mat.m_Shader.read().m_ParsedForm.m_Name)
        settings = mat.object_reader.read_typetree()
        keywords = settings['m_ValidKeywords']
        ints(len(keywords))
        for keyword in keywords: string(keyword)
        ints(settings['m_CustomRenderQueue'], settings['m_LightmapFlags'], settings['m_EnableInstancingVariants'], settings['m_DoubleSidedGI'])
        ints(len(settings['stringTagMap']))
        for key, value in settings['stringTagMap']: string(key); string(value)
        ints(len(settings['disabledShaderPasses']))
        for value in settings['disabledShaderPasses']: string(value)
        props = mat.m_SavedProperties
        assert not props.m_Ints, 'Add integer material properties if used by a later game build'
        ints(len(props.m_Floats))
        for name, value in props.m_Floats: string(name); floats(value)
        ints(len(props.m_Colors))
        for name, value in props.m_Colors: string(name); floats(*vec(value, 'rgba'))
        textures = [(n, t) for n, t in props.m_TexEnvs if t.m_Texture.path_id]
        ints(len(textures))
        for name, tex in textures:
            t = tex.m_Texture.read()
            string(name); floats(*vec(tex.m_Scale, 'xy'), *vec(tex.m_Offset, 'xy'))
            ints(t.m_Width, t.m_Height, t.m_TextureFormat, t.m_MipCount, t.m_ColorSpace)
            sampling = t.m_TextureSettings
            string(t.m_Name)
            ints(sampling.m_FilterMode, sampling.m_Aniso, sampling.m_WrapU, sampling.m_WrapV, sampling.m_WrapW)
            floats(sampling.m_MipBias)
            assert t.m_StreamData.path and not t.image_data
            s = t.m_StreamData
            with (data_dir / Path(s.path).name).open('rb') as f:
                f.seek(s.offset); data = f.read(s.size)
            blob(Path(s.path).name, s.offset, data)
    ints(len(renderer.m_Materials))
    for ptr in renderer.m_Materials: material(ptr)
    clips = [c for c in controller.m_AnimationClips if c.read().m_Name in ('CondorFly','CondorFlap')]
    assert len(clips) == 2
    ints(len(clips))
    for ptr in clips:
        obj = ptr.read(); clip = ptr.read_typetree(); muscle = clip['m_MuscleClip']; d = muscle['m_Clip']['data']
        duration = muscle['m_StopTime'] - muscle['m_StartTime']
        string(clip['m_Name']); floats(duration, muscle['m_StartTime']); ints(round(duration*60)+1)
        streamed = d['m_StreamedClip']; ints(streamed['curveCount'])
        assert d['m_DenseClip']['m_CurveCount'] == 0
        field(obj, struct.pack('<%dI' % len(streamed['data']), *streamed['data']))
        constants = d['m_ConstantClip']['data']; ints(len(constants))
        field(obj, struct.pack('<%df' % len(constants), *constants))
        bindings = []; trail_bindings = []; audio_bindings = []; curve = 0
        for b in clip['m_ClipBindingConstant']['genericBindings']:
            n = (4 if b['attribute'] == 2 else 3) if b['typeID'] == 4 else 1
            if b['typeID'] == 4:
                assert b['attribute'] in (1,2,3,4)
                bindings.append((paths[b['path']], b['attribute'], curve, n))
            elif b['typeID'] == 96:
                assert b['attribute'] == zlib.crc32(b'm_Time') & 0xffffffff
                trail_bindings.append((paths[b['path']], curve))
            elif b['typeID'] == 82:
                assert b['attribute'] == zlib.crc32(b'm_Volume') & 0xffffffff
                audio_bindings.append((paths[b['path']], curve))
            else:
                raise ValueError(f"Unsupported animation binding: {b}")
            curve += n
        assert curve == streamed['curveCount'] + len(constants)
        ints(len(bindings))
        for b in bindings: ints(*b)
        ints(len(trail_bindings))
        for b in trail_bindings: ints(*b)
        ints(len(audio_bindings))
        for b in audio_bindings: ints(*b)
    # These are the original wing-tip effects, not a substitute particle effect.
    ints(len(trails))
    for node, trail, component in trails:
        params = trail['m_Parameters']
        ints(node, trail['m_Enabled'], trail['m_Emitting'])
        floats(trail['m_Time'], trail['m_MinVertexDistance'], params['widthMultiplier'])
        ints(params['numCornerVertices'], params['numCapVertices'], params['alignment'], params['textureMode'])
        floats(params['textureScale']['x'], params['textureScale']['y'], params['shadowBias'])
        ints(params['generateLightingData'], trail['m_ApplyActiveColorSpace'])
        ints(trail['m_CastShadows'], trail['m_ReceiveShadows'], trail['m_LightProbeUsage'], trail['m_ReflectionProbeUsage'], trail['m_MotionVectors'])
        ints(trail['m_SortingLayerID'], trail['m_SortingOrder'], trail['m_RenderingLayerMask'])
        curve = params['widthCurve']
        ints(curve['m_PreInfinity'], curve['m_PostInfinity'], len(curve['m_Curve']))
        for key in curve['m_Curve']:
            floats(*(key[k] for k in ('time','value','inSlope','outSlope','inWeight','outWeight')))
            ints(key['weightedMode'])
        gradient = params['colorGradient']
        ints(gradient['m_Mode'], gradient['m_ColorSpace'], gradient['m_NumColorKeys'], gradient['m_NumAlphaKeys'])
        for i in range(gradient['m_NumColorKeys']):
            floats(*(gradient[f'key{i}'][k] for k in 'rgb'), gradient[f'ctime{i}']/65535)
        for i in range(gradient['m_NumAlphaKeys']):
            floats(gradient[f'key{i}']['a'], gradient[f'atime{i}']/65535)
        assert len(component.m_Materials) == 1
        material(component.m_Materials[0])
    assert len(audio) == 2
    prefix, audio_ranges = audio_bundle_layout(data_dir, audio)
    ints(len(prefix)); out.write(prefix); ints(len(audio_ranges))
    for filename, offset, count in audio_ranges:
        with (data_dir / filename).open('rb') as f:
            f.seek(offset); payload = f.read(count)
        assert payload[:4] == b'FSB5'
        blob(filename, offset, payload)
    ints(len(audio))
    for node, source in audio:
        assert source.Loop and source.m_PlayOnAwake
        ints(node); string(source.m_Resource.read().m_Name.lower())
        string(source.OutputAudioMixerGroup.read().m_Name)
        floats(source.m_Volume, source.m_Pitch, source.MinDistance, source.MaxDistance, source.DopplerLevel)
        for curve_name, multiplier in (('panLevelCustomCurve', 1), ('spreadCustomCurve', 360), ('reverbZoneMixCustomCurve', 1)):
            keys = getattr(source, curve_name).m_Curve
            assert len(keys) == 1
            floats(keys[0].value * multiplier)
        ints(source.Priority, source.rolloffMode)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(gzip.compress(out.getvalue(), mtime=0))
    print(f'Indexed {len(nodes)} transforms, {handler.m_VertexCount} vertices, {len(clips)} clips')
    print(f'{len(ranges)} checked ranges ({sum(r[2] for r in ranges):,} bytes read from game), '
          f'manifest {output.stat().st_size:,} bytes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--peak-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'Forms/Raven/Assets/CondorResources.bin')
    args = parser.parse_args(); index(args.peak_root, args.output)
