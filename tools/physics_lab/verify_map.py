"""Validate the PhysicsLab fixture with standard Python; no engine or Blender required."""
import json
import math
import sys
from pathlib import Path

TOLERANCE_METERS = 0.01


def require(condition, message):
    if not condition:
        raise ValueError(message)


def cross(a, b):
    return (a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0])


def subtract(a, b):
    return tuple(x-y for x, y in zip(a, b))


def hits(obj, axis, point, direction):
    """Return front-face intersections of an axis-aligned line through the given 2D point."""
    u, v = [i for i in range(3) if i != axis]
    coordinates = []
    for indices in obj["triangles"]:
        a, b, c = [obj["vertices"][i] for i in indices]
        ab, ac = subtract(b, a), subtract(c, a)
        # Clockwise front normal is the reverse of the ordinary right-handed cross.
        if cross(ac, ab)[axis] * direction >= 0:
            continue
        det = ab[u]*ac[v] - ab[v]*ac[u]
        du, dv = point[0] - a[u], point[1] - a[v]
        s, t = (du*ac[v] - dv*ac[u])/det, (ab[u]*dv - ab[v]*du)/det
        if min(s, t, 1-s-t) >= -1e-8:
            coordinates.append(a[axis] + s*ab[axis] + t*ac[axis])
    return coordinates


def expect_surface(obj, axis, point, direction, expected):
    values = hits(obj, axis, point, direction)
    require(any(abs(value-expected) <= TOLERANCE_METERS for value in values),
            f"{obj['name']} at {point}: expected {expected}, got {values}")


def verify(path):
    data = json.loads(path.read_text(encoding="utf-8"))
    require((data["version"], data["units"], data["upAxis"], data["frontFace"]) ==
            (1, "meters", "Y", "clockwise"), "coordinate contract")
    by_name = {obj["name"]: obj for obj in data["objects"]}
    walls = ["Plane.001", "Plane.002", "Plane.003", "Plane.004"]
    require(len(data["objects"]) == 7 and set(by_name) == {"Plane", "Cube", "Torus", *walls}, "mesh inventory")
    for obj in by_name.values():
        vertices = obj["vertices"]
        for p in vertices:
            require(len(p) == 3 and all(math.isfinite(x) for x in p), "invalid vertex")
        for tri in obj["triangles"]:
            require(len(tri) == 3 and all(type(i) is int and 0 <= i < len(vertices) for i in tri), "invalid index")
            a, b, c = [vertices[i] for i in tri]
            require(math.hypot(*cross(subtract(b, a), subtract(c, a))) > 0, "degenerate triangle")

    floor = by_name["Plane"]
    for axis, expected in enumerate([(-25, 25), (0, 0), (-25, 25)]):
        values = [p[axis] for p in floor["vertices"]]
        require(max(abs(min(values)-expected[0]), abs(max(values)-expected[1])) < TOLERANCE_METERS, "floor bounds")
    expect_surface(floor, 1, (0, 0), -1, 0)
    for name, axis, point, direction, coordinate in [
        (walls[0], 2, (0, 10), 1, 25), (walls[1], 0, (10, 0), -1, -25),
        (walls[2], 2, (0, 10), -1, -25), (walls[3], 0, (10, 0), 1, 25)
    ]:
        wall = by_name[name]
        require(abs(max(p[1] for p in wall["vertices"])-20) < TOLERANCE_METERS, "wall height")
        expect_surface(wall, axis, point, direction, coordinate)

    platform = by_name["Cube"]
    expect_surface(platform, 1, (-17, 3.5), -1, 3)
    expect_surface(platform, 1, (-12, -18), -1, 6)
    expect_surface(platform, 1, (-12, -18), 1, 5.25)
    require(not hits(platform, 1, (-17, -18), -1) and not hits(platform, 1, (-17, -18), 1), "platform hole blocked")
    expect_surface(platform, 0, (5.6, -18), -1, -20.5987)
    expect_surface(platform, 0, (5.6, -18), 1, -13.8854)
    expect_surface(platform, 2, (-17, 5.6), -1, -20.95)
    expect_surface(platform, 2, (-17, 5.6), 1, -15.05)
    expect_surface(floor, 1, (-17, -18), -1, 0)
    ring = by_name["Torus"]
    require(not hits(ring, 2, (0, 2.65), 1) and not hits(ring, 2, (0, 2.65), -1), "ring opening blocked")
    require(bool(hits(ring, 2, (3.7, 2.65), 1)), "ring rim missing")
    paint = data["caution"]
    require(paint["name"] not in by_name, "paint included in collision inventory")
    for x, z in ((-17, -14.85), (-17, -21.15), (-20.8, -18), (-13.68, -18)):
        expect_surface(paint, 1, (x, z), -1, 6.008)
    for x in (-24.8, -9.2):
        expect_surface(paint, 1, (x, 3.5), -1, 3.008)
    for x, z in ((24.8, 0), (-24.8, 0), (0, 24.8), (0, -24.8)):
        expect_surface(paint, 1, (x, z), -1, 0.008)
    for axis, point, direction, expected in (
            (0, (19.8, 0), 1, 24.992), (0, (19.8, 0), -1, -24.992),
            (2, (0, 19.8), 1, 24.992), (2, (0, 19.8), -1, -24.992)):
        expect_surface(paint, axis, point, direction, expected)
    require(not hits(paint, 1, (-17, -18), -1), "paint covers the platform opening")
    print("PASS edge paint on ramp, platform opening, floor perimeter and four walls")
    print("PASS floor, four inward walls, ramp, platform, opening and ring")
    print(f"{len(by_name)} objects, {sum(len(o['vertices']) for o in by_name.values())} vertices, "
          f"{sum(len(o['triangles']) for o in by_name.values())} triangles")


if __name__ == "__main__":
    verify(Path(sys.argv[1]))
