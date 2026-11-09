"""Read-only checks of the native Raven resource used by CrowVisualAsset.

Requires UnityPy; no game assets are exported or embedded in the DLL.
"""
import argparse
from collections import Counter
from pathlib import Path
import zlib

import UnityPy
from export_condor_visual import sample_clip, values


def inspect(peak_root):
    data = peak_root / 'PEAK_Data'
    managers = UnityPy.load(str(data / 'globalgamemanagers'))
    resources = next(o.read_typetree() for o in managers.objects if o.type.name == 'ResourceManager')
    assert any(name.lower() == 'ravenvoicethief' for name, _ in resources['m_Container'])
    env = UnityPy.load(str(data / 'resources.assets'))
    root = next(o.read() for o in env.objects if o.assets_file.name == 'resources.assets'
                and o.type.name == 'GameObject' and o.peek_name() == 'RavenVoiceThief')
    nodes, animators, renderers, scripts, particles = [], [], [], [], []
    components = Counter()

    def walk(go, path=''):
        tf = next(p.component.read() for p in go.m_Component if p.component.type.name == 'Transform')
        nodes.append((tf, path))
        for pair in go.m_Component:
            ptr = pair.component
            components[ptr.type.name] += 1
            if ptr.type.name == 'Animator': animators.append(ptr.read())
            if ptr.type.name == 'SkinnedMeshRenderer': renderers.append(ptr.read())
            if ptr.type.name == 'ParticleSystem': particles.append((go, ptr.read(), ptr.read_typetree()))
            if ptr.type.name == 'MonoBehaviour': scripts.append(ptr.deref().read(check_read=False).m_Script.read().m_ClassName)
        for child in tf.m_Children:
            child_go = child.read().m_GameObject.read()
            walk(child_go, path + '/' + child_go.m_Name)

    walk(root)
    assert {'VoiceThief', 'PhotonView', 'PhotonCleanupHelper'} <= set(scripts)
    assert set(components) == {'Transform', 'MonoBehaviour', 'Animator', 'SkinnedMeshRenderer',
                               'ParticleSystem', 'ParticleSystemRenderer'}, components
    assert len(particles) == 1
    feather_go, feather, settings = particles[0]
    assert feather_go.m_Name == 'Feathers' and not feather_go.m_IsActive
    assert {p.component.type.name for p in feather_go.m_Component} == {'Transform', 'ParticleSystem', 'ParticleSystemRenderer'}
    feather_tf = next(p.component.read() for p in feather_go.m_Component if p.component.type.name == 'Transform')
    assert not feather_tf.m_Children
    assert settings['playOnAwake'] and not settings['looping'] and settings['lengthInSec'] == 1
    assert settings['EmissionModule']['enabled'] and settings['EmissionModule']['rateOverTime']['scalar'] == 30
    assert not settings['SubModule']['enabled'] and not settings['TrailModule']['enabled']
    feather_renderer = next(p.component.read() for p in feather_go.m_Component if p.component.type.name == 'ParticleSystemRenderer')
    for ptr in feather_renderer.m_Materials:
        material = ptr.read()
        textures = [entry.m_Texture.read() for _, entry in material.m_SavedProperties.m_TexEnvs if entry.m_Texture.path_id]
        assert material.m_Shader.path_id and textures
        for texture in textures:
            assert texture.m_Width > 0 and texture.m_Height > 0 and texture.image.getbbox() is not None
    assert len(animators) == len(renderers) == 1
    animator = animators[0]
    model_id = animator.m_GameObject.path_id
    model_path = next(path for tf, path in nodes if tf.m_GameObject.path_id == model_id)
    model_nodes = [(tf, path[len(model_path):].lstrip('/')) for tf, path in nodes
                   if path == model_path or path.startswith(model_path + '/')]
    ids = {tf.object_reader.path_id for tf, _ in model_nodes}
    renderer = renderers[0]
    assert all(bone.path_id in ids for bone in renderer.m_Bones)
    assert renderer.m_RootBone.path_id in ids
    mesh = renderer.m_Mesh.read()
    assert mesh.m_VertexData.m_VertexCount > 0
    for ptr in renderer.m_Materials:
        material = ptr.read()
        assert material.m_Shader.path_id
        textures = [entry.m_Texture for _, entry in material.m_SavedProperties.m_TexEnvs if entry.m_Texture.path_id]
        assert textures
        for texture in textures:
            assert texture.read().m_Width > 0 and texture.read().m_Height > 0
    controller = animator.m_Controller.read_typetree()
    assert {'Base Layer.FlyAway', 'Base Layer.RavenIdle'} <= set(dict(controller['m_TOS']).values())
    assert not controller['m_StateMachineBehaviours']
    assert 'RandomParameter' in dict(controller['m_TOS']).values() and 'RandomizeAnimatorParameter' in scripts
    flight = next(ptr for ptr in animator.m_Controller.read().m_AnimationClips if ptr.read().m_Name == 'RavenFlyAway')
    paths = {zlib.crc32(path.encode()): i for i, (_, path) in enumerate(model_nodes)}
    defaults = [(values(tf.m_LocalPosition), values(tf.m_LocalRotation, 'xyzw'), values(tf.m_LocalScale)) for tf, _ in model_nodes]
    name, duration, frames = sample_clip(flight.deref(), paths, defaults)
    feather_path = next(path for tf, path in model_nodes if tf.m_GameObject.path_id == feather_go.object_reader.path_id)
    assert any(b['path'] == zlib.crc32(feather_path.encode()) and b['typeID'] == 1
               and b['attribute'] == zlib.crc32(b'm_IsActive') for b in flight.read_typetree()['m_ClipBindingConstant']['genericBindings'])
    print(f'PASS: Resources/RavenVoiceThief; {len(model_nodes)} model transforms, '
          f'{mesh.m_VertexData.m_VertexCount} vertices, {len(renderer.m_Bones)} mapped bones; native materials/textures resolve.')
    print(f'PASS: {name}, {duration:.2f}s, {len(frames)} finite sampled poses; native controller has no state-machine scripts.')
    # Production loops .3-.8s and fixes the Armature translation/scale.
    # Verify all other transforms actually join at those endpoints.
    for index, (_, path) in enumerate(model_nodes):
        a, b = frames[18][index * 10:index * 10 + 10], frames[48][index * 10:index * 10 + 10]
        if path == 'Armature':
            assert sum((x-y)**2 for x, y in zip(a[:3], b[:3])) > 100
            a, b = a[3:7], b[3:7]
        assert max(abs(x-y) for x, y in zip(a, b)) < 0.0001, (path, a, b)
    idle = next(ptr for ptr in animator.m_Controller.read().m_AnimationClips if ptr.read().m_Name == 'RavenIdle')
    idle_name, idle_duration, idle_frames = sample_clip(idle.deref(), paths, defaults)
    print(f'PASS: matched .3-.8s wing-loop endpoints; root departure excluded; '
          f'{idle_name} ({idle_duration:.2f}s) has {len(idle_frames)} finite native poses.')
    print('PASS: native Feathers leaf, shader and texture; inactive at rest, animation-triggered, 1-second emission at 30/s; no wing trails.')
    print('PASS: native Idle random-phase parameter; audited component inventory:', dict(components))
    print('Native gameplay/Photon scripts remain excluded; particle leaves and animation-driven SFX_PlayOneShot components are copied.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--peak-root', type=Path, required=True)
    inspect(parser.parse_args().peak_root)
