using System.Globalization;
using System.Net;

namespace DeathWish.Server;

public static class ServerOptionsLoader
{
    /// <summary>Loads CLI, environment, file, then defaults. Relative file paths use the working directory.</summary>
    public static ServerOptions Load(string[] args, string envFilePath = ".env")
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 2) throw new ArgumentException("Usage: DeathWish.Server [IP address] [port 1-65535]");

        var values = ReadFile(envFilePath);
        string Resolve(string key, string fallback)
            => Environment.GetEnvironmentVariable(key) ?? values.GetValueOrDefault(key) ?? fallback;

        string address = args.ElementAtOrDefault(0) ?? Resolve("SERVER_ADDRESS", ServerOptions.DefaultAddress);
        string portText = args.ElementAtOrDefault(1) ?? Resolve("SERVER_PORT", ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture));
        string roomText = Resolve("SERVER_ROOM_COUNT", ServerOptions.DefaultRoomCount.ToString(CultureInfo.InvariantCulture));
        if (!IPAddress.TryParse(address, out _)) throw new ArgumentException("SERVER_ADDRESS must be an IP address.");
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new ArgumentException("SERVER_PORT must be an integer from 1 to 65535.");
        if (!int.TryParse(roomText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int roomCount) || roomCount < 1)
            throw new ArgumentException("SERVER_ROOM_COUNT must be a positive integer.");

        return new ServerOptions(address, port, roomCount);
    }

    private static Dictionary<string, string> ReadFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (FileNotFoundException) { return values; }
        catch (DirectoryNotFoundException) { return values; }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int separator = line.IndexOf('=');
            if (separator < 1) throw InvalidLine(index);
            string key = line[..separator].Trim();
            if (!(char.IsAsciiLetter(key[0]) || key[0] == '_') ||
                key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
                throw InvalidLine(index);

            string value = line[(separator + 1)..].Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                int closing = value.IndexOf(value[0], 1);
                if (closing < 0) throw InvalidLine(index);
                string trailing = value[(closing + 1)..].Trim();
                if (trailing.Length != 0 && !trailing.StartsWith('#')) throw InvalidLine(index);
                value = value[1..closing];
            }
            else
            {
                for (int offset = 0; offset < value.Length; offset++)
                {
                    if (value[offset] == '#' && (offset == 0 || char.IsWhiteSpace(value[offset - 1])))
                    {
                        value = value[..offset].TrimEnd();
                        break;
                    }
                }
            }

            values[key] = value;
        }
        return values;
    }

    private static FormatException InvalidLine(int index)
        => new($"Invalid .env syntax at line {index + 1}. Expected KEY=VALUE.");
}
