using System.Text.Json.Serialization;

namespace IRCClient;

public class SavedConnection
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 6667;
    public string Nick { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Password { get; set; } = "";

    public string Channels { get; set; } = ""; // comma-separated
}
