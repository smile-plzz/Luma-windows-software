namespace Luma.App;

public static class DiskGuard
{
    public const long MinimumFreeBytes = 500L * 1024 * 1024; // 500 MiB threshold

    public static bool HasSufficientSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppSettings.LocalRoot));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace >= MinimumFreeBytes;
        }
        catch
        {
            return true; // If drive query fails, don't block normal operation
        }
    }

    public static long AvailableFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppSettings.LocalRoot));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }
}

