using System.Runtime.InteropServices;

namespace ClipPlus.Core;

// Use the OS-provided SQLite. The Framework build uses the Windows engine;
// the modern target is retained only for fast cross-platform core tests.
public static class SqlitePlatform
{
#if NETFRAMEWORK
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
#endif

    private static readonly Lazy<bool> Initialized = new(() =>
    {
#if NETFRAMEWORK
        // Load only the Windows system copy, not a DLL next to a portable exe.
        // Keep the module loaded for the lifetime of SQLite's P/Invoke calls.
        if (LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"),
                IntPtr.Zero, 0x00000800) == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows SQLite is unavailable");
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#else
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
#endif
        SQLitePCL.raw.FreezeProvider();
        return true;
    });

    public static void Initialize() => _ = Initialized.Value;
}
