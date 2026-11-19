using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;

namespace DeathWish.Server.Physics;

internal readonly struct PlayerContactCallbacks : INarrowPhaseCallbacks
{
    private const float MAX_RECOVERY_METERS_PER_SECOND = 2;
    private const float CONTACT_FREQUENCY_HZ = 120;
    private const float CONTACT_DAMPING_RATIO = 1;

    public void Initialize(Simulation simulation) { }
    public void Dispose() { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Player movement only resolves contacts with static level geometry.
        return a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static;
    }

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties material) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        material = new PairMaterialProperties(0, MAX_RECOVERY_METERS_PER_SECOND,
            new SpringSettings(CONTACT_FREQUENCY_HZ, CONTACT_DAMPING_RATIO));
        return true;
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;
}
