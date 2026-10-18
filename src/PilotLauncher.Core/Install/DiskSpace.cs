using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PilotLauncher.Core.Install;

public sealed class InsufficientDiskSpaceException : IOException
{
    public long RequiredBytes { get; }
    public long FreeBytes { get; }

    public InsufficientDiskSpaceException(string path, long requiredBytes, long freeBytes)
        : base($"Not enough free space for {path}: need about {requiredBytes / (1 << 20)} MB, free {freeBytes / (1 << 20)} MB.")
    {
        RequiredBytes = requiredBytes;
        FreeBytes = freeBytes;
    }
}

public sealed class DiskSpace
{
    private readonly Func<string, long> _freeBytes;

    public DiskSpace() : this(GetFreeBytes) { }

    public DiskSpace(Func<string, long> freeBytes) => _freeBytes = freeBytes;

    /// <summary>
    /// Peak use while installing from a zip: zip + the extracted setup exe, then the carved cabinet, then the unpacked
    /// client. Files are deleted as soon as the next stage has them, so three times the zip plus a margin is enough.
    /// </summary>
    public static long RequiredForInstall(long zipSizeBytes) => zipSizeBytes * 3 + (256L << 20);

    public void EnsureFree(string path, long requiredBytes)
    {
        var free = _freeBytes(path);
        if (free < requiredBytes)
            throw new InsufficientDiskSpaceException(path, requiredBytes, free);
    }

    public static long GetFreeBytes(string path)
    {
        var dir = Path.GetFullPath(path);
        while (!Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException(path);
        if (!GetDiskFreeSpaceEx(dir, out var available, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read free space of {dir}");
        return (long)available;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
