using System.Numerics;
using DeathWish.Server.Physics;

internal static class PrimitiveTests
{
    internal static void Run()
    {
        List<Collider> colliders =
        [
            new PlaneCollider(new Vector3(4, -2, 9), 8, 2),
            new BoxCollider(new Vector3(3, 1, 0), new Vector3(2, 2, 2)),
            new BoxCollider(new Vector3(-4, 2, 1), new Vector3(1, 4, 3))
        ];
        CheckBounds(colliders[1], new Vector3(2, 0, -1), new Vector3(4, 2, 1));
        colliders[1].SetCenterWorldPosition(Vector3.Zero);
        Reject(() => colliders[1].SetCenterWorldPosition(new Vector3(float.NaN, 0, 0)));
        if (colliders[1].CenterWorldPositionMeters != Vector3.Zero)
        {
            throw new Exception("A rejected move must preserve the collider's center.");
        }
        CheckBounds(colliders[1], new Vector3(-1), new Vector3(1));
        CheckBounds(colliders[0], new Vector3(0, -2, 8), new Vector3(8, -2, 10));
        CheckBounds(colliders[2], new Vector3(-4.5f, 0, -0.5f), new Vector3(-3.5f, 4, 2.5f));

        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            Reject(() => new BoxCollider(Vector3.Zero, new Vector3(invalid, 1, 1)));
            Reject(() => new BoxCollider(Vector3.Zero, new Vector3(1, invalid, 1)));
            Reject(() => new BoxCollider(Vector3.Zero, new Vector3(1, 1, invalid)));
            Reject(() => new PlaneCollider(Vector3.Zero, invalid, 1));
            Reject(() => new PlaneCollider(Vector3.Zero, 1, invalid));
        }
        Reject(() => new PlaneCollider(new Vector3(0, float.NaN, 0), 1, 1));
        Console.WriteLine("PASS mixed collider list, independent positions, bounds and geometry validation");
    }

    private static void CheckBounds(Collider collider, Vector3 expectedMin, Vector3 expectedMax)
    {
        if (collider.GetBounds() != (expectedMin, expectedMax))
        {
            throw new Exception($"Unexpected world bounds for {collider.GetType().Name}.");
        }
    }

    private static void Reject(Action create)
    {
        try
        {
            create();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new Exception("Expected invalid primitive geometry to be rejected.");
    }
}
