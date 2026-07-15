using System.Text.Json;

namespace IRCClient;

// Persists the saved-connection library to a JSON file under %AppData%\IRCClient
public static class ConnectionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IRCClient", "connections.json");

    public static List<SavedConnection> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? [];
        }
        catch
        {
            return [];
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
            File.WriteAllText(FilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
