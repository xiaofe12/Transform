"""Inspect native Condor versus Beetle network availability in installed PEAK.

Read-only. Requires UnityPy for inspection, not at mod runtime. PhotonView layout
below matches the installed PUN version and fails explicitly if its layout changes.
Source-side evidence is described in Tests/CondorNetwork/README.md.
"""
import argparse
from pathlib import Path
import struct

import UnityPy


def inspect(peak_root):
    data = peak_root / 'PEAK_Data'
    resources = UnityPy.load(str(data / 'globalgamemanagers'))
    settings = next(f for f in resources.files.values() if getattr(f, 'name', None) == 'globalgamemanagers')
    manager = next(o.read_typetree() for o in settings.objects.values() if o.type.name == 'ResourceManager')
    container = dict(manager['m_Container'])
    print(f'Resources index: {len(manager["m_Container"])} entries, {len(container)} unique paths')
    print('Beetle:', container.get('0_items/beetle'))
    condor_paths = [name for name in container if 'condor' in name.lower()]
    print('Condor paths:', condor_paths)
    # RavenVoiceThief is a different bird, not the volcano Condor requested here.
    print('Raven paths (different assets):', [name for name in container if 'raven' in name.lower()])

    scene_env = UnityPy.load(str(data / 'level4'))
    scene = next(f for f in scene_env.files.values() if getattr(f, 'name', None) == 'level4')
    count = 0
    for obj in scene.objects.values():
        if obj.type.name != 'MonoBehaviour':
            continue
        # UnityPy's base MonoBehaviour tree reads the object/script header; it does
        # not describe C# fields, so explicitly allow remaining serialized bytes.
        component = obj.read(check_read=False)
        if not component.m_Script.path_id or component.m_Script.read().m_ClassName != 'Condor':
            continue
        go = component.m_GameObject.read()
        scripts = []
        for pair in go.m_Component:
            if pair.component.type.name != 'MonoBehaviour':
                continue
            reader = pair.component.deref()
            header = reader.read(check_read=False)
            name = header.m_Script.read().m_ClassName if header.m_Script.path_id else ''
            scripts.append(name)
            if name == 'PhotonView':
                raw = reader.get_raw_data()
                # Base MonoBehaviour header is 32 bytes. Serialized PhotonView:
                # byte Group (+align), prefix, synchronization, ownership, search,
                # observed component count, scene id, instantiation id, runtime bool.
                if len(raw) != 68:
                    raise ValueError(f'PhotonView layout changed ({len(raw)} bytes); inspect installed PUN before parsing.')
                prefix, sync, ownership, search, observed, view_id, instantiation = struct.unpack_from('<7i', raw, 36)
                if observed != 0:
                    raise ValueError('Condor now has observed components; inspect its updated synchronization.')
                print(f'{go.m_Name} (scene GameObject {go.object_reader.path_id}): '
                      f'view={view_id}, observed={observed}, observableSearch={search}, '
                      f'ownership={ownership}, runtime={bool(raw[64])}')
        print('  scripts:', ', '.join(scripts))
        count += 1
    print(f'Scene Condor components: {count}')
    print('No game files modified.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--peak-root', type=Path, required=True)
    inspect(parser.parse_args().peak_root)
