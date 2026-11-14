namespace DeathWish.Server;

internal readonly record struct WorldLoopStatistics(
    long CompletedTickCount,
    long OverrunCount,
    TimeSpan LastTickDuration,
    TimeSpan MaxTickDuration,
    TimeSpan DroppedTime);
