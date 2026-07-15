namespace IRCClient;

public class AppSettings
{
    public bool ConnectOnStartup { get; set; }
    public bool ReconnectOnDisconnect { get; set; }

    // Which saved connection "Connect on startup" uses; falls back to the
    // first saved connection if this name no longer exists.
    public string LastConnectionName { get; set; } = "";

    // Catch-all directory for all logging; empty disables logging.
    public string LogDirectory { get; set; } = "";
}
