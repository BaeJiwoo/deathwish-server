using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using DeathWish.Protocol;

internal sealed record PatrolRoute(int Number, string Name)
{
    private const float EAST_ROUTE_X_METERS = 4.5f;
    private const float EAST_SOUTH_ENTRY_Z_METERS = -8.4f;
    private const float EAST_NORTH_ENTRY_Z_METERS = 9.05f;
    private const float JUMP_ROUTE_X_METERS = -10.4f;
    private const float JUMP_APPROACH_X_METERS = -8.5f;
    private const float JUMP_SOUTH_ENTRY_Z_METERS = -14.55f;
    private const float JUMP_NORTH_ENTRY_Z_METERS = 14.55f;
    private const float TAKEOFF_INSET_METERS = 0.55f;

    private static readonly JumpPlatform[] JumpPlatforms =
    [
        new(-12f, -10f, 0.75f),
        new(-7.9f, -5.9f, 1f),
        new(-2.9f, -0.9f, 1.35f),
        new(3.1f, 5.1f, 1.15f),
        new(9.75f, 11.75f, 1.4f)
    ];

    private static readonly PatrolStep[] CentralCircuit =
    [
        Walk(2.4f, -3.5f),
        Walk(2.4f, 3.5f),
        Walk(-2.4f, 3.5f),
        Walk(-2.4f, -3.5f)
    ];

    private static readonly PatrolStep[] EastCircuit =
    [
        Walk(EAST_ROUTE_X_METERS, EAST_SOUTH_ENTRY_Z_METERS),
        Walk(EAST_ROUTE_X_METERS, EAST_NORTH_ENTRY_Z_METERS)
    ];

    internal static IReadOnlyList<PatrolRoute> All { get; } =
        Array.AsReadOnly(new[]
        {
            new PatrolRoute(1, "Central cover"),
            new PatrolRoute(2, "East arch and bridge"),
            new PatrolRoute(3, "West jump course"),
            new PatrolRoute(CombatDrill.ROUTE_NUMBER, "East firing lane")
        });

    internal bool IsCombatDrill => Number == CombatDrill.ROUTE_NUMBER;

    internal static bool TryGet(int number, [NotNullWhen(true)] out PatrolRoute? route)
    {
        route = All.FirstOrDefault(entry => entry.Number == number);
        return route != null;
    }

    internal (Vector3 Entry, PatrolStep[] Circuit) GetCircuit(TeamId team)
    {
        bool isNorth = team == TeamId.Blue;
        switch (Number)
        {
            case 1:
                return StartCircuit(CentralCircuit, isNorth ? 1 : 0);
            case 2:
                return StartCircuit(EastCircuit, isNorth ? 1 : 0);
            case 3:
                float startZ = isNorth ? JUMP_NORTH_ENTRY_Z_METERS : JUMP_SOUTH_ENTRY_Z_METERS;
                return (new Vector3(JUMP_APPROACH_X_METERS, 0, startZ), CreateJumpCircuit(isNorth, startZ));
            default:
                throw new ArgumentOutOfRangeException(nameof(Number));
        }
    }

    private static (Vector3 Entry, PatrolStep[] Circuit) StartCircuit(PatrolStep[] circuit, int entryIndex)
    {
        var steps = new PatrolStep[circuit.Length];
        for (int index = 0; index < steps.Length; index++)
        {
            steps[index] = circuit[(entryIndex + index + 1) % steps.Length];
        }
        return (circuit[entryIndex].Target, steps);
    }

    private static PatrolStep[] CreateJumpCircuit(bool startsNorth, float startZ)
    {
        // Approach from beside the ramp, then cross the flat strip between its toe and the arena wall.
        var steps = new List<PatrolStep> { Walk(JUMP_ROUTE_X_METERS, startZ) };
        AddJumpLeg(steps, startsNorth);
        AddJumpLeg(steps, !startsNorth);
        return steps.ToArray();
    }

    private static void AddJumpLeg(List<PatrolStep> steps, bool startsNorth)
    {
        int direction = startsNorth ? -1 : 1;
        int index = startsNorth ? JumpPlatforms.Length - 1 : 0;
        JumpPlatform first = JumpPlatforms[index];
        steps.Add(Walk(JUMP_ROUTE_X_METERS, first.CenterZ, first.Height));

        for (int count = 1; count < JumpPlatforms.Length; count++)
        {
            JumpPlatform source = JumpPlatforms[index];
            float takeoffZ = startsNorth ? source.NearZ + TAKEOFF_INSET_METERS : source.FarZ - TAKEOFF_INSET_METERS;
            steps.Add(Walk(JUMP_ROUTE_X_METERS, takeoffZ, source.Height));

            index += direction;
            JumpPlatform target = JumpPlatforms[index];
            steps.Add(new PatrolStep(new Vector3(JUMP_ROUTE_X_METERS, target.Height, target.CenterZ), source.Height));
        }
        steps.Add(Walk(JUMP_ROUTE_X_METERS, startsNorth ? JUMP_SOUTH_ENTRY_Z_METERS : JUMP_NORTH_ENTRY_Z_METERS));
    }

    private static PatrolStep Walk(float xMeters, float zMeters, float heightMeters = 0) =>
        new(new Vector3(xMeters, heightMeters, zMeters));

    private readonly record struct JumpPlatform(float NearZ, float FarZ, float Height)
    {
        internal float CenterZ => (NearZ + FarZ) / 2;
    }
}

internal readonly record struct PatrolStep(Vector3 Target, float? JumpStartHeightMeters = null);
