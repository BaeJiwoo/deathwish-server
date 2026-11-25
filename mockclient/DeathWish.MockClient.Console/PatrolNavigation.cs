using System.Numerics;
using System.Text.Json;

internal sealed class PatrolNavigation
{
    private const string MAP_RESOURCE_NAME = "DeathWish.TeamDeathmatch.json";
    private const float PLAYER_HEIGHT_METERS = 1.8f;
    private const float GROUND_CLEARANCE_METERS = 0.46f;
    private const float WALL_CLEARANCE_METERS = 0.36f;
    private const float CORNER_OFFSET_METERS = 0.06f;

    private readonly Bounds _arena;
    private readonly Bounds[] _obstacles;

    private PatrolNavigation(Bounds arena, Bounds[] obstacles)
    {
        _arena = arena;
        _obstacles = obstacles;
    }

    internal static PatrolNavigation Load()
    {
        using Stream stream = typeof(PatrolNavigation).Assembly.GetManifestResourceStream(MAP_RESOURCE_NAME)
            ?? throw new InvalidOperationException("The TeamDeathmatch patrol map resource is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var obstacles = new List<Bounds>();
        Bounds? arena = null;
        foreach (JsonElement part in document.RootElement.GetProperty("objects").EnumerateArray())
        {
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            foreach (JsonElement vertex in part.GetProperty("vertices").EnumerateArray())
            {
                var point = new Vector3(vertex[0].GetSingle(), vertex[1].GetSingle(), vertex[2].GetSingle());
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            var bounds = new Bounds(new Vector2(min.X, min.Z), new Vector2(max.X, max.Z));
            string? name = part.GetProperty("name").GetString();
            if (name == "Floor")
            {
                arena = bounds.Grow(-WALL_CLEARANCE_METERS);
            }
            else if (name?.EndsWith("Wall", StringComparison.Ordinal) != true && max.Y > 0 && min.Y < PLAYER_HEIGHT_METERS)
            {
                obstacles.Add(bounds.Grow(GROUND_CLEARANCE_METERS));
            }
        }

        return new PatrolNavigation(arena ?? throw new InvalidDataException("The patrol map has no arena floor."), obstacles.ToArray());
    }

    /// <summary>Routes only the ground approach. The authored circuit controls ramp, bridge and jump traversal.</summary>
    internal Vector2[] FindApproach(Vector2 origin, Vector2 target)
    {
        Vector2 start = Vector2.Clamp(origin, _arena.Min, _arena.Max);
        if (!CanStand(target))
        {
            return [];
        }
        if (CanStand(start))
        {
            return FindGroundPath(start, target);
        }

        // An interrupted circuit may be atop a ramp or inside the arch. Leave that footprint before ground routing.
        foreach (Vector2 escape in EscapeCandidates(start).OrderBy(point => Vector2.DistanceSquared(start, point)))
        {
            Vector2[] path = FindGroundPath(escape, target);
            if (path.Length > 0)
            {
                return [escape, .. path];
            }
        }
        return [];
    }

    private Vector2[] FindGroundPath(Vector2 start, Vector2 target)
    {
        if (HasClearPath(start, target))
        {
            return [target];
        }

        // Obstacle corners are sufficient for a short polygonal path across the flat approach lanes.
        var nodes = new List<Vector2> { start, target };
        foreach (Bounds obstacle in _obstacles)
        {
            Bounds margin = obstacle.Grow(CORNER_OFFSET_METERS);
            foreach (Vector2 corner in margin.Corners())
            {
                if (CanStand(corner))
                {
                    nodes.Add(corner);
                }
            }
        }

        int targetIndex = 1;
        var distances = new float[nodes.Count];
        Array.Fill(distances, float.PositiveInfinity);
        distances[0] = 0;
        var previous = new int[nodes.Count];
        var visited = new bool[nodes.Count];
        while (true)
        {
            int next = -1;
            float bestDistance = float.PositiveInfinity;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (!visited[index] && distances[index] < bestDistance)
                {
                    next = index;
                    bestDistance = distances[index];
                }
            }
            if (next < 0)
            {
                return [];
            }
            if (next == targetIndex)
            {
                return ReconstructPath(nodes, previous, targetIndex);
            }

            visited[next] = true;
            for (int index = 0; index < nodes.Count; index++)
            {
                float distance = bestDistance + Vector2.Distance(nodes[next], nodes[index]);
                if (!visited[index] && distance < distances[index] && HasClearPath(nodes[next], nodes[index]))
                {
                    distances[index] = distance;
                    previous[index] = next;
                }
            }
        }
    }

    private static Vector2[] ReconstructPath(List<Vector2> nodes, int[] previous, int index)
    {
        var path = new List<Vector2>();
        while (index != 0)
        {
            path.Add(nodes[index]);
            index = previous[index];
        }
        path.Reverse();
        return path.ToArray();
    }

    private IEnumerable<Vector2> EscapeCandidates(Vector2 start)
    {
        foreach (Bounds obstacle in _obstacles)
        {
            if (!obstacle.Contains(start))
            {
                continue;
            }

            Bounds margin = obstacle.Grow(CORNER_OFFSET_METERS);
            foreach (Vector2 candidate in margin.EdgeExits(start))
            {
                if (CanStand(candidate) && HasClearPath(start, candidate, canLeaveStartingObstacle: true))
                {
                    yield return candidate;
                }
            }
        }
    }

    private bool CanStand(Vector2 point) => _arena.Contains(point) && !_obstacles.Any(obstacle => obstacle.Contains(point));

    private bool HasClearPath(Vector2 start, Vector2 target, bool canLeaveStartingObstacle = false)
    {
        return !_obstacles.Any(obstacle => (!canLeaveStartingObstacle || !obstacle.Contains(start)) &&
            obstacle.IntersectsSegment(start, target));
    }

    private readonly record struct Bounds(Vector2 Min, Vector2 Max)
    {
        internal Bounds Grow(float margin) => new(Min - new Vector2(margin), Max + new Vector2(margin));

        internal bool Contains(Vector2 point) =>
            point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;

        internal Vector2[] Corners() => [Min, Max, new(Min.X, Max.Y), new(Max.X, Min.Y)];

        internal Vector2[] EdgeExits(Vector2 point) =>
            [new(Min.X, point.Y), new(Max.X, point.Y), new(point.X, Min.Y), new(point.X, Max.Y)];

        internal bool IntersectsSegment(Vector2 start, Vector2 target)
        {
            Vector2 delta = target - start;
            float enter = 0;
            float exit = 1;
            return Clip(start.X, delta.X, Min.X, Max.X, ref enter, ref exit) &&
                Clip(start.Y, delta.Y, Min.Y, Max.Y, ref enter, ref exit);
        }

        private static bool Clip(float origin, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (delta == 0)
            {
                return origin >= min && origin <= max;
            }

            float first = (min - origin) / delta;
            float last = (max - origin) / delta;
            enter = MathF.Max(enter, MathF.Min(first, last));
            exit = MathF.Min(exit, MathF.Max(first, last));
            return enter <= exit;
        }
    }
}
