using System.Numerics;
using System.Text.Json;
using BepuPhysics.Collidables;
using DeathWish.Protocol;

namespace DeathWish.Server.Physics;

internal sealed record MeshWorldMap(IReadOnlyList<Collider> Colliders, IReadOnlyList<MapSpawn> Spawns)
{
    private const int PHYSICS_LAB_VERSION = 1;
    private const int TEAM_DEATHMATCH_VERSION = 2;
    private const int SPAWNS_PER_TEAM = 5;
    private const int FIRST_TEAM_ID = 1;
    private const int SECOND_TEAM_ID = 2;
    private const float FORWARD_VECTOR_TOLERANCE = 0.0001f;
    private const float MIN_SPAWN_X_SEPARATION_METERS = PlayerPhysicsSimulation.PLAYER_WIDTH_METERS * 2;
    private const float MIN_SPAWN_Y_SEPARATION_METERS = PlayerPhysicsSimulation.PLAYER_HEIGHT_METERS * 2;
    private const float MIN_SPAWN_Z_SEPARATION_METERS = PlayerPhysicsSimulation.PLAYER_DEPTH_METERS * 2;

    internal static MeshWorldMap Load(WorldMapId mapId)
    {
        (string resourceName, int expectedVersion) = mapId switch
        {
            WorldMapId.PhysicsLab => ("DeathWish.PhysicsLab.json", PHYSICS_LAB_VERSION),
            WorldMapId.TeamDeathmatch => ("DeathWish.TeamDeathmatch.json", TEAM_DEATHMATCH_VERSION),
            _ => throw new ArgumentOutOfRangeException(nameof(mapId))
        };
        using Stream stream = typeof(MeshWorldMap).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing embedded map {resourceName}.");
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement map = document.RootElement;
        bool isSupported = map.GetProperty("version").GetInt32() == expectedVersion &&
            map.GetProperty("units").GetString() == "meters" && map.GetProperty("upAxis").GetString() == "Y" &&
            map.GetProperty("frontFace").GetString() == "clockwise";
        if (!isSupported)
        {
            throw new InvalidDataException($"Unsupported {mapId} map coordinate contract.");
        }

        JsonElement objects = map.GetProperty("objects");
        int objectCount = objects.GetArrayLength();
        if (objectCount == 0)
        {
            throw new InvalidDataException($"{mapId} contains no collision meshes.");
        }

        var colliders = new List<Collider>(objectCount);
        foreach (JsonElement obj in objects.EnumerateArray())
        {
            colliders.Add(ReadMesh(obj));
        }
        IReadOnlyList<MapSpawn> spawns = mapId == WorldMapId.TeamDeathmatch ? ReadSpawns(map) : [];
        return new MeshWorldMap(colliders.AsReadOnly(), spawns);
    }

    private static IReadOnlyList<MapSpawn> ReadSpawns(JsonElement map)
    {
        if (map.GetProperty("mapId").GetInt32() != (int)WorldMapId.TeamDeathmatch)
        {
            throw new InvalidDataException("Team deathmatch map ID does not match its resource.");
        }

        var spawns = new List<MapSpawn>();
        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in map.GetProperty("spawns").EnumerateArray())
        {
            string? id = entry.GetProperty("id").GetString();
            int teamId = entry.GetProperty("team").GetInt32();
            Vector3 position = ReadVector(entry.GetProperty("position"));
            Vector3 forward = ReadVector(entry.GetProperty("forward"));
            bool hasValidForward = MathF.Abs(forward.Y) < FORWARD_VECTOR_TOLERANCE &&
                MathF.Abs(forward.LengthSquared() - 1f) < FORWARD_VECTOR_TOLERANCE;
            if (string.IsNullOrWhiteSpace(id) || !spawnIds.Add(id) ||
                teamId is not (FIRST_TEAM_ID or SECOND_TEAM_ID) || !hasValidForward)
            {
                throw new InvalidDataException("Invalid team deathmatch spawn marker.");
            }

            // One player can block at most one marker, so a free spawn always exists below capacity.
            bool hasOverlappingMarker = spawns.Any(spawn =>
                MathF.Abs(position.X - spawn.Position.X) < MIN_SPAWN_X_SEPARATION_METERS &&
                MathF.Abs(position.Y - spawn.Position.Y) < MIN_SPAWN_Y_SEPARATION_METERS &&
                MathF.Abs(position.Z - spawn.Position.Z) < MIN_SPAWN_Z_SEPARATION_METERS);
            if (hasOverlappingMarker)
            {
                throw new InvalidDataException($"Spawn {id} is too close to another spawn marker.");
            }

            spawns.Add(new MapSpawn(id, teamId, position, forward));
        }

        if (spawns.Count(spawn => spawn.TeamId == FIRST_TEAM_ID) != SPAWNS_PER_TEAM ||
            spawns.Count(spawn => spawn.TeamId == SECOND_TEAM_ID) != SPAWNS_PER_TEAM)
        {
            throw new InvalidDataException("Team deathmatch requires five spawn markers per team.");
        }
        return spawns.AsReadOnly();
    }

    private static StaticMeshCollider ReadMesh(JsonElement obj)
    {
        JsonElement points = obj.GetProperty("vertices");
        var vertices = new Vector3[points.GetArrayLength()];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        int index = 0;
        foreach (JsonElement point in points.EnumerateArray())
        {
            Vector3 vertex = ReadVector(point);
            vertices[index++] = vertex;
            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }

        // Exported vertices are world-space; Bepu applies the static's center when placing the mesh.
        Vector3 center = (min + max) / 2;
        JsonElement faces = obj.GetProperty("triangles");
        var triangles = new Triangle[faces.GetArrayLength()];
        index = 0;
        foreach (JsonElement face in faces.EnumerateArray())
        {
            triangles[index++] = new Triangle(vertices[face[0].GetInt32()] - center,
                vertices[face[1].GetInt32()] - center, vertices[face[2].GetInt32()] - center);
        }
        return new StaticMeshCollider(center, (max - min) / 2, triangles);
    }

    private static Vector3 ReadVector(JsonElement point)
    {
        if (point.GetArrayLength() != 3)
        {
            throw new InvalidDataException("Expected a three-component map vector.");
        }

        var vector = new Vector3(point[0].GetSingle(), point[1].GetSingle(), point[2].GetSingle());
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z))
        {
            throw new InvalidDataException("Map vector components must be finite.");
        }
        return vector;
    }
}

internal readonly record struct MapSpawn(string Id, int TeamId, Vector3 Position, Vector3 Forward);
