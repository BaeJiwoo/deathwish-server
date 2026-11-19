using System.Numerics;
using System.Text.Json;
using BepuPhysics.Collidables;

namespace DeathWish.Server.Physics;

internal static class PhysicsLabMap
{
    private const string RESOURCE_NAME = "DeathWish.PhysicsLab.json";

    internal static IReadOnlyList<Collider> LoadColliders()
    {
        using Stream stream = typeof(PhysicsLabMap).Assembly.GetManifestResourceStream(RESOURCE_NAME)
            ?? throw new InvalidDataException($"Missing embedded map {RESOURCE_NAME}.");
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement map = document.RootElement;
        bool isSupported = map.GetProperty("version").GetInt32() == 1 &&
            map.GetProperty("units").GetString() == "meters" && map.GetProperty("upAxis").GetString() == "Y" &&
            map.GetProperty("frontFace").GetString() == "clockwise";
        if (!isSupported)
        {
            throw new InvalidDataException("Unsupported PhysicsLab map coordinate contract.");
        }

        var colliders = new List<Collider>();
        foreach (JsonElement obj in map.GetProperty("objects").EnumerateArray())
        {
            colliders.Add(ReadMesh(obj));
        }
        return colliders.AsReadOnly();
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
            var vertex = new Vector3(point[0].GetSingle(), point[1].GetSingle(), point[2].GetSingle());
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
}
