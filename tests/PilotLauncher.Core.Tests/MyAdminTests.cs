using System.Diagnostics;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Notes;

namespace PilotLauncher.Core.Tests;

/// <summary>
/// myAdmin is the administrator console of Pilot, shipped like the clients as <c>Pilot-myAdmin-setup_&lt;version&gt;.zip</c>.
/// The names below were read from an installed 26.13 (exe, ProductName, registry key, "Programs and Features" name) and from the
/// MSI inside the real setup.
/// </summary>
[TestClass]
public class MyAdminTests
{
    private static readonly Edition MyAdmin = Editions.Get(EditionId.MyAdmin);

    private sealed class Registry : IAsconRegistry
    {
        public Dictionary<string, string> InstallPaths { get; } = new();
        public IEnumerable<string> ProductNames() => InstallPaths.Keys;
        public string? GetValue(string productName, string valueName) => valueName == "InstallPath" ? InstallPaths.GetValueOrDefault(productName) : null;
    }

    [TestMethod]
    public void EditionMatchesTheInstalledProduct()
    {
        Assert.AreEqual("myAdmin", MyAdmin.DisplayName);
        Assert.AreEqual("Pilot-myAdmin-setup", MyAdmin.SetupPrefix);
        Assert.AreEqual("Pilot-myAdmin", MyAdmin.FolderName);
        Assert.AreEqual("Pilot-myAdmin", MyAdmin.RegistryName);
        CollectionAssert.AreEqual(new[] { "Pilot-myAdmin" }, MyAdmin.MsiProductNames.ToArray());
        CollectionAssert.AreEqual(new[] { "Ascon.Pilot.MyAdmin.exe" }, MyAdmin.ExeCandidates.ToArray());
    }

    [TestMethod]
    public void EveryEditionHasItsOwnLetterAndNames()
    {
        Assert.AreEqual(Editions.All.Count, Editions.All.Select(e => e.Code).Distinct().Count());
        Assert.AreEqual(Editions.All.Count, Editions.All.Select(e => e.SetupPrefix).Distinct().Count());
        Assert.AreEqual(Editions.All.Count, Editions.All.Select(e => e.RegistryName).Distinct().Count());
    }

    [TestMethod]
    public void OnlyMyAdminHasNoExtensions() =>
        CollectionAssert.AreEqual(new[] { EditionId.MyAdmin }, Editions.All.Where(e => !e.SupportsExtensions).Select(e => e.Id).ToArray());

    [TestMethod]
    public void SetupNamesAreRecognised()
    {
        Assert.IsTrue(Editions.TryParseSetupFile("Pilot-myAdmin-setup_26.13.0.62219.zip", out var edition, out var version));
        Assert.AreEqual(EditionId.MyAdmin, edition.Id);
        Assert.AreEqual(new Version(26, 13, 0, 62219), version);

        Assert.IsTrue(Editions.TryParseSetupName("Pilot-myAdmin-setup_26.13.0.62219.exe", out edition, out _, out var extension));
        Assert.AreEqual(EditionId.MyAdmin, edition.Id);
        Assert.AreEqual(".exe", extension);

        Assert.IsFalse(Editions.TryParseSetupFile("Pilot-myAdmin-setup.zip", out _, out _), "the unversioned latest file is not a version");
        Assert.IsFalse(Editions.TryParseSetupFile("Pilot-myAdmin-Server-setup_26.13.0.62219.zip", out _, out _));
    }

    [TestMethod]
    public void ListingGivesMyAdminVersionsAndSkipsTheRest()
    {
        const string html = """
            <html><body><pre><a href="../">../</a>
            <a href="Pilot-myAdmin-setup.zip">Pilot-myAdmin-setup.zip</a>                  07-Jul-2026 13:27    116M
            <a href="Pilot-myAdmin-setup_26.13.0.62219.zip">Pilot-myAdmin-setup_26.13.0.62219.zip</a>      07-Jul-2026 13:27    116M
            <a href="Pilot-myAdmin-setup_26.11.0.61894.zip">Pilot-myAdmin-setup_26.11.0.61894.zip</a>      29-May-2026 18:15    116M
            <a href="Pilot-Server-setup_26.13.0.62219.zip">Pilot-Server-setup_26.13.0.62219.zip</a>       07-Jul-2026 13:25    90M
            <a href="Pilot-BIM-setup_26.13.0.62219.zip">Pilot-BIM-setup_26.13.0.62219.zip</a>          07-Jul-2026 13:26    176M
            </pre></body></html>
            """;

        var entries = ReleaseListingParser.Parse(html);

        Assert.AreEqual(3, entries.Count);
        CollectionAssert.AreEqual(new[] { EditionId.Bim, EditionId.MyAdmin, EditionId.MyAdmin }, entries.Select(e => e.Edition.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "26.13.0.62219", "26.11.0.61894" },
            entries.Where(e => e.Edition.Id == EditionId.MyAdmin).Select(e => e.Version.ToString()).ToArray());
    }

