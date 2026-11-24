using System.Runtime.InteropServices;

namespace ClipPlus.Core;

// Use the Windows-provided engine rather than distributing a second native
// SQLite build. On Linux the core tests use the distribution's SQLite library.
public static class SqlitePlatform
{
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            var assembly = typeof(SQLitePCL.SQLite3Provider_winsqlite3).Assembly;
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
                name is "winsqlite3" or "winsqlite3.dll"
                    ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")) : 0);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        }
        else
        {
            var assembly = typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly;
            if (OperatingSystem.IsLinux())
                NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
                    name == "sqlite3" && NativeLibrary.TryLoad("libsqlite3.so.0", out var handle) ? handle : 0);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        }
        SQLitePCL.raw.FreezeProvider();
        return true;
    });

    public static void Initialize() => _ = Initialized.Value;
}
