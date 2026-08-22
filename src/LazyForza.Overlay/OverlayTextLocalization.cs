namespace LazyForza.Overlay;

public static class OverlayTextLocalization
{
    private static Func<string, string> translator = static value => value;

    public static void Configure(Func<string, string>? value) =>
        Volatile.Write(ref translator, value ?? (static text => text));

    internal static string Text(string value) =>
        Volatile.Read(ref translator)(value);
}
