"""Export a DeathWish map from Blender in background mode. See maps/*/README.md."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


TEAM_DEATHMATCH_MAP_ID = 2
SPAWNS_PER_TEAM = 5


def to_map_axes(vector, scale=1):
    x, y, z = vector
    return [value * scale or 0.0 for value in (x, z, y)]


def extract_object(obj, depsgraph, meters_per_unit):
    evaluated = obj.evaluated_get(depsgraph)
    matrix = evaluated.matrix_world
    determinant = matrix.to_3x3().determinant()
    if not math.isfinite(determinant) or determinant == 0:
        raise ValueError(f"{obj.name}: invalid world transform")

    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        vertices = []
        for vertex in mesh.vertices:
            point = to_map_axes(matrix @ vertex.co, meters_per_unit)
            if not all(math.isfinite(value) for value in point):
                raise ValueError(f"{obj.name}: non-finite vertex")
            vertices.append(point)

        triangles = []
        for triangle in mesh.loop_triangles:
            a, b, c = triangle.vertices
            # Swapping Y/Z already converts Blender CCW to clockwise; a mirrored object flips it again.
            triangles.append([a, c, b] if determinant < 0 else [a, b, c])
        if not triangles:
            raise ValueError(f"{obj.name}: no triangles")
        return {"name": obj.name, "vertices": vertices, "triangles": triangles}
    finally:
        evaluated.to_mesh_clear()


def extract_spawns(scene, depsgraph):
    markers = scene.collection.children.get("TeamSpawns")
    if markers is None:
        raise ValueError("missing TeamSpawns collection")

    spawns = []
    for marker in markers.all_objects:
        team = marker.get("team")
        if marker.type != "EMPTY" or type(team) is not int or team not in (1, 2):
            raise ValueError(f"{marker.name}: expected an Empty with integer team 1 or 2")

        matrix = marker.evaluated_get(depsgraph).matrix_world
        position = to_map_axes(matrix.translation, scene.unit_settings.scale_length)
        heading = matrix.to_3x3() @ Vector((0, 1, 0))
        length = math.hypot(heading.x, heading.y)
        if not all(math.isfinite(value) for value in position) or not math.isfinite(length) or length <= 1e-6:
            raise ValueError(f"{marker.name}: invalid position or horizontal heading")

        forward = to_map_axes((heading.x / length, heading.y / length, 0))
        spawns.append({"id": marker.name, "team": team, "position": position, "forward": forward})

    for team in (1, 2):
        if sum(spawn["team"] == team for spawn in spawns) != SPAWNS_PER_TEAM:
            raise ValueError(f"expected {SPAWNS_PER_TEAM} spawns for team {team}")
    return sorted(spawns, key=lambda spawn: (spawn["team"], spawn["id"]))


def write_map(destination, objects, caution=None, spawns=None):
    def rows(items):
        return ",\n".join("        " + json.dumps(row, allow_nan=False) for row in items)

    blocks = []
    for obj in objects:
        blocks.append(
            '    {\n      "name": ' + json.dumps(obj["name"], ensure_ascii=False)
            + ',\n      "vertices": [\n' + rows(obj["vertices"])
            + '\n      ],\n      "triangles": [\n' + rows(obj["triangles"]) + '\n      ]\n    }'
        )
    version = 1 if spawns is None else 2
    content = ('{\n  "version": ' + str(version) + ',\n  "units": "meters",\n  "upAxis": "Y",\n'
               '  "frontFace": "clockwise",\n  "objects": [\n'
               + ",\n".join(blocks) + '\n  ]')
    if caution is not None:
        content += ',\n  "caution": ' + json.dumps(caution, allow_nan=False)
    if spawns is not None:
        content += ',\n  "mapId": ' + str(TEAM_DEATHMATCH_MAP_ID) + ',\n  "spawns": [\n'
        content += ",\n".join("    " + json.dumps(spawn, ensure_ascii=False, allow_nan=False) for spawn in spawns)
        content += '\n  ]'
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(content + '\n}\n', encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--map-id", type=int, choices=(TEAM_DEATHMATCH_MAP_ID,))
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if args.output.suffix.lower() != ".json":
        parser.error("output must be a JSON file")
    scene = bpy.context.scene
    depsgraph = bpy.context.evaluated_depsgraph_get()
    objects = []
    caution = None
    for obj in sorted(scene.objects, key=lambda item: item.name):
        if obj.type != "MESH":
            continue
        mesh = extract_object(obj, depsgraph, scene.unit_settings.scale_length)
        if obj.get("deathwish_caution"):
            if caution is not None:
                raise ValueError("expected one combined caution mesh")
            caution = mesh
        else:
            objects.append(mesh)
    if not objects:
        raise ValueError("scene has no mesh objects")
    spawns = None if args.map_id is None else extract_spawns(scene, depsgraph)
    write_map(args.output, objects, caution, spawns)


main()
