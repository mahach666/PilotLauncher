using System.Diagnostics;
using Microsoft.Win32;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

/// <summary>A Pilot client installed by the regular setup (not by this launcher).</summary>
public sealed record SystemInstall(Edition Edition, Version Version, string InstallDir, string ExePath);

public interface IAsconRegistry
{
    IEnumerable<string> ProductNames();
    string? GetValue(string productName, string valueName);
}

/// <summary>Reads <c>HKLM\SOFTWARE\ASCON\&lt;product&gt;</c> in the 64-bit registry view.</summary>
public sealed class AsconRegistry : IAsconRegistry
{
    private const string Path = @"SOFTWARE\ASCON";

    public IEnumerable<string> ProductNames()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(Path);
        return key?.GetSubKeyNames() ?? [];
    }

    public string? GetValue(string productName, string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"{Path}\{productName}");
        return key?.GetValue(valueName) as string;
    }
}

public interface ISystemInstalls
{
    IReadOnlyList<SystemInstall> Detect();
}

public sealed class SystemInstallDetector : ISystemInstalls
{
    private readonly IAsconRegistry _registry;

    public SystemInstallDetector(IAsconRegistry registry) => _registry = registry;

    /// <summary>
    /// The registry says where a client is installed; the executable decides whether it still is (the "Programs and
    /// Features" list keeps stale entries) and what version it really is.
    /// </summary>
    public IReadOnlyList<SystemInstall> Detect()
    {
        var result = new List<SystemInstall>();
        foreach (var name in _registry.ProductNames())
        {
            var edition = Editions.All.FirstOrDefault(e => string.Equals(e.RegistryName, name, StringComparison.OrdinalIgnoreCase));
            if (edition is null)
                continue;
            var installDir = _registry.GetValue(name, "InstallPath");
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                continue;
            var exe = edition.ExeCandidates
                .Select(c => System.IO.Path.Combine(installDir, c))
                .FirstOrDefault(File.Exists);
            if (exe is null)
                continue;
            var info = FileVersionInfo.GetVersionInfo(exe);
            var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            result.Add(new SystemInstall(edition, version, installDir.TrimEnd('\\', '/'), exe));
        }

        return result;
    }
}
