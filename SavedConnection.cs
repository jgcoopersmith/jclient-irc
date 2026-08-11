using System.Text.Json.Serialization;

namespace IRCClient;

public class SavedConnection
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 6667;
    public string Nick { get; set; } = "";

    // Tried when Nick is already taken, before falling back to a numbered one.
    // Empty (the default for entries saved before this existed) skips straight
    // to the numbered fallback.
    public string SecondNick { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Password { get; set; } = "";

    public string Channels { get; set; } = ""; // comma-separated
}
