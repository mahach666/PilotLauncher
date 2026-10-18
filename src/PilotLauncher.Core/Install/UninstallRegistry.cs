using System.Text.RegularExpressions;
using Microsoft.Win32;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

/// <summary>One "Programs and Features" record.</summary>
public sealed record UninstallEntry(string DisplayName, string? DisplayVersion, string? BundleVersion, string? UninstallString);

public interface IUninstallRegistry
{
    IEnumerable<UninstallEntry> Entries();
}

/// <summary>Reads <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c> in both registry views.</summary>
public sealed class UninstallRegistry : IUninstallRegistry
{
    private const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IEnumerable<UninstallEntry> Entries()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hklm.OpenSubKey(Path);
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string displayName)
                    continue;
                yield return new UninstallEntry(
                    displayName,
                    key.GetValue("DisplayVersion") as string,
                    key.GetValue("BundleVersion") as string,
                    key.GetValue("UninstallString") as string);
            }
        }
    }
}

public abstract record UninstallCommand;

/// <summary><c>msiexec /x</c> of an MSI product.</summary>
public sealed record MsiUninstall(string ProductCode) : UninstallCommand;

/// <summary>Older installs: the Burn bundle copy in the package cache, started with <c>/uninstall</c>.</summary>
public sealed record BundleUninstall(string ExePath) : UninstallCommand;

/// <summary>
/// Picks the record that removes exactly the client of one edition and version. The list in "Programs and Features"
/// keeps stale records (an old version beside the real one), so version and product name must both match, and nothing
/// but the two known command shapes is accepted: the registry string itself is never executed.
/// </summary>
public static class ClientUninstallLocator
{
    private static readonly Regex Msi = new(
        @"^\s*MsiExec(?:\.exe)?\s+/[XI]\s*(?<code>\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Bundle = new(@"^\s*""(?<exe>[^""]+)""\s+/uninstall\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex PackageFolder = new(
        @"^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant);

    /// <summary>Candidates in order of preference: the bundle (removes everything the setup put in) before the bare MSI.</summary>
    public static IReadOnlyList<UninstallCommand> Find(IEnumerable<UninstallEntry> entries, Edition edition, Version installed, string packageCacheRoot)
    {
        var bundles = new List<UninstallCommand>();
        var msis = new List<UninstallCommand>();
        foreach (var entry in entries)
        {
            if (!edition.MsiProductNames.Contains(entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                || !Version.TryParse(entry.BundleVersion ?? entry.DisplayVersion, out var version)
                || version != installed
                || entry.UninstallString is null)
                continue;

            if (Msi.Match(entry.UninstallString) is { Success: true } msi)
                msis.Add(new MsiUninstall(msi.Groups["code"].Value));
            else if (Bundle.Match(entry.UninstallString) is { Success: true } bundle
                     && IsPackageCacheSetup(bundle.Groups["exe"].Value, edition, packageCacheRoot))
                bundles.Add(new BundleUninstall(bundle.Groups["exe"].Value));
        }

        return [.. bundles, .. msis];
    }

    private static bool IsPackageCacheSetup(string exe, Edition edition, string packageCacheRoot)
    {
        if (!System.IO.Path.IsPathFullyQualified(exe) || System.IO.Path.GetFullPath(exe) != exe)
            return false;
        var folder = System.IO.Path.GetDirectoryName(exe);
        return string.Equals(System.IO.Path.GetDirectoryName(folder), packageCacheRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
               && PackageFolder.IsMatch(System.IO.Path.GetFileName(folder)!)
               && string.Equals(System.IO.Path.GetFileName(exe), edition.SetupPrefix + ".exe", StringComparison.OrdinalIgnoreCase);
    }
}
