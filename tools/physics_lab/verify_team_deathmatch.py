"""Validate the PoC arena export using standard Python."""
import json
import math
import sys
from itertools import combinations
from pathlib import Path

from verify_map import TOLERANCE_METERS, cross, expect_surface, hits, require, subtract


PLAYER_HALF_WIDTH_METERS = 0.325
PLAYER_HEIGHT_METERS = 1.8
SPAWNS_PER_TEAM = 5
ARENA_HALF_WIDTH_METERS = 16
ARENA_HALF_LENGTH_METERS = 19
ARCH_CENTER_X_METERS = 4.5
ARCH_CENTER_Z_METERS = -7.25
BRIDGE_CENTER_X_METERS = 4.5
BRIDGE_WIDTH_METERS = 1.05
BRIDGE_DECK_Y_METERS = 2.2
JUMP_ROUTE_X_METERS = -10.4
JUMP_ROUTE_X_BOUNDS_METERS = (-11.55, -9.25)
JUMP_BLOCKS_METERS = (
    (-12, -10, 0.75),
    (-7.9, -5.9, 1),
    (-2.9, -0.9, 1.35),
    (3.1, 5.1, 1.15),
    (9.75, 11.75, 1.4),
)
JUMP_GAPS_METERS = (2.1, 3.0, 4.0, 4.65)


def verify_mesh(obj):
    vertices = obj["vertices"]
    require(bool(vertices) and bool(obj["triangles"]), f"{obj['name']}: empty mesh")
    for vertex in vertices:
        require(len(vertex) == 3 and all(math.isfinite(value) for value in vertex), "invalid vertex")
    for indices in obj["triangles"]:
        require(len(indices) == 3 and all(type(i) is int and 0 <= i < len(vertices) for i in indices),
                "invalid triangle index")
        a, b, c = (vertices[i] for i in indices)
        require(math.hypot(*cross(subtract(b, a), subtract(c, a))) > 0, "degenerate triangle")


def verify_spawns(spawns, floor, obstacles):
    require(len(spawns) == 2 * SPAWNS_PER_TEAM, "spawn count")
    require(len({spawn["id"] for spawn in spawns}) == len(spawns), "duplicate spawn ID")
    for team, team_z, forward_z in ((1, -12, 1), (2, 12, -1)):
        team_spawns = [spawn for spawn in spawns if spawn["team"] == team]
        require(len(team_spawns) == SPAWNS_PER_TEAM, f"team {team}: spawn count")
        require(sorted(spawn["position"][0] for spawn in team_spawns) == [-6, -3, 0, 3, 6],
                f"team {team}: original spawn row changed")
        for spawn in team_spawns:
            spawn_id = spawn["id"]
            x, y, z = spawn["position"]
            require(all(math.isfinite(value) for value in (x, y, z)), f"{spawn_id}: nonfinite position")
            require(abs(z - team_z) < TOLERANCE_METERS, f"{spawn_id}: incorrect end of arena")
            require(math.dist(spawn["forward"], (0, 0, forward_z)) < TOLERANCE_METERS,
                    f"{spawn_id}: must face midfield")
            footprint = (x - PLAYER_HALF_WIDTH_METERS, x + PLAYER_HALF_WIDTH_METERS,
                         z - PLAYER_HALF_WIDTH_METERS, z + PLAYER_HALF_WIDTH_METERS)
            min_x, max_x, min_z, max_z = footprint
            for corner in ((min_x, min_z), (max_x, min_z), (min_x, max_z), (max_x, max_z)):
                expect_surface(floor, 1, corner, -1, y)

            for obstacle in obstacles:
                coordinates = tuple(zip(*obstacle["vertices"]))
                overlaps_x = max_x > min(coordinates[0]) and min_x < max(coordinates[0])
                overlaps_y = y + PLAYER_HEIGHT_METERS > min(coordinates[1]) and y < max(coordinates[1])
                overlaps_z = max_z > min(coordinates[2]) and min_z < max(coordinates[2])
                require(not (overlaps_x and overlaps_y and overlaps_z),
                        f"{spawn_id}: overlaps {obstacle['name']}")

    for first, second in combinations(spawns, 2):
        x1, _, z1 = first["position"]
        x2, _, z2 = second["position"]
        require(max(abs(x1 - x2), abs(z1 - z2)) > 2 * PLAYER_HALF_WIDTH_METERS,
                f"{first['id']} and {second['id']}: overlapping footprints")


