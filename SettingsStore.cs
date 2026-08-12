using System.Text.Json;

namespace IRCClient;

// Persists app settings to a JSON file under %AppData%\IRCClient
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IRCClient", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    // Best-effort like ConnectionStore.Save; settings are low-stakes enough
    // that a failed write shouldn't interrupt the user.
    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            // Written beside the real file and swapped in. Settings are saved
            // often — every window move, every toggle — so a crash landing
            // mid-write is a real possibility, and a half-written file would
            // lose the lot.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        catch { }
    }
}
