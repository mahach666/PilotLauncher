using System.Diagnostics;
using System.IO.Compression;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

/// <summary>A setup package (zip from the download center or the bare setup exe) whose edition and version are known.</summary>
public sealed record SetupFile(string Path, Edition Edition, Version Version);

/// <summary>Works out which edition and version a setup file on disk belongs to, without running it.</summary>
public static class SetupFileInspector
{
    /// <summary>
    /// The file name first (<c>Pilot-ICE-Enterprise-setup_26.9.0.61368.zip</c>), then the version resource of the exe
    /// (for a zip: of the exe inside it). Null when neither tells, so the caller can ask the user.
    /// </summary>
    /// <param name="tempDir">Where a setup exe is taken out of a zip to read it; the copy is removed before returning.</param>
    public static SetupFile? Identify(string path, string tempDir)
    {
        if (Editions.TryParseSetupName(System.IO.Path.GetFileName(path), out var edition, out var version, out _))
            return new SetupFile(path, edition, version);

        if (!IsZip(path))
            return FromVersionInfo(path);

        var work = System.IO.Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var exe = System.IO.Path.Combine(work, "setup.exe");
            VersionInstaller.ExtractSetupFromZip(path, exe);
            return FromVersionInfo(exe) is { } found ? found with { Path = path } : null;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    public static bool IsZip(string path) => string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>ProductName must be the name of one edition's client (<see cref="Edition.MsiProductNames"/>); FileVersion gives the version.</summary>
    private static SetupFile? FromVersionInfo(string exePath)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var edition = Editions.All.FirstOrDefault(e =>
            e.MsiProductNames.Contains(info.ProductName?.Trim() ?? "", StringComparer.OrdinalIgnoreCase));
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        return edition is null || version == new Version(0, 0, 0, 0) ? null : new SetupFile(exePath, edition, version);
    }
}