def verify_arch(arch):
    # A standing player fits through the centerline in either direction.
    for direction, face_z in ((1, -7.7), (-1, -6.8)):
        for offset_x in (-PLAYER_HALF_WIDTH_METERS, 0, PLAYER_HALF_WIDTH_METERS):
            require(not hits(arch, 2, (ARCH_CENTER_X_METERS + offset_x, PLAYER_HEIGHT_METERS), direction),
                    "east arch blocks the standing clearance")
        expect_surface(arch, 2, (ARCH_CENTER_X_METERS, 2.9), direction, face_z)
        expect_surface(arch, 2, (5.98, 1), direction, face_z)

    expect_surface(arch, 1, (ARCH_CENTER_X_METERS, ARCH_CENTER_Z_METERS), 1, 2.72)
    expect_surface(arch, 1, (ARCH_CENTER_X_METERS, ARCH_CENTER_Z_METERS), -1, 3.1)
    for offset_x in (-0.8, 0.8):
        ceiling = hits(arch, 1, (ARCH_CENTER_X_METERS + offset_x, ARCH_CENTER_Z_METERS), 1)
        require(any(2.35 < y < 2.55 for y in ceiling), "expected the arch soffit to curve down from the crown")


def verify_bridge(bridge, floor):
    xs = [vertex[0] for vertex in bridge["vertices"]]
    require(math.isclose(max(xs) - min(xs), BRIDGE_WIDTH_METERS, abs_tol=TOLERANCE_METERS),
            "east bridge width changed")
    for z, y in ((-6, 0), (-4, 1.1), (-2.01, 2.1945), (-2, BRIDGE_DECK_Y_METERS), (-1.99, BRIDGE_DECK_Y_METERS),
                 (0, BRIDGE_DECK_Y_METERS), (3.99, BRIDGE_DECK_Y_METERS), (4, BRIDGE_DECK_Y_METERS),
                 (4.01, 2.1945), (6, 1.1), (8, 0)):
        expect_surface(bridge, 1, (BRIDGE_CENTER_X_METERS, z), -1, y)

    # Open flanks land on the existing floor. The underside is tall enough to pass through at Z=0.
    for x in (3.5, 5.5):
        require(not hits(bridge, 1, (x, 0), -1), f"east bridge flank X={x} obstructs the drop")
        expect_surface(floor, 1, (x, 0), -1, 0)
    expect_surface(bridge, 1, (BRIDGE_CENTER_X_METERS, 0), 1, 1.95)
    for direction in (-1, 1):
        require(not hits(bridge, 0, (PLAYER_HEIGHT_METERS, 0), direction), "east bridge underside blocks a standing player")
    require(not any(vertex[1] > BRIDGE_DECK_Y_METERS + TOLERANCE_METERS for vertex in bridge["vertices"]),
            "east bridge must have no obstacles above the deck")


def verify_jump_course(by_name, floor):
    for index, (near_z, far_z, top_y) in enumerate(JUMP_BLOCKS_METERS, start=1):
        block = by_name[f"JumpBlock{index:02d}"]
        xs, _, zs = zip(*block["vertices"])
        require(math.isclose(min(xs), JUMP_ROUTE_X_BOUNDS_METERS[0], abs_tol=TOLERANCE_METERS) and
                math.isclose(max(xs), JUMP_ROUTE_X_BOUNDS_METERS[1], abs_tol=TOLERANCE_METERS) and
                math.isclose(min(zs), near_z, abs_tol=TOLERANCE_METERS) and
                math.isclose(max(zs), far_z, abs_tol=TOLERANCE_METERS), f"{block['name']}: incorrect landing footprint")
        for z in (near_z + 0.2, far_z - 0.2):
            for x in (JUMP_ROUTE_X_METERS - PLAYER_HALF_WIDTH_METERS, JUMP_ROUTE_X_METERS + PLAYER_HALF_WIDTH_METERS):
                expect_surface(block, 1, (x, z), -1, top_y)

    for index, gap_meters in enumerate(JUMP_GAPS_METERS):
        takeoff_edge = JUMP_BLOCKS_METERS[index][1]
        next_edge = JUMP_BLOCKS_METERS[index + 1][0]
        require(math.isclose(next_edge - takeoff_edge, gap_meters, abs_tol=TOLERANCE_METERS),
                f"jump gap {index + 1}: incorrect length")
        for z in (takeoff_edge + 0.05, (takeoff_edge + next_edge) / 2, next_edge - 0.05):
            for obstacle in by_name.values():
                if obstacle is not floor:
                    require(not hits(obstacle, 1, (JUMP_ROUTE_X_METERS, z), -1),
                            f"{obstacle['name']}: obstructs jump gap {index + 1}")
            expect_surface(floor, 1, (JUMP_ROUTE_X_METERS, z), -1, 0)

    entry = by_name["JumpEntryRamp"]
    exit_ramp = by_name["JumpExitRamp"]
    # Probe just inside the edges because Blender rounds vertices to single precision.
    for z, y in ((-14.149, 0), (-13.075, 0.375), (-12.001, 0.75)):
        expect_surface(entry, 1, (JUMP_ROUTE_X_METERS, z), -1, y)
    for z, y in ((11.751, 1.4), (12.975, 0.7), (14.199, 0)):
        expect_surface(exit_ramp, 1, (JUMP_ROUTE_X_METERS, z), -1, y)
    for z in (-14.6, 14.6):
        expect_surface(floor, 1, (JUMP_ROUTE_X_METERS, z), -1, 0)
        for ramp in (entry, exit_ramp):
            require(not hits(ramp, 1, (JUMP_ROUTE_X_METERS, z), -1), f"{ramp['name']}: obstructs the flat approach")


