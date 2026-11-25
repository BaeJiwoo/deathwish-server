using DeathWish.Server;

static class ServerOptionsTests
{
    public static void Run()
    {
        const string GRAVITY_KEY = "SERVER_GRAVITY_METERS_PER_SECOND_SQUARED";
        const string TDM_ROOM_KEY = "SERVER_TDM_ROOM_ID";
        const string TDM_TEAM_SIZE_KEY = "SERVER_TDM_TEAM_SIZE";
        const string TRAINING_ROOM_KEY = "SERVER_TRAINING_ROOM_ID";
        string[] keys = { "SERVER_ADDRESS", "SERVER_PORT", "SERVER_ROOM_COUNT", "SERVER_TICK_RATE", GRAVITY_KEY,
            TDM_ROOM_KEY, TDM_TEAM_SIZE_KEY, TRAINING_ROOM_KEY };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        string path = Path.Combine(Path.GetTempPath(), $"deathwish-options-{Guid.NewGuid():N}.env");
        try
        {
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);
            ServerOptions defaults = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(defaults.GravityMetersPerSecondSquared == 9.81f, "Default gravity magnitude");
            Require(defaults.TeamDeathmatchRoomId is null, "Team deathmatch map is opt-in");
            Require(defaults.TeamDeathmatchTeamSize == 5, "Default team deathmatch is 5v5");
            Require(defaults.TrainingRoomId is null, "Training room is opt-in");
            File.WriteAllText(path, $"SERVER_ROOM_COUNT=3\n{TDM_ROOM_KEY}=1");
            Require(ServerOptionsLoader.Load([], path).TeamDeathmatchRoomId == 1, "Team deathmatch room from file");
            Environment.SetEnvironmentVariable(TDM_ROOM_KEY, "3");
            Require(ServerOptionsLoader.Load([], path).TeamDeathmatchRoomId == 3, "Environment overrides team deathmatch room");
            Environment.SetEnvironmentVariable(TDM_ROOM_KEY, null);
            foreach (string invalidRoomId in new[] { "", "0", "-1", "4", "1.5", "invalid" })
            {
                File.WriteAllText(path, $"SERVER_ROOM_COUNT=3\n{TDM_ROOM_KEY}={invalidRoomId}");
                Reject<ArgumentException>(() => ServerOptionsLoader.Load([], path));
            }
            File.WriteAllText(path, $"{TDM_TEAM_SIZE_KEY}=1");
            Require(ServerOptionsLoader.Load([], path).TeamDeathmatchTeamSize == 1, "Local 1v1 team size from configuration");
            Environment.SetEnvironmentVariable(TDM_TEAM_SIZE_KEY, "4");
            Require(ServerOptionsLoader.Load([], path).TeamDeathmatchTeamSize == 4, "Environment overrides team size");
            Environment.SetEnvironmentVariable(TDM_TEAM_SIZE_KEY, null);
            foreach (string invalidTeamSize in new[] { "", "0", "-1", "6", "1.5", "invalid" })
            {
                File.WriteAllText(path, $"{TDM_TEAM_SIZE_KEY}={invalidTeamSize}");
                Reject<ArgumentException>(() => ServerOptionsLoader.Load([], path));
            }

            File.WriteAllText(path, $"SERVER_ROOM_COUNT=3\n{TDM_ROOM_KEY}=1\n{TRAINING_ROOM_KEY}=2");
            ServerOptions withTraining = ServerOptionsLoader.Load([], path);
            Require(withTraining.TrainingRoomId == 2 && withTraining.TeamDeathmatchRoomId == 1,
                "Training and competitive rooms may coexist");
            Environment.SetEnvironmentVariable(TRAINING_ROOM_KEY, "3");
            Require(ServerOptionsLoader.Load([], path).TrainingRoomId == 3, "Environment overrides training room");
            Environment.SetEnvironmentVariable(TRAINING_ROOM_KEY, "1");
            Reject<ArgumentException>(() => ServerOptionsLoader.Load([], path));
            Environment.SetEnvironmentVariable(TRAINING_ROOM_KEY, null);
            foreach (string invalidTrainingRoom in new[] { "", "0", "-1", "1.5", "4", "invalid" })
            {
                File.WriteAllText(path, $"SERVER_ROOM_COUNT=3\n{TRAINING_ROOM_KEY}={invalidTrainingRoom}");
                Reject<ArgumentException>(() => ServerOptionsLoader.Load([], path));
            }

            File.WriteAllText(path, $"{GRAVITY_KEY}=1.25");
            Require(ServerOptionsLoader.Load([], path).GravityMetersPerSecondSquared == 1.25f, "Gravity from file");
            Environment.SetEnvironmentVariable(GRAVITY_KEY, "0");
            Require(ServerOptionsLoader.Load([], path).GravityMetersPerSecondSquared == 0, "Environment can disable gravity");
            Environment.SetEnvironmentVariable(GRAVITY_KEY, null);
            foreach (string invalidGravity in new[] { "-1", "NaN", "Infinity", "1e100", "invalid" })
            {
                File.WriteAllText(path, $"{GRAVITY_KEY}={invalidGravity}");
                Reject<ArgumentException>(() => ServerOptionsLoader.Load([], path));
            }

            Require(defaults.RoomCount == ServerOptions.DefaultRoomCount && defaults.Port == ServerOptions.DefaultPort && defaults.TickRate == 30, "Missing file defaults");
            File.WriteAllText(path, "# Test settings\nSERVER_ADDRESS='127.0.0.2'\nSERVER_PORT = 8000 # port\nSERVER_ROOM_COUNT=4\nSERVER_ROOM_COUNT=5\nSERVER_TICK_RATE=60\n");
            ServerOptions file = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(file.Address == "127.0.0.2" && file.Port == 8000 && file.RoomCount == 5 && file.TickRate == 60, "File parsing and last duplicate wins");
            Require(Environment.GetEnvironmentVariable("SERVER_PORT") is null, "File does not mutate process environment");
            Environment.SetEnvironmentVariable("SERVER_PORT", "9000");
            Environment.SetEnvironmentVariable("SERVER_ROOM_COUNT", "6");
            Environment.SetEnvironmentVariable("SERVER_TICK_RATE", "120");
            ServerOptions environment = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(environment.Port == 9000 && environment.RoomCount == 6 && environment.TickRate == 120, "Environment overrides file");
            ServerOptions cli = ServerOptionsLoader.Load(new[] { "::1", "10000" }, path);
            Require(cli.Address == "::1" && cli.Port == 10000 && cli.RoomCount == 6 && cli.TickRate == 120, "CLI overrides environment");
            Environment.SetEnvironmentVariable("SERVER_PORT", "invalid");
            Require(ServerOptionsLoader.Load(new[] { "127.0.0.1", "7777" }, path).Port == 7777, "CLI shadows invalid lower priority value");
            Reject<ArgumentException>(() => ServerOptionsLoader.Load(Array.Empty<string>(), path));
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);