    [TestMethod]
    public void AccountNameHasTheMyAdminLetterAndIsAcceptedByTheElevatedHelper()
    {
        var name = AccountNaming.UserName("abcd", MyAdmin, new Version(26, 13, 0, 62219));

        Assert.AreEqual("plabcdM26x13x62219", name);
        Assert.IsTrue(AccountNaming.IsLauncherAccount(name));
        Assert.IsFalse(AccountNaming.IsLauncherAccount("plabcdX26x13x62219"), "an unknown edition letter stays refused");
    }

    [TestMethod]
    public void SystemDetectionFindsTheInstalledMyAdmin()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var live = Path.Combine(dir, "Pilot-myAdmin");
            Directory.CreateDirectory(live);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(live, "Ascon.Pilot.MyAdmin.exe"));
            var registry = new Registry();
            registry.InstallPaths["Pilot-myAdmin"] = live + "\\"; // as the installer writes it, with the trailing slash
            registry.InstallPaths["Pilot-Server"] = live;          // the server is still not a client

            var install = new SystemInstallDetector(registry).Detect().Single();

            Assert.AreEqual(EditionId.MyAdmin, install.Edition.Id);
            Assert.AreEqual(live, install.InstallDir);
            Assert.AreEqual(Path.Combine(live, "Ascon.Pilot.MyAdmin.exe"), install.ExePath);
            Assert.AreEqual(FileVersionInfo.GetVersionInfo(install.ExePath).FileMajorPart, install.Version.Major);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void UninstallLocatorFindsOnlyTheMyAdminRecord()
    {
        var entries = new[]
        {
            new UninstallEntry("Pilot-Server", "26.13.0.62219", null, "MsiExec.exe /X{1A65593F-C67F-4288-A896-5942EF14F7B1}"),
            new UninstallEntry("Pilot-myAdmin", "26.13.0.62219", null, "MsiExec.exe /X{5DEEC076-A055-4B82-8D79-49B9A141A662}"),
            new UninstallEntry("Pilot-ICE Enterprise", "26.13.0.62219", null, "MsiExec.exe /X{1B007BC5-F609-4BC0-98C5-0AB4008511E2}"),
        };
        var cache = @"C:\ProgramData\Package Cache";

        Assert.AreEqual(new MsiUninstall("{5DEEC076-A055-4B82-8D79-49B9A141A662}"),
            ClientUninstallLocator.Find(entries, MyAdmin, new Version(26, 13, 0, 62219), cache).Single());
        Assert.AreEqual(new MsiUninstall("{1B007BC5-F609-4BC0-98C5-0AB4008511E2}"),
            ClientUninstallLocator.Find(entries, Editions.Get(EditionId.IceEnterprise), new Version(26, 13, 0, 62219), cache).Single());
    }

    // ---- release notes: the sample page has myAdmin rows in 26.11, 25.14, 24.40, 24.19, 24.4 and 24.1

    private static IReadOnlyList<ReleaseNotes> Notes => ReleaseNotesParser.Parse(File.ReadAllText(TestSupport.Fixture("release-notes.sample.html")));

    private static EditionDigest Digest(string version) => EditionNotes.Digest(Notes.Single(r => r.Version == Version.Parse(version)), MyAdmin);

    [TestMethod]
    public void NotesTakeTheDesktopRowAndLeaveTheWebOneOut()
    {
        // 26.11 has only "Pilot-myAdmin Web", 26.13 has two myAdmin Web fixes: nothing there is about the console
        Assert.AreEqual(0, Digest("26.11.0.61894").Changes.Count);
        Assert.AreEqual(0, Digest("26.13.0.62219").Fixes.Count);

        var fixes = Digest("25.14.0.57594").Fixes;
        Assert.AreEqual(1, fixes.Count);
        StringAssert.Contains(fixes[0], "при импорте пользователя из LDAP");
    }

    [TestMethod]
    public void NotesTakeThePlainMyAdminRowsOfThe2024Releases()
    {
        var plain = Digest("24.40.0.52162").Fixes;
        Assert.AreEqual(2, plain.Count);
        StringAssert.Contains(plain[1], "base.messages.dbp");

        foreach (var version in new[] { "24.19.0.50468", "24.4.0.49743", "24.1.0.49302" })
            Assert.IsTrue(Digest(version).Changes.Count > 0, $"the 'Pilot-myAdmin' row of {version}");
    }

    [TestMethod]
    public void NotesOfOtherEditionsDoNotGetMyAdminFixes()
    {
        var ice = EditionNotes.Digest(Notes.Single(r => r.Version == new Version(25, 14, 0, 57594)), Editions.Get(EditionId.Ice));

        Assert.IsFalse(ice.Fixes.Any(f => f.Contains("импорте пользователя из LDAP")));
    }
}
