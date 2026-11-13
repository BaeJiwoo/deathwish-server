using DeathWish.Server;

static class ServerOptionsTests
{
    public static void Run()
    {
        string[] keys = { "SERVER_ADDRESS", "SERVER_PORT", "SERVER_ROOM_COUNT" };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        string path = Path.Combine(Path.GetTempPath(), $"deathwish-options-{Guid.NewGuid():N}.env");
        try
        {
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);
            ServerOptions defaults = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(defaults.RoomCount == ServerOptions.DefaultRoomCount && defaults.Port == ServerOptions.DefaultPort, "Missing file defaults");
            File.WriteAllText(path, "# Test settings\nSERVER_ADDRESS='127.0.0.2'\nSERVER_PORT = 8000 # port\nSERVER_ROOM_COUNT=4\nSERVER_ROOM_COUNT=5\n");
            ServerOptions file = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(file.Address == "127.0.0.2" && file.Port == 8000 && file.RoomCount == 5, "File parsing and last duplicate wins");
            Require(Environment.GetEnvironmentVariable("SERVER_PORT") is null, "File does not mutate process environment");
            Environment.SetEnvironmentVariable("SERVER_PORT", "9000");
            Environment.SetEnvironmentVariable("SERVER_ROOM_COUNT", "6");
            ServerOptions environment = ServerOptionsLoader.Load(Array.Empty<string>(), path);
            Require(environment.Port == 9000 && environment.RoomCount == 6, "Environment overrides file");
            ServerOptions cli = ServerOptionsLoader.Load(new[] { "::1", "10000" }, path);
            Require(cli.Address == "::1" && cli.Port == 10000 && cli.RoomCount == 6, "CLI overrides environment");
            Environment.SetEnvironmentVariable("SERVER_PORT", "invalid");
            Require(ServerOptionsLoader.Load(new[] { "127.0.0.1", "7777" }, path).Port == 7777, "CLI shadows invalid lower priority value");
            Reject<ArgumentException>(() => ServerOptionsLoader.Load(Array.Empty<string>(), path));
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, null);

            foreach (string invalid in new[] { "SERVER_PORT=0", "SERVER_PORT=65536", "SERVER_PORT=abc", "SERVER_ADDRESS=nope",
                "SERVER_ROOM_COUNT=0", "SERVER_ROOM_COUNT=-1", "SERVER_ROOM_COUNT=2147483648", "SERVER_ROOM_COUNT=" })
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
