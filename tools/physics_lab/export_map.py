"""Run with Blender 4.2 in background mode. See maps/PhysicsLab/README.md."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy


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
            x, y, z = matrix @ vertex.co
            point = [value * meters_per_unit for value in (x, z, y)]
            if not all(math.isfinite(value) for value in point):
                raise ValueError(f"{obj.name}: non-finite vertex")
            vertices.append([value if value else 0.0 for value in point])

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


def write_map(destination, objects):
    def rows(items):
        return ",\n".join("        " + json.dumps(row, allow_nan=False) for row in items)

    blocks = []
    for obj in objects:
        blocks.append(
            '    {\n      "name": ' + json.dumps(obj["name"], ensure_ascii=False)
            + ',\n      "vertices": [\n' + rows(obj["vertices"])
            + '\n      ],\n      "triangles": [\n' + rows(obj["triangles"]) + '\n      ]\n    }'
        )
    content = ('{\n  "version": 1,\n  "units": "meters",\n  "upAxis": "Y",\n'
               '  "frontFace": "clockwise",\n  "objects": [\n'
               + ",\n".join(blocks) + '\n  ]\n}\n')
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(content, encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if args.output.suffix.lower() != ".json":
        parser.error("output must be a JSON file")
    scene = bpy.context.scene
    depsgraph = bpy.context.evaluated_depsgraph_get()
    objects = [extract_object(obj, depsgraph, scene.unit_settings.scale_length)
               for obj in sorted(scene.objects, key=lambda item: item.name) if obj.type == "MESH"]
    if not objects:
        raise ValueError("scene has no mesh objects")
    write_map(args.output, objects)


main()
