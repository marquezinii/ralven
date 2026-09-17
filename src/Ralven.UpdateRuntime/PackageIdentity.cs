using System.Runtime.InteropServices;

namespace Ralven.UpdateRuntime;

public static class PackageIdentity
{
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows()) return false;
        uint length = 0;
        // ERROR_INSUFFICIENT_BUFFER means a package name exists; no buffer is needed.
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 122;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
