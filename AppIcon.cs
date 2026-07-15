namespace IRCClient;

// Loads icon.ico from the assembly's embedded resources so every window can
// use it without depending on shell icon extraction from the running exe
// (unreliable while the process has the file open) or a loose file on disk.
// Cached: Form.Dispose() only disposes its derived small-icon copy, never the
// Icon assigned via the public Icon property, so a fresh Icon() per call would
// leak a native GDI handle every time a dialog is opened.
public static class AppIcon
{
    private static readonly Icon? Cached = Load();

    public static Icon? Get() => Cached;

    private static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("IRCClient.icon.ico");
        return stream != null ? new Icon(stream) : null;
    }
}