            foreach (string invalid in new[] { "SERVER_PORT=0", "SERVER_PORT=65536", "SERVER_PORT=abc", "SERVER_ADDRESS=nope",
                "SERVER_ROOM_COUNT=0", "SERVER_ROOM_COUNT=-1", "SERVER_ROOM_COUNT=2147483648", "SERVER_ROOM_COUNT=",
                "SERVER_TICK_RATE=0", "SERVER_TICK_RATE=-1", "SERVER_TICK_RATE=2147483648",
                "SERVER_TICK_RATE=", "SERVER_TICK_RATE=abc", "SERVER_TICK_RATE=30.5" })
            {
                File.WriteAllText(path, invalid);
                Reject<ArgumentException>(() => ServerOptionsLoader.Load(Array.Empty<string>(), path));
            }
            foreach (string invalid in new[] { "SERVER_PORT", "SERVER_PORT=\"7777", "SERVER_PORT='7777' garbage", "BAD KEY=1" })
            {
                File.WriteAllText(path, invalid);
                Reject<FormatException>(() => ServerOptionsLoader.Load(Array.Empty<string>(), path));
            }
            File.WriteAllText(path, "SERVER_PORT=\"7777\" # comment\nSERVER_ROOM_COUNT=3\n");
            Require(ServerOptionsLoader.Load(Array.Empty<string>(), path).RoomCount == 3, "Quoted values and comments");
            Reject<ArgumentException>(() => ServerOptionsLoader.Load(new[] { "127.0.0.1", "7777", "3" }, path));
            File.WriteAllText(path, "SERVER_TICK_RATE=60\n");
            Environment.SetEnvironmentVariable("SERVER_TICK_RATE", "invalid");
            Reject<ArgumentException>(() => ServerOptionsLoader.Load(Array.Empty<string>(), path));
            Environment.SetEnvironmentVariable("SERVER_TICK_RATE", "2147483647");
            Require(ServerOptionsLoader.Load(Array.Empty<string>(), path).TickRate == int.MaxValue, "Positive int32 tick rate accepted");
            Reject<ArgumentOutOfRangeException>(() => new ServerOptions(tickRate: 0));
            Reject<ArgumentOutOfRangeException>(() => new ServerOptions(tickRate: -1));
            Require(new ServerOptions("127.0.0.1", 7777, 3).TickRate == 30, "Existing option constructor keeps default tick rate");
            Console.WriteLine("PASS .env parsing, configuration precedence, defaults, and validation");
        }
        finally
        {
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, previous[key]);
            File.Delete(path);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
