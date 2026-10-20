using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Notes;

/// <param name="Exact">False when the catalog version differs from the one in the notes (24.40.0.51967 and 24.40.0.52162) and was matched by its first numbers.</param>
public sealed record NotesMatch(ReleaseNotes Notes, bool Exact);

/// <param name="Changes">Items of the edition's product rows.</param>
/// <param name="Fixes">Fixes whose "Product:" prefix names the edition's product.</param>
/// <param name="UnattributedFixes">Fixes without a product (early 2024 releases); they cannot be assigned to an edition.</param>
public sealed record EditionDigest(ReleaseNotes Release, IReadOnlyList<string> Changes, IReadOnlyList<string> Fixes, int UnattributedFixes);

/// <summary>What the release notes say about one launcher edition, and which release of the notes belongs to a catalog version.</summary>
public static class EditionNotes
{
    private static readonly string[] IceProducts = ["Pilot-ICE Desktop", "Pilot-ICE"];
    private static readonly string[] BimProducts = ["Pilot-BIM Desktop", "Pilot-BIM"];
    private static readonly string[] MyAdminProducts = ["Pilot-myAdmin Desktop", "Pilot-myAdmin"]; // "Pilot-myAdmin Web" is another product

    /// <summary>
    /// Row titles of the notes that describe the edition. The plain names are from the 2024 releases, which did not split desktop and web.
    /// ICE Enterprise and ECM are built on the ICE client.
    /// </summary>
    public static IReadOnlyList<string> Products(Edition edition) => edition.Id switch
    {
        EditionId.Bim => BimProducts,
        EditionId.MyAdmin => MyAdminProducts,
        _ => IceProducts,
    };

    public static EditionDigest Digest(ReleaseNotes release, Edition edition)
    {
        var keys = Products(edition).Select(ReleaseNotesParser.ProductKey).ToHashSet();
        bool Belongs(string? product) => product is not null && keys.Contains(ReleaseNotesParser.ProductKey(product));

        var changes = release.Products
            .Where(p => Belongs(p.Name))
            .SelectMany(p => p.Items)
            .Where(i => i.Product is null || Belongs(i.Product))
            .Select(i => i.Text)
            .ToList();
        var fixes = release.Fixes.Where(i => Belongs(i.Product)).Select(i => i.Text).ToList();
        return new EditionDigest(release, changes, fixes, release.Fixes.Count(i => i.Product is null));
    }

    /// <summary>The notes of a catalog version: the same version, else the only release with the same first three numbers, else first two.</summary>
    public static NotesMatch? Find(IReadOnlyList<ReleaseNotes> all, Version version)
    {
        if (all.FirstOrDefault(r => r.Version == version) is { } exact)
            return new NotesMatch(exact, true);
        var sameBuild = all.Where(r => r.Version.Major == version.Major && r.Version.Minor == version.Minor && r.Version.Build == version.Build).ToList();
        if (sameBuild.Count == 1)
            return new NotesMatch(sameBuild[0], false);
        var sameMinor = all.Where(r => r.Version.Major == version.Major && r.Version.Minor == version.Minor).ToList();
        return sameMinor.Count == 1 ? new NotesMatch(sameMinor[0], false) : null;
    }

    /// <summary>Releases after the older of the two versions up to the newer one inclusive, newest first. Versions missing from the notes bound the range as they are.</summary>
    public static IReadOnlyList<ReleaseNotes> Between(IReadOnlyList<ReleaseNotes> all, Version a, Version b)
    {
        var (low, high) = a <= b ? (a, b) : (b, a);
        low = Find(all, low)?.Notes.Version ?? low;
        high = Find(all, high)?.Notes.Version ?? high;
        return all.Where(r => r.Version > low && r.Version <= high).OrderByDescending(r => r.Version).ToList();
    }
}
