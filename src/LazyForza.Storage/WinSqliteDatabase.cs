using System.Runtime.InteropServices;

namespace LazyForza.Storage;

internal sealed class WinSqliteDatabase : IDisposable
{
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private readonly object gate = new();
    private IntPtr handle;

    public WinSqliteDatabase(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("LazyForza MVP uses Windows winsqlite3.dll.");
        var result = Native.sqlite3_open_v2(path, out handle, 0x00000002 | 0x00000004 | 0x00010000, IntPtr.Zero);
        if (result != Ok) throw CreateException(result, "open database");
    }

    public void Execute(string sql)
    {
        lock (gate)
        {
            var result = Native.sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var errorPointer);
            if (result == Ok) return;
            var message = errorPointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(errorPointer);
            if (errorPointer != IntPtr.Zero) Native.sqlite3_free(errorPointer);
            throw new InvalidOperationException($"SQLite error {result}: {message ?? ErrorMessage()} (SQL: {Shorten(sql)})");
        }
    }

    public string? QueryText(string sql)
    {
        lock (gate)
        {
            var statement = Prepare(sql);
            try
            {
                var result = Native.sqlite3_step(statement);
                if (result == Done) return null;
                if (result != Row) throw CreateException(result, "query text");
                var pointer = Native.sqlite3_column_text(statement, 0);
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                Native.sqlite3_finalize(statement);
            }
        }
    }

    public IReadOnlyList<IReadOnlyList<string?>> QueryRows(string sql)
    {
        lock (gate)
        {
            var statement = Prepare(sql);
            try
            {
                var rows = new List<IReadOnlyList<string?>>();
                var columnCount = Native.sqlite3_column_count(statement);
                while (true)
                {
                    var result = Native.sqlite3_step(statement);
                    if (result == Done) break;
                    if (result != Row) throw CreateException(result, "query rows");
                    var row = new string?[columnCount];
                    for (var column = 0; column < columnCount; column++)
                    {
                        var pointer = Native.sqlite3_column_text(statement, column);
                        row[column] = pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
                    }

                    rows.Add(row);
                }

                return rows;
            }
            finally
            {
                Native.sqlite3_finalize(statement);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (handle == IntPtr.Zero) return;
            var result = Native.sqlite3_close_v2(handle);
            handle = IntPtr.Zero;
            if (result != Ok) throw new InvalidOperationException($"SQLite close failed with code {result}.");
        }
    }

    private IntPtr Prepare(string sql)
    {
        var result = Native.sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != Ok) throw CreateException(result, "prepare statement");
        return statement;
    }

    private InvalidOperationException CreateException(int result, string operation) =>
        new($"SQLite failed to {operation} ({result}): {ErrorMessage()}");

    private string ErrorMessage()
    {
        var pointer = Native.sqlite3_errmsg(handle);
        return pointer == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(pointer) ?? "unknown error";
    }

    private static string Shorten(string sql) => sql.Length <= 100 ? sql : sql[..100] + "...";

    private static class Native
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, out IntPtr errorMessage);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr pointer);
    }
}

