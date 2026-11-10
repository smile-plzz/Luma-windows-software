using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Luma.App;

internal static class ShellFiles
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Operation
    {
        public nint Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref Operation operation);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    public static bool Run(nint owner, uint function, IEnumerable<string> paths, string? destination = null)
    {
        var files = paths.ToArray();
        if (files.Length == 0) return false;
        var operation = new Operation
        {
            Window = owner,
            Function = function,
            From = string.Join('\0', files) + "\0\0",
            To = destination is null ? null : destination + "\0\0",
            Flags = 0x0040 | 0x0200 // Allow Windows undo and use native collision/progress/confirmation dialogs.
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 && !operation.Aborted) throw new Win32Exception(result, "Windows could not complete the file operation.");
        return !operation.Aborted;
    }

    public static bool RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                if (SHParseDisplayName(dir, IntPtr.Zero, out var folderPidl, 0, out _) == 0)
                {
                    try
                    {
                        if (SHParseDisplayName(path, IntPtr.Zero, out var filePidl, 0, out _) == 0)
                        {
                            try
                            {
                                if (SHOpenFolderAndSelectItems(folderPidl, 1, new[] { filePidl }, 0) == 0)
                                    return true;
                            }
                            finally { CoTaskMemFree(filePidl); }
                        }
                    }
                    finally { CoTaskMemFree(folderPidl); }
                }
            }
        }
        catch { }

        // Fallback using ProcessStartInfo with ArgumentList (avoids command-line string interpolation)
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add($"/select,{Path.GetFullPath(path)}");
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenInDefaultApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var psi = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
