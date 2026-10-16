using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DepthStrap.Recovery;

/// <summary>Fixed-query cookie maintenance using Windows' SQLite library. Cookie values are never selected or decrypted.</summary>
internal sealed class BrowserCookieDatabase : IDisposable
{
    private IntPtr _database;
    private readonly List<SafeFileHandle> _anchors = new();
    internal static IDisposable GuardParents(string file)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            for (string? parent = Path.GetDirectoryName(file); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                var handle = Native.CreateFile(CookieReset.NativePath(parent), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                handles.Add(handle);
                if (handle.IsInvalid || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("A browser profile directory is inaccessible or redirected.");
                var final = new StringBuilder(32768); uint count = Native.GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
                if (count == 0 || count >= final.Capacity || !CookieReset.NormalizeFinalPath(final.ToString()).Equals(parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A browser profile directory changed during verification.");
            }
            return new DirectoryGuard(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class DirectoryGuard(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); } }
    internal BrowserCookieDatabase(string path, bool writable, bool createFixture = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("A browser cookie database must have an absolute path.");
        path = Path.GetFullPath(path);
        try
        {
            // Hold directories and existing sidecars against rename/reparse swaps.
            for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) Anchor(parent, true);
            foreach (string file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                try { File.GetAttributes(file); }
                catch (FileNotFoundException) { if (file == path && !createFixture) throw; continue; }
                Anchor(file, false);
            }
            int result = Native.sqlite3_open_v2(path, out _database, (writable ? 2 : 1) | (createFixture ? 4 : 0) | 0x10000, IntPtr.Zero);
            if (result != 0) throw new IOException("The browser cookie database is locked or unavailable. Close the browser completely and retry.");
            Native.sqlite3_busy_timeout(_database, 1000);
            Execute("PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=OFF;");
        }
        catch { Dispose(); throw; }
    }
    private void Anchor(string path, bool directory)
    {
        var handle = Native.CreateFile(CookieReset.NativePath(path), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A browser profile is inaccessible or in use. Close the browser before resetting its Roblox cookies."); }
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected browser profiles are not followed.");
            var final = new StringBuilder(32768);
            uint count = Native.GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (count == 0 || count >= final.Capacity || !CookieReset.NormalizeFinalPath(final.ToString()).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A browser cookie path could not be verified.");
            _anchors.Add(handle);
        }
        catch { handle.Dispose(); throw; }
    }
    internal void Execute(string sql)
    {
        int result = Native.sqlite3_exec(_database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        if (result != 0) throw new IOException("Browser cookie maintenance failed (SQLite status " + result + "). The reset is incomplete; close the browser and review a new preview.");
    }
    internal long Scalar(string sql)
    {
        int result = Native.sqlite3_prepare_v2(_database, sql, -1, out var statement, IntPtr.Zero);
        try
        {
            if (result != 0 || Native.sqlite3_step(statement) != 100) throw new IOException("The browser cookie format is unavailable or unsupported.");
            return Native.sqlite3_column_int64(statement, 0);
        }
        finally { if (statement != IntPtr.Zero) Native.sqlite3_finalize(statement); }
    }
    internal void ValidateSchema(bool firefox)
    {
        string table = firefox ? "moz_cookies" : "cookies";
        string host = firefox ? "host" : "host_key";
        if (Scalar($"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}' AND lower(sql) NOT LIKE '%virtual%';") != 1
            || Scalar("SELECT count(*) FROM sqlite_master WHERE type='trigger';") != 0
            || Scalar($"SELECT count(*) FROM pragma_table_info('{table}') WHERE name='{host}';") != 1)
            throw new IOException("The browser cookie schema is unsupported. Its database was preserved.");
    }
    internal static string Predicate(bool firefox)
    {
        string column = firefox ? "host" : "host_key";
        return $"(lower({column}) IN ('roblox.com','.roblox.com') OR lower({column}) LIKE '%.roblox.com')";
    }
    internal long Count(bool firefox) => Scalar($"SELECT count(*) FROM {(firefox ? "moz_cookies" : "cookies")} WHERE {Predicate(firefox)};");
    internal void Begin() => Execute("BEGIN IMMEDIATE;");
    internal void Rollback() { if (_database != IntPtr.Zero) { try { Execute("ROLLBACK;"); } catch (IOException) { } } }
    internal long DeleteRoblox(bool firefox)
    {
        Execute("PRAGMA secure_delete=ON;");
        if (Scalar("PRAGMA secure_delete;") != 1) throw new IOException("SQLite could not enable cookie-content clearing.");
        Execute($"DELETE FROM {(firefox ? "moz_cookies" : "cookies")} WHERE {Predicate(firefox)};");
        return Native.sqlite3_changes(_database);
    }
    internal void Commit() => Execute("COMMIT;");
    internal void Checkpoint()
    {
        int result = Native.sqlite3_wal_checkpoint_v2(_database, IntPtr.Zero, 3, out _, out _); // TRUNCATE, through SQLite
        if (result != 0) throw new IOException("Browser cookies were removed, but their journal is still busy. The reset is incomplete; close the browser and retry.");
    }
    public void Dispose()
    {
        if (_database != IntPtr.Zero) { Native.sqlite3_close_v2(_database); _database = IntPtr.Zero; }
        foreach (var anchor in _anchors) anchor.Dispose(); _anchors.Clear();
    }
    private static class Native
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_close_v2(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr data, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern void sqlite3_free(IntPtr memory);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_changes(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int sqlite3_wal_checkpoint_v2(IntPtr database, IntPtr name, int mode, out int log, out int checkpointed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint count, uint flags);
    }
}
