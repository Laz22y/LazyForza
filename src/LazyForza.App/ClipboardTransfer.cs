using System.Runtime.InteropServices;
using System.Windows;

namespace LazyForza.App;

internal static class ClipboardTransfer
{
    // Clipboard ownership is shared with other processes; temporary contention is normal.
    internal static async Task<bool> TryCopyAsync(string text, CancellationToken token, Action<string>? write = null)
    {
        write ??= Clipboard.SetText;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { write(text); return true; }
            catch (ExternalException)
            {
                if (attempt == 4) return false;
                await Task.Delay(100, token);
            }
        }
        return false;
    }
}
