using System.Text.Json;

namespace IRCClient;

// Persists the saved-connection library to a JSON file under %AppData%\IRCClient
public static class ConnectionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IRCClient", "connections.json");

    public static List<SavedConnection> Load() => LoadOrNull() ?? [];

    // Null means the file is there but could not be read or parsed — which is
    // not the same as "there are no connections". Callers about to write must
    // tell those apart, or a bad read turns into an overwrite of good data.
    public static List<SavedConnection>? LoadOrNull()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? [];
        }
        catch
        {
            return null;
        }
    }

    // Returns false (instead of throwing) if the file couldn't be written, e.g. the
    // AppData folder is read-only or connections.json is locked by another process.
    public static bool Save(List<SavedConnection> connections)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(connections, new JsonSerializerOptions { WriteIndented = true });

            // Write beside the real file and swap it in, so a crash or a full
            // disk mid-write leaves the previous file intact rather than a
            // half-written one.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
