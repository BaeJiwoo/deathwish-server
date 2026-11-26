"""Bake Bob's upper-body aiming loop using Blender's FBX importer.

blender -b --factory-startup --python-exit-code 1 --python tools/animation/bake_shooter_aim.py -- [--check]
The default inputs are in the sibling deathwish-assets repository.
"""

import argparse
import json
import math
import sys
import tempfile
from pathlib import Path
from zipfile import ZipFile

import bpy
from mathutils import Matrix


SERVER = Path(__file__).resolve().parents[2]
RESOURCE = SERVER / 'integrations/Unity/Resources/ShooterAimPose.json'
SOURCE_CLIP = 'idle aiming.fbx'
# Blender FBX world -> right / up / forward in the Unity marker.
TO_CHARACTER = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))
BONES = ['Spine', 'Spine1', 'Spine2', 'Neck', 'Head']
for side in ('Left', 'Right'):
    BONES.extend(side + part for part in (
        'Shoulder', 'Arm', 'ForeArm', 'Hand', 'HandThumb1', 'HandThumb2',
        'HandThumb3', 'HandIndex1', 'HandIndex2', 'HandIndex3'))


def load_armature(path, animation):
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=animation)
    armature, = (obj for obj in bpy.context.selected_objects if obj.type == 'ARMATURE')
    return armature


def bone_matrices(armature, animated):
    bones = armature.pose.bones if animated else armature.data.bones
    return {
        bone.name.split(':')[-1]: armature.matrix_world @ (bone.matrix if animated else bone.matrix_local)
        for bone in bones
    }


def rounded(values):
    return [round(component, 6) for component in values]


def bake(model_path, pack_path):
    model = load_armature(model_path, False)
    bind = bone_matrices(model, False)
    with tempfile.TemporaryDirectory() as work, ZipFile(pack_path) as pack:
        source = load_armature(pack.extract(SOURCE_CLIP, work), True)
    scene = bpy.context.scene
    action = source.animation_data.action
    first, last = map(int, action.frame_range)
    fps = scene.render.fps / scene.render.fps_base
    count = last - first
    if count <= 0 or fps != int(fps):
        raise ValueError('Expected a looping clip sampled at an integer FPS.')

    frames = []
    first_pose = {}
    previous = {}
    # Include the loop end in validation, but do not store the duplicated endpoint.
    for frame in range(first, last + 1):
        scene.frame_set(frame)
        pose = bone_matrices(source, True)
        row = []
        for name in BONES:
            delta = pose[name].to_quaternion() @ bind[name].to_quaternion().inverted()
            character_rotation = (TO_CHARACTER @ delta.to_matrix() @ TO_CHARACTER.transposed()).to_quaternion()
            if frame == first:
                first_pose[name] = character_rotation.copy()
            else:
                character_rotation.make_compatible(previous[name])
            previous[name] = character_rotation
            row.extend(rounded((character_rotation.x, character_rotation.y, character_rotation.z, character_rotation.w)))
        if frame != last:
            frames.append(row)

    seam_degrees = max(math.degrees(2 * math.acos(min(1, abs(previous[n].dot(first_pose[n]))))) for n in BONES)
    if seam_degrees > 1.0:
        raise ValueError(f'Aiming loop has a rotation seam of {seam_degrees:.3f} degrees.')

    metadata = {
        'boneNames': BONES, 'sampleRateHz': int(fps), 'durationSeconds': round(count / fps, 8),
    }
    # Keep each sample on one line so individual frames are reviewable.
    sample_lines = [json.dumps(frame, separators=(',', ':'))[1:-1] for frame in frames]
    content = json.dumps(metadata, indent=2)[:-2] + ',\n  "rotations": [\n    '
    content += ',\n    '.join(sample_lines) + '\n  ]\n}\n'
    print(f'{count} frames @ {fps:g} Hz, {len(BONES)} bones, '
          f'seam {seam_degrees:.3f} degrees.')
    return content


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, default=SERVER.parent / 'deathwish-assets')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    result = bake(args.assets / 'Characters/bob_rigged.fbx', args.assets / 'Animations/ShooterPack.zip')
    if args.check:
        if RESOURCE.read_text() != result:
            raise RuntimeError('Checked-in shooter pose differs. Regenerate with the Blender version in the development log.')
    else:
        RESOURCE.write_text(result)
