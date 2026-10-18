using System.Runtime.InteropServices;

namespace PilotLauncher.Core.Install;

public sealed record MsiInfo(string Path, string ProductName, bool IsX64);

/// <summary>Reads ProductName and platform of an MSI through the Windows Installer COM automation object.</summary>
public static class MsiPackage
{
    private static readonly byte[] CompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool LooksLikeMsi(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var file = File.OpenRead(path);
        return file.Read(head) == 8 && head.SequenceEqual(CompoundFileMagic);
    }

    public static MsiInfo Read(string path)
    {
        var type = Type.GetTypeFromProgID("WindowsInstaller.Installer")
                   ?? throw new PlatformNotSupportedException("Windows Installer automation is not available.");
        dynamic installer = Activator.CreateInstance(type)!;
        dynamic? database = null;
        dynamic? view = null;
        dynamic? record = null;
        dynamic? summary = null;
        try
        {
            database = installer.OpenDatabase(path, 0);
            view = database.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='ProductName'");
            view.Execute();
            record = view.Fetch();
            string productName = record is null ? "" : record.StringData(1);
            summary = installer.SummaryInformation(path, 0);
            string template = summary.Property(7) ?? "";
            return new MsiInfo(path, productName, template.Contains("x64", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (object? com in new object?[] { record, view, summary, database, installer })
            {
                if (com is not null && Marshal.IsComObject(com))
                    Marshal.FinalReleaseComObject(com);
            }
        }
    }

    /// <summary>Picks the client package of an edition, preferring the 64-bit one.</summary>
    public static MsiInfo SelectClient(IEnumerable<MsiInfo> packages, Catalog.Edition edition)
    {
        var all = packages.ToList();
        var matching = all
            .Where(p => edition.MsiProductNames.Contains(p.ProductName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.IsX64 == Environment.Is64BitOperatingSystem)
            .ToList();
        return matching.FirstOrDefault()
               ?? throw new InvalidDataException(
                   $"No '{string.Join("' / '", edition.MsiProductNames)}' package in the setup. Found: " +
                   (all.Count == 0 ? "no MSI files" : string.Join(", ", all.Select(p => $"'{p.ProductName}'"))));
    }
}
