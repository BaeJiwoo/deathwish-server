using System.Runtime.InteropServices;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;

internal static class WasdMode
{
    private const int INPUT_INTERVAL_MS = 33;
    private const int WRITE_TIMEOUT_SECONDS = 5;
    private const float YAW_DEGREES_PER_SECOND = 90;
    private const float DEGREES_PER_TURN = 360;
    private const int KEY_ESCAPE = 0x1B;
    private const int KEY_LEFT = 0x25;
    private const int KEY_RIGHT = 0x27;
    private const int KEY_W = 0x57;
    private const int KEY_A = 0x41;
    private const int KEY_S = 0x53;
    private const int KEY_D = 0x44;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    public static async Task<uint> RunAsync(ConnectionTestClient client, long playerId,
        uint sequence, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.WriteLine("/wasd requires an interactive Windows console. Use /move on other platforms.");
            return sequence;
        }

        IntPtr window = GetConsoleWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window))
        {
            Console.WriteLine("/wasd requires a standalone Windows Console Host window; VS Code/Windows Terminal tabs cannot safely report input focus.");
            Console.WriteLine("Run mockclient/Start-MockClient.ps1 to open a supported window, or use /move here.");
            return sequence;
        }

        Console.WriteLine("WASD: move, Left/Right: yaw, Esc: return. Focus loss stops movement.");
        float yawDegrees = 0;
        long shownTick = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double previousSeconds = clock.Elapsed.TotalSeconds;
        try
        {
            while (!client.Disconnected.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasFocus = GetForegroundWindow() == window;
                if (hasFocus && IsDown(KEY_ESCAPE))
                {
                    break;
                }
                double elapsedSeconds = clock.Elapsed.TotalSeconds;
                float deltaSeconds = (float)Math.Min(0.1, elapsedSeconds - previousSeconds);
                previousSeconds = elapsedSeconds;
                float strafe = 0;
                float forward = 0;
                if (hasFocus)
                {
                    yawDegrees = (yawDegrees + Axis(KEY_RIGHT, KEY_LEFT) * YAW_DEGREES_PER_SECOND * deltaSeconds) % DEGREES_PER_TURN;
                    strafe = Axis(KEY_D, KEY_A);
                    forward = Axis(KEY_W, KEY_S);
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(WRITE_TIMEOUT_SECONDS));
                    await client.SendMoveAsync(MovementInput.FromLocalAxes(sequence++, strafe, forward, yawDegrees), timeout.Token);
                }
                WorldSnapshot? snapshot = client.LatestSnapshot;
                if (snapshot != null && snapshot.ServerTick != shownTick)
                {
                    shownTick = snapshot.ServerTick;
                    SnapshotPlayer player = snapshot.Players.First(p => p.PlayerId == playerId);
                    Console.Write($"\rTick {shownTick}: ({player.X:F2}, {player.Y:F2}, {player.Z:F2})  Players={snapshot.Players.Count}  Yaw={yawDegrees:F0}       ");
                }
                await Task.Delay(INPUT_INTERVAL_MS, cancellationToken);
            }
            if (!client.Disconnected.IsCompleted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(WRITE_TIMEOUT_SECONDS));
                await client.SendMoveAsync(new MoveInput(sequence++, 0, 0), timeout.Token);
            }
            return sequence;
        }
        finally
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }
            Console.WriteLine();
        }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static int Axis(int positive, int negative) => (IsDown(positive) ? 1 : 0) - (IsDown(negative) ? 1 : 0);
}
