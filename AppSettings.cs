namespace IRCClient;

public class AppSettings
{
    public bool ConnectOnStartup { get; set; }
    public bool ReconnectOnDisconnect { get; set; }

    // Which saved connection "Connect on startup" uses; falls back to the
    // first saved connection if this name no longer exists.
    public string LastConnectionName { get; set; } = "";

    // When set, closing the window hides it to the notification area and the
    // client keeps running (and stays connected) instead of quitting.
    public bool MinimizeToTrayOnClose { get; set; }

    // Catch-all directory for all logging; empty means nothing is written.
    public string LogDirectory { get; set; } = "";

    // Master on/off toggle for logging (File > Options > Log). Defaults to on
    // so settings saved before this flag existed keep logging to their
    // already-configured directory.
    public bool LoggingEnabled { get; set; } = true;

    // Windows (channel/PM/server tab names) excluded from logging via the
    // tab's right-click Start/Stop Logging toggle.
    public List<string> LoggingDisabledWindows { get; set; } = [];

    // Optional custom CTCP VERSION reply; when non-empty it replaces the
    // built-in "jclient irc by j0ker <version>" response.
    public string CustomVersionReply { get; set; } = "";

    // mIRC-style aliases, one per line: "/name commands" (see Tools > Alias).
    public string Aliases { get; set; } = "";

    // Paces our own outgoing lines so the server doesn't kill us for excess
    // flood (File > Options > Flood). On by default: the failure it prevents
    // is a disconnect, and the cost is a short delay on long pastes.
    public bool FloodProtection { get; set; } = true;

    // Masks whose messages are dropped, one per line (Tools > Ignore).
    // "nick", "nick!user@host" and wildcards (* ?) are all accepted.
    public string IgnoreMasks { get; set; } = "";

    // View menu
    public bool KeepOnTop { get; set; }

    // "Default font" — when set, this font is enforced across the whole app.
    // Empty family means no override (controls keep their own fonts).
    public string DefaultFontFamily { get; set; } = "";
    public float DefaultFontSize { get; set; } = 9f;
    public int DefaultFontStyle { get; set; } // System.Drawing.FontStyle as int
    public bool DefaultFontEnabled { get; set; }

    // "Default channel font" — like the default font, but applied only to
    // chat channel windows (#/& logs). Wins over the default font there.
    public string ChannelFontFamily { get; set; } = "";
    public float ChannelFontSize { get; set; } = 9.5f;
    public int ChannelFontStyle { get; set; }
    public bool ChannelFontEnabled { get; set; }
}
