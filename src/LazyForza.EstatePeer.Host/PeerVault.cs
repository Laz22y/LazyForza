using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LazyForza.RaceServer.Core;

namespace LazyForza.EstatePeer.Host;

internal static class PeerVault
{
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(Transform(File.ReadAllBytes(path), false))
        ?? throw new InvalidDataException("房间存档无效。");

    public static void Write<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Transform(bytes, true));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // User-scoped Windows DPAPI. No portable private key or plaintext recovery tokens on disk.
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class PeerPersistence(string directory) : IRaceStatePersistence
{
    private readonly string path = Path.Combine(directory, "race-state.dat");
    public RaceRecoveryState? LoadRecoveryState() => File.Exists(path) ? PeerVault.Read<RaceRecoveryState>(path) : null;
    public void SaveRecoveryState(RaceRecoveryState state) => PeerVault.Write(path, state);
    // The authority's bounded events/results are already in its atomic recovery state.
    public void AppendAudit(RaceAuditEntry entry) { }
}
