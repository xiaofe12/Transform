"""Verify Raven's native animation -> SFX component -> decoded audio chain.

Read-only, using UnityPy. No sound data is embedded in or exported with the mod.
"""
import argparse
import io
from pathlib import Path
import struct
import wave
import zlib

import UnityPy


def verify(peak_root):
    env = UnityPy.load(str(peak_root / 'PEAK_Data' / 'resources.assets'))
    objects = {o.path_id: o for o in env.objects if o.assets_file.name == 'resources.assets'}
    root = next(o.read() for o in objects.values()
                if o.type.name == 'GameObject' and o.peek_name() == 'RavenVoiceThief')
    sounds, clips = {}, {}

    def walk(go, path=''):
        for pair in go.m_Component:
            ptr = pair.component
            if ptr.type.name == 'MonoBehaviour':
                obj = ptr.deref()
                header = obj.read(check_read=False)
                if header.m_Script.read().m_ClassName == 'SFX_PlayOneShot':
                    raw = obj.get_raw_data()
                    # Native bool fields are each aligned to 4 bytes. Fail on a
                    # changed schema instead of accidentally accepting empty SFX.
                    assert header.m_Name == '' and not header.m_Enabled
                    assert struct.unpack_from('<4I', raw, 32) == (0, 1, 1, 0)
                    assert struct.unpack_from('<iq', raw, 48) == (0, 0)
                    count, = struct.unpack_from('<I', raw, 60)
                    assert count > 0 and len(raw) == 64 + 12 * count
                    effects = []
                    for i in range(count):
                        file_id, path_id = struct.unpack_from('<iq', raw, 64 + i * 12)
                        assert file_id == 0
                        effects.append(objects[path_id])
                    sounds[zlib.crc32(path.encode())] = (path, effects)
            elif ptr.type.name == 'Animator':
                for clip in ptr.read().m_Controller.read().m_AnimationClips:
                    clips[clip.read().m_Name] = clip.read_typetree()
            elif ptr.type.name == 'Transform':
                for child in ptr.read().m_Children:
                    child_go = child.read().m_GameObject.read()
                    walk(child_go, path + '/' + child_go.m_Name if path else child_go.m_Name)

    # Animation bindings are relative to the Animator's Raven model root.
    def model(go):
        if any(p.component.type.name == 'Animator' for p in go.m_Component): return go
        tf = next(p.component.read() for p in go.m_Component if p.component.type.name == 'Transform')
        for child in tf.m_Children:
            found = model(child.read().m_GameObject.read())
            if found is not None: return found
        return None

    walk(model(root))
    assert {path.rsplit('/', 1)[1] for path, _ in sounds.values()} == {'IdleSquawkLoud', 'IdleSquawkSoft', 'AngyFlyAway'}
    decoded = set()
    for path, effects in sounds.values():
        for effect in effects:
            header = effect.read(check_read=False)
            assert header.m_Script.read().m_ClassName == 'SFX_Instance'
            raw = effect.get_raw_data()
            offset = (32 + len(header.m_Name.encode()) + 3) & ~3
            count, = struct.unpack_from('<I', raw, offset)
            offset += 4
            assert count > 0 and len(raw) == offset + count * 12 + 36
            settings = struct.unpack_from('<8fi', raw, offset + count * 12)
            assert settings[0] > 0 and settings[2] > 0 and settings[4] == 1 and settings[6] > 5
            for i in range(count):
                file_id, path_id = struct.unpack_from('<iq', raw, offset + i * 12)
                assert file_id == 0
                if path_id in decoded: continue
                clip = objects[path_id].read()
                assert objects[path_id].type.name == 'AudioClip' and clip.m_Length > 0
                pcm = next(iter(clip.samples.values()))
                with wave.open(io.BytesIO(pcm), 'rb') as wav:
                    assert wav.getnchannels() == clip.m_Channels and wav.getframerate() == clip.m_Frequency
                    assert abs(wav.getnframes() / wav.getframerate() - clip.m_Length) < .01
                    assert any(wav.readframes(wav.getnframes()))
                decoded.add(path_id)
                print(f'PASS: {clip.m_Name}: {clip.m_Length:.3f}s, {clip.m_Frequency}Hz, decoded non-silent native audio.')
            print(f'PASS: {path.rsplit("/", 1)[1]} -> {header.m_Name}, {count} original clips, 3D range {settings[6]:g}m.')

    for name in ('RavenIdle', 'RavenFlyAway'):
        clip = clips[name]
        streamed = clip['m_MuscleClip']['m_Clip']['data']['m_StreamedClip']
        raw = struct.pack('<%dI' % len(streamed['data']), *streamed['data'])
        keys = [[] for _ in range(streamed['curveCount'])]
        offset = 0
        while offset < len(raw):
            time, count = struct.unpack_from('<fI', raw, offset)
            offset += 8
            for _ in range(count):
                index, a, b, c, value = struct.unpack_from('<I4f', raw, offset)
                offset += 20
                keys[index].append((time, value))
        index, bound = 0, set()
        for binding in clip['m_ClipBindingConstant']['genericBindings']:
            if binding['path'] in sounds and binding['typeID'] == 114:
                assert binding['attribute'] == zlib.crc32(b'm_Enabled')
                assert keys[index] and any(value == 1 for _, value in keys[index])
                path = sounds[binding['path']][0]
                bound.add(path.rsplit('/', 1)[1])
                if name == 'RavenFlyAway':
                    # The .3-.8s flight loop stays enabled throughout, so native
                    # OnEnable plays takeoff once, not once per wing cycle.
                    assert all(value == 1 for _, value in keys[index])
            index += (4 if binding['attribute'] == 2 else 3) if binding['typeID'] == 4 else 1
        assert bound == ({'IdleSquawkLoud', 'IdleSquawkSoft'} if name == 'RavenIdle' else {'AngyFlyAway'})
    print(f'PASS: {len(decoded)} native clips; Idle has two sound pulses, FlyAway enables takeoff once; no animation events required.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--peak-root', type=Path, required=True)
    verify(parser.parse_args().peak_root)
