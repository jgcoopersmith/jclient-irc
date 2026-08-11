using System.Runtime.InteropServices;

namespace IRCClient;

// Windows' own spell checking service (spellcheck.h), present since Windows 8
// with en-US shipped in the OS — no dictionary files or packages to carry.
// Every entry point degrades to "no errors" if the service is unavailable, so
// callers never have to care whether spell checking is actually running.

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    void get_SupportedLanguages(out IEnumStringNative langs);
    [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string tag, out int supported);
    void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string tag, out ISpellChecker checker);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    void get_LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string tag);
    void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError errors);
    void Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumStringNative suggestions);
    void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    [PreserveSig] int Next(out ISpellingError? error);
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
    void get_StartIndex(out uint index);
    void get_Length(out uint length);
    void get_CorrectiveAction(out int action);
    void get_Replacement([MarshalAs(UnmanagedType.LPWStr)] out string replacement);
}

[ComImport, Guid("00000101-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumStringNative
{
    [PreserveSig] int Next(int celt,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)] string[] rgelt,
        out int fetched);
}

// A misspelled run: where it starts in the text and how long it is.
public readonly record struct Misspelling(int Start, int Length);

public static class SpellCheck
{
    private static readonly ISpellChecker? Checker = Create();

    public static bool Available => Checker != null;

    private static ISpellChecker? Create()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
            if (type == null) return null;
            if (Activator.CreateInstance(type) is not ISpellCheckerFactory factory) return null;

            // Prefer the UI language, falling back to en-US, then to whatever
            // the system does support.
            foreach (var tag in new[] { System.Globalization.CultureInfo.CurrentUICulture.Name, "en-US" })
            {
                if (string.IsNullOrEmpty(tag)) continue;
                if (factory.IsSupported(tag, out int ok) == 0 && ok != 0)
                {
                    factory.CreateSpellChecker(tag, out var checker);
                    return checker;
                }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        catch (NotSupportedException) { }
        return null;
    }

    // Misspelled runs in the given text, empty when spell checking is off or
    // the text is a command (a "/join #chan" line is not prose).
    public static List<Misspelling> Check(string text)
    {
        var found = new List<Misspelling>();
        if (Checker == null || string.IsNullOrWhiteSpace(text)) return found;

        try
        {
            Checker.Check(text, out var errors);
            while (errors.Next(out var error) == 0 && error != null)
            {
                error.get_StartIndex(out uint start);
                error.get_Length(out uint length);
                if (length > 0) found.Add(new Misspelling((int)start, (int)length));
            }
        }
        catch (COMException) { }
        return found;
    }

    public static List<string> Suggest(string word)
    {
        var list = new List<string>();
        if (Checker == null || string.IsNullOrWhiteSpace(word)) return list;

        try
        {
            Checker.Suggest(word, out var suggestions);
            var buffer = new string[1];
            while (suggestions.Next(1, buffer, out int fetched) == 0 && fetched == 1)
                list.Add(buffer[0]);
        }
        catch (COMException) { }
        return list;
    }

    // Adds to the user's own dictionary, which Windows keeps across sessions
    // and shares with every app using this service.
    public static void Add(string word)
    {
        try { Checker?.Add(word); }
        catch (COMException) { }
    }

    // Ignores for this process only
    public static void Ignore(string word)
    {
        try { Checker?.Ignore(word); }
        catch (COMException) { }
    }
}
