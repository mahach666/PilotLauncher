using System.Text.RegularExpressions;

namespace PilotLauncher.Core.Catalog;

public enum EditionId
{
    IceEnterprise,
    Ice,
    Bim,
    Ecm,
}

/// <param name="SetupPrefix">File name prefix in the download center, e.g. <c>Pilot-ICE-Enterprise-setup</c>.</param>
/// <param name="Code">One letter used in generated Windows account names.</param>
/// <param name="RegistryName">Sub key of <c>HKLM\SOFTWARE\ASCON</c> written by the system installer.</param>
/// <param name="MsiProductNames">ProductName values of the client MSI inside the setup bundle.</param>
/// <param name="ExeCandidates">Client executable names, in order of preference.</param>
public sealed record Edition(
    EditionId Id,
    string DisplayName,
    string SetupPrefix,
    char Code,
    string RegistryName,
    IReadOnlyList<string> MsiProductNames,
    IReadOnlyList<string> ExeCandidates)
{
    /// <summary>Folder name inside the store, e.g. <c>Pilot-ICE-Enterprise</c>.</summary>
    public string FolderName => SetupPrefix[..^"-setup".Length];

    public override string ToString() => DisplayName;
}

public static class Editions
{
    // shortcut: ICE and ECM executable/registry names are inferred from the BIM/Enterprise naming
    // (Ascon.Pilot.Pilot<Edition>.exe, HKLM\SOFTWARE\ASCON\Pilot-<Edition>), not verified on a real install.
    public static readonly IReadOnlyList<Edition> All =
    [
        new(EditionId.IceEnterprise, "ICE Enterprise", "Pilot-ICE-Enterprise-setup", 'E', "Pilot-ICE Enterprise",
            ["Pilot-ICE Enterprise"], ["Ascon.Pilot.PilotEnterprise.exe"]),
        new(EditionId.Ice, "ICE", "Pilot-ICE-setup", 'I', "Pilot-ICE",
            ["Pilot-ICE"], ["Ascon.Pilot.PilotICE.exe", "Ascon.Pilot.Pilot.exe"]),
        new(EditionId.Bim, "BIM", "Pilot-BIM-setup", 'B', "Pilot-BIM",
            ["Pilot-BIM"], ["Ascon.Pilot.PilotBIM.exe"]),
        new(EditionId.Ecm, "ECM", "Pilot-ECM-setup", 'C', "Pilot-ECM",
            ["Pilot-ECM"], ["Ascon.Pilot.PilotECM.exe"]),
    ];

    private static readonly Regex SetupFile = new(
        @"^(?<prefix>Pilot-(?:ICE-Enterprise|ICE|BIM|ECM)-setup)_(?<version>\d+\.\d+\.\d+\.\d+)\.(?<ext>zip|exe)$",
        RegexOptions.CultureInvariant);

    public static Edition Get(EditionId id) => All.First(e => e.Id == id);

    /// <summary>A download center file name: only the <c>.zip</c> packages are published there.</summary>
    public static bool TryParseSetupFile(string fileName, out Edition edition, out Version version) =>
        TryParseSetupName(fileName, out edition, out version, out var extension) && extension == ".zip";

    /// <summary>A setup package name as it lies on disk: <c>Pilot-&lt;edition&gt;-setup_&lt;version&gt;</c> with <c>.zip</c> or <c>.exe</c>.</summary>
    public static bool TryParseSetupName(string fileName, out Edition edition, out Version version, out string extension)
    {
        edition = null!;
        version = null!;
        extension = "";
        var m = SetupFile.Match(fileName);
        if (!m.Success)
            return false;
        edition = All.First(e => e.SetupPrefix == m.Groups["prefix"].Value);
        version = Version.Parse(m.Groups["version"].Value);
        extension = "." + m.Groups["ext"].Value;
        return true;
    }
}
