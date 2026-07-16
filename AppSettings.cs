namespace IRCClient;

public class AppSettings
{
    public bool ConnectOnStartup { get; set; }
    public bool ReconnectOnDisconnect { get; set; }

    // Which saved connection "Connect on startup" uses; falls back to the
    // first saved connection if this name no longer exists.
    public string LastConnectionName { get; set; } = "";

    // Catch-all directory for all logging; empty means nothing is written.
    public string LogDirectory { get; set; } = "";

    // Master on/off toggle for logging (File > Options > Log). Defaults to on
    // so settings saved before this flag existed keep logging to their
    // already-configured directory.
    public bool LoggingEnabled { get; set; } = true;

    // Windows (channel/PM/server tab names) excluded from logging via the
    // tab's right-click Start/Stop Logging toggle.
    public List<string> LoggingDisabledWindows { get; set; } = [];
}