def verify_perimeter(by_name, floor, obstacles):
    half_width = ARENA_HALF_WIDTH_METERS
    half_length = ARENA_HALF_LENGTH_METERS
    xs, _, zs = zip(*floor["vertices"])
    require((min(xs), max(xs), min(zs), max(zs)) == (-half_width, half_width, -half_length, half_length),
            "arena floor bounds")

    for name, axis, point, direction, coordinate in (
            ("WestWall", 0, (1, 0), -1, -half_width), ("EastWall", 0, (1, 0), 1, half_width),
            ("RedEndWall", 2, (0, 1), -1, -half_length), ("BlueEndWall", 2, (0, 1), 1, half_length)):
        expect_surface(by_name[name], axis, point, direction, coordinate)

    # The two side lanes and two end crossovers meet inside the new floor corners.
    for x in (-14, 14):
        for z in (-17, 17):
            expect_surface(floor, 1, (x, z), -1, 0)

    for axis, paths, limit in ((2, (-14, 14), half_length), (0, (-17, 17), half_width)):
        for path in paths:
            point = (path, 1) if axis == 2 else (1, path)
            for obstacle in obstacles:
                for direction in (-1, 1):
                    coordinates = hits(obstacle, axis, point, direction)
                    require(all(abs(coordinate) >= limit - TOLERANCE_METERS for coordinate in coordinates),
                            f"{obstacle['name']}: obstructs perimeter path at {path}")


def verify(path):
    data = json.loads(path.read_text(encoding="utf-8"))
    require((data["version"], data["mapId"], data["units"], data["upAxis"], data["frontFace"]) ==
            (2, 2, "meters", "Y", "clockwise"), "arena coordinate contract")
    by_name = {obj["name"]: obj for obj in data["objects"]}
    required = {"Floor", "WestWall", "EastWall", "RedEndWall", "BlueEndWall", "RedSpawnCover", "BlueSpawnCover",
                "CenterPillar", "WestCover", "EastCover", "EastArch", "EastNarrowBridge", "JumpEntryRamp", "JumpExitRamp",
                *(f"JumpBlock{index:02d}" for index in range(1, len(JUMP_BLOCKS_METERS) + 1))}
    require(required <= by_name.keys() and len(by_name) == len(data["objects"]), "missing arena geometry or duplicate name")
    for obj in by_name.values():
        verify_mesh(obj)

    floor = by_name["Floor"]
    obstacles = [obj for obj in by_name.values() if obj is not floor]
    verify_perimeter(by_name, floor, obstacles)
    for name, axis, point, direction, coordinate in (
            ("RedSpawnCover", 2, (0, 1.4), 1, -9.375),
            ("BlueSpawnCover", 2, (0, 1.4), -1, 9.375),
            ("CenterPillar", 2, (0, 1.4), 1, -1.5),
            ("CenterPillar", 2, (0, 1.4), -1, 1.5)):
        expect_surface(by_name[name], axis, point, direction, coordinate)

    for lane_x in (-8, -6, 8):
        for obstacle in obstacles:
            for direction in (-1, 1):
                coordinates = hits(obstacle, 2, (lane_x, 1), direction)
                require(all(abs(z) > 12 for z in coordinates), f"{obstacle['name']}: blocks lane X={lane_x}")

    verify_spawns(data["spawns"], floor, obstacles)
    verify_arch(by_name["EastArch"])
    verify_bridge(by_name["EastNarrowBridge"], floor)
    verify_jump_course(by_name, floor)
    print(f"PASS 32 x 38 m arena, perimeter paths, cover, original lanes and {len(data['spawns'])} grounded spawns")
    print("PASS curved arch aperture, crown and jambs, continuous bridge ramps and deck, open flanks and underside")
    print("PASS five jump block footprints, four open gaps, floor landing areas and both access ramps")
    print(f"{len(by_name)} objects, {sum(len(obj['vertices']) for obj in by_name.values())} vertices, "
          f"{sum(len(obj['triangles']) for obj in by_name.values())} triangles")


if __name__ == "__main__":
    verify(Path(sys.argv[1]))
