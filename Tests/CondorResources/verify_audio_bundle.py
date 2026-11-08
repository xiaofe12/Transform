"""Independently verify/decode the bundle assembled by the production C# reader.

Build-time tools only: UnityPy and its FMOD decoder. No audio is redistributed.
Run after the CondorResources C# regression, with PEAK_Data and the output bundle.
"""
import argparse
import io
from pathlib import Path
import wave

import UnityPy


def verify(data_dir, bundle_path):
    original = UnityPy.load(str(data_dir / 'level4'))
    model = next(o.read() for o in original.objects if o.assets_file.name == 'level4'
                 and o.type.name == 'GameObject' and o.read().m_Name == 'Condor1')
    native = {}

    def walk(transform):
        for pair in transform.m_GameObject.read().m_Component:
            if pair.component.type.name == 'AudioSource':
                clip = pair.component.read().m_Resource.read()
                native[clip.m_Name] = clip
        for child in transform.m_Children:
            walk(child.read())

    walk(next(p.component.read() for p in model.m_Component if p.component.type.name == 'Transform'))
    assembled = UnityPy.load(str(bundle_path))
    clips = {o.read().m_Name: o.read() for o in assembled.objects if o.type.name == 'AudioClip'}
    assert set(clips) == set(native) == {'glider LOOP', 'condor FLAP'}
    assert {o.type.name for o in assembled.objects} == {'AssetBundle', 'AudioClip'}
    container = next(o.read() for o in assembled.objects if o.type.name == 'AssetBundle').m_Container
    assert {key for key, _ in container} == {name.lower() for name in native}
    for name, clip in clips.items():
        source = native[name]
        for field in ('m_Channels', 'm_Frequency', 'm_Length', 'm_CompressionFormat', 'm_SubsoundIndex'):
            assert getattr(source, field) == getattr(clip, field), (name, field)
        # Decode both paths independently. This catches wrong stream offsets,
        # missing bundle resource resolution, invalid FSBs and changed content.
        expected = next(iter(source.samples.values()))
        actual = next(iter(clip.samples.values()))
        assert actual == expected, f'{name}: decoded native audio differs'
        with wave.open(io.BytesIO(actual), 'rb') as wav:
            assert wav.getnchannels() == clip.m_Channels and wav.getframerate() == clip.m_Frequency
            assert abs(wav.getnframes() / wav.getframerate() - clip.m_Length) < 0.01
            assert any(wav.readframes(wav.getnframes())), f'{name}: silent PCM'
        print(f'PASS: {name}: {clip.m_Length:.3f}s, {clip.m_Frequency}Hz, '
              f'{clip.m_Channels} channels; decoded PCM matches installed game audio.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('data_dir', type=Path)
    parser.add_argument('bundle', type=Path)
    args = parser.parse_args()
    verify(args.data_dir, args.bundle)
