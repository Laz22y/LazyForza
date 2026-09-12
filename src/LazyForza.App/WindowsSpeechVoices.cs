using System.Globalization;
using System.Runtime.InteropServices;

namespace LazyForza.App;

internal sealed record WindowsSpeechVoice(string Id, string Name, string Language)
{
    public override string ToString() => $"{Name} ({Language})";
}

internal static class WindowsSpeechVoices
{
    public static Task<IReadOnlyList<WindowsSpeechVoice>> LoadAsync()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<WindowsSpeechVoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? voice = null, tokens = null;
            try
            {
                voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
                tokens = ((dynamic)voice!).GetVoices("", "");
                var result = new List<WindowsSpeechVoice>();
                for (var index = 0; index < Math.Min(64, (int)((dynamic)tokens).Count); index++)
                {
                    object token = ((dynamic)tokens).Item(index);
                    try
                    {
                        var languages = ((string)((dynamic)token).GetAttribute("Language")).Split(';');
                        foreach (var value in languages)
                        {
                            if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid)) continue;
                            var language = CultureInfo.GetCultureInfo(lcid).Name;
                            if (!language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                                !language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) continue;
                            result.Add(new((string)((dynamic)token).Id, (string)((dynamic)token).GetDescription(), language));
                            break;
                        }
                    }
                    catch (ArgumentException) { /* Ignore an invalid language registered by one voice. */ }
                    catch (COMException) { /* One broken token must not hide other installed voices. */ }
                    finally { Release(token); }
                }
                completion.TrySetResult(result);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                Release(tokens);
                Release(voice);
            }
        }) { IsBackground = true, Name = "LazyForza voice catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Release(object? value)
    {
        try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        catch (Exception) { /* Voice catalog cleanup cannot terminate the application. */ }
    }
}
