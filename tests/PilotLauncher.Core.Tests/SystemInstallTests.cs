using System.IO.Compression;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class SystemInstallTests
{
    private const string CacheRoot = @"C:\ProgramData\Package Cache";
    private const string IceMsiCode = "{0FCF4BDD-D516-48CE-AAD8-57C1C4A40979}";
    private const string IceBundleCode = "{847dcf46-20ae-4221-bba8-15c9f8c2d126}";

    private static readonly Edition Enterprise = Editions.Get(EditionId.IceEnterprise);

    private static UninstallEntry Msi(string name, string version, string code) =>
        new(name, version, null, $"MsiExec.exe /X{code}");

    private static UninstallEntry Bundle(string name, string version, string code, string setupName) =>
        new(name, version, version, $"\"{CacheRoot}\\{code}\\{setupName}\"  /uninstall");

    /// <summary>Registry records of a PC with a stale 25.9 bundle beside the real 26.11 MSI, and other Pilot products.</summary>
    private static List<UninstallEntry> RealisticRegistry() =>
    [
        Msi("Pilot-Server", "26.11.0.61894", "{05CC7D28-7AB9-412E-BE02-732F66A31E59}"),
        Msi("Pilot-ICE Enterprise", "26.11.0.61894", IceMsiCode),
        Msi("Pilot-myAdmin", "26.11.0.61894", "{84B675E1-B90A-43E2-B861-3F7C6C91FA67}"),
        Msi("Pilot-BIM", "26.11.0.61894", "{8FB5DE34-5749-4BA8-813E-CD4B9E0877A0}"),
        Msi("Pilot-BIM-Server", "26.11.0.20591", "{78E566B1-2180-4365-B9C8-A3357FAC50B3}"),
        Bundle("Pilot-ICE Enterprise", "25.9.0.55929", IceBundleCode, "Pilot-ICE-Enterprise-setup.exe"),
        Bundle("Pilot-Server", "25.9.0.55929", "{940f73a8-07cb-473e-b911-48f78b918a9b}", "Pilot-Server-setup.exe"),
        new("Pilot-ICE File System Support Package", "pfm.1.0.0.192", null, @"C:\WINDOWS\PismoFileMount\pfm.exe uninstall -p pfm-license-ascon.txt"),
    ];

    [TestMethod]
    [DataRow(null, "26.11.0.61894", SystemClientAction.Install)]
    [DataRow("25.9.0.55929", "26.11.0.61894", SystemClientAction.Upgrade)]
    [DataRow("26.9.0.61368", "26.9.0.61369", SystemClientAction.Upgrade)]
    [DataRow("26.11.0.61894", "26.9.0.61368", SystemClientAction.UninstallThenInstall)]
    [DataRow("26.11.0.61894", "25.12.0.57162", SystemClientAction.UninstallThenInstall)]
    [DataRow("26.9.0.61369", "26.9.0.61368", SystemClientAction.UninstallThenInstall)]
    [DataRow("26.11.0.61894", "26.11.0.61894", SystemClientAction.AlreadyInstalled)]
    public void PlanUpgradesNewerAndRemovesFirstWhenTargetIsOlder(string? installed, string target, SystemClientAction expected) =>
        Assert.AreEqual(expected, SystemClientPlan.Choose(installed is null ? null : Version.Parse(installed), Version.Parse(target)));

    [TestMethod]
    public void LocatorTakesTheMsiRecordOfTheRealVersionNotTheStaleBundle()
    {
        var found = ClientUninstallLocator.Find(RealisticRegistry(), Enterprise, new Version(26, 11, 0, 61894), CacheRoot);

        Assert.AreEqual(new MsiUninstall(IceMsiCode), found.Single());
    }

    [TestMethod]
    public void LocatorTakesTheBundleRecordOfAnOlderInstall()
    {
        var found = ClientUninstallLocator.Find(RealisticRegistry(), Enterprise, new Version(25, 9, 0, 55929), CacheRoot);

        Assert.AreEqual(new BundleUninstall($@"{CacheRoot}\{IceBundleCode}\Pilot-ICE-Enterprise-setup.exe"), found.Single());
    }

    [TestMethod]
    public void LocatorFindsNothingForAVersionThatHasNoRecord() =>
        Assert.AreEqual(0, ClientUninstallLocator.Find(RealisticRegistry(), Enterprise, new Version(26, 9, 0, 61368), CacheRoot).Count);

    [TestMethod]
    public void LocatorTouchesOnlyTheClientOfTheEdition()
    {
        var bim = ClientUninstallLocator.Find(RealisticRegistry(), Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), CacheRoot);
        Assert.AreEqual(new MsiUninstall("{8FB5DE34-5749-4BA8-813E-CD4B9E0877A0}"), bim.Single());

        // "Pilot-ICE" must not pick up "Pilot-ICE Enterprise", and the server/myAdmin records are never candidates
        Assert.AreEqual(0, ClientUninstallLocator.Find(RealisticRegistry(), Editions.Get(EditionId.Ice), new Version(26, 11, 0, 61894), CacheRoot).Count);
        Assert.AreEqual(0, ClientUninstallLocator.Find(RealisticRegistry(), Editions.Get(EditionId.Ecm), new Version(26, 11, 0, 61894), CacheRoot).Count);
    }

    [TestMethod]
    public void LocatorPrefersTheBundleWhenBothRecordsExist()
    {
        var entries = new List<UninstallEntry>
        {
            Msi("Pilot-ICE Enterprise", "26.9.0.61368", IceMsiCode),
            Bundle("Pilot-ICE Enterprise", "26.9.0.61368", IceBundleCode, "Pilot-ICE-Enterprise-setup.exe"),
        };

        var found = ClientUninstallLocator.Find(entries, Enterprise, new Version(26, 9, 0, 61368), CacheRoot);

        Assert.AreEqual(2, found.Count);
        Assert.IsInstanceOfType<BundleUninstall>(found[0]);
        Assert.IsInstanceOfType<MsiUninstall>(found[1]);
    }

    [TestMethod]
    [DataRow("cmd.exe /c del /q C:\\x")]
    [DataRow("MsiExec.exe /X{0FCF4BDD-D516-48CE-AAD8-57C1C4A40979} & calc")]
    [DataRow("MsiExec.exe /X{0FCF4BDD-D516-48CE-AAD8-57C1C4A40979} /qn")]
    [DataRow("MsiExec.exe /X{not-a-guid}")]
    [DataRow("\"C:\\Users\\Public\\Pilot-ICE-Enterprise-setup.exe\" /uninstall")]
    [DataRow("\"C:\\ProgramData\\Package Cache\\{847dcf46-20ae-4221-bba8-15c9f8c2d126}\\other.exe\" /uninstall")]
    [DataRow("\"C:\\ProgramData\\Package Cache\\{847dcf46-20ae-4221-bba8-15c9f8c2d126}\\..\\Pilot-ICE-Enterprise-setup.exe\" /uninstall")]
    [DataRow("\"C:\\ProgramData\\Package Cache\\x\\y\\Pilot-ICE-Enterprise-setup.exe\" /uninstall")]
    [DataRow("\"C:\\ProgramData\\Package Cache\\{847dcf46-20ae-4221-bba8-15c9f8c2d126}\\Pilot-ICE-Enterprise-setup.exe\" /uninstall /extra")]
    public void LocatorRejectsUninstallStringsOfUnknownShape(string uninstallString)
    {
        var entries = new[] { new UninstallEntry("Pilot-ICE Enterprise", "26.11.0.61894", "26.11.0.61894", uninstallString) };

        Assert.AreEqual(0, ClientUninstallLocator.Find(entries, Enterprise, new Version(26, 11, 0, 61894), CacheRoot).Count);
    }

    // ---- elevated side ----

    private sealed class FakeInstalls : ISystemInstalls
    {
        public List<SystemInstall> Items { get; } = [];
        public IReadOnlyList<SystemInstall> Detect() => Items.ToList();

        public void Set(Edition edition, string version)
        {
            Items.RemoveAll(i => i.Edition.Id == edition.Id);
            Items.Add(new SystemInstall(edition, Version.Parse(version), @"C:\Pilot", @"C:\Pilot\pilot.exe"));
        }
    }

    private sealed class FakeUninstallRegistry : IUninstallRegistry
    {
        public List<UninstallEntry> Items { get; } = RealisticRegistry();
        public IEnumerable<UninstallEntry> Entries() => Items;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, string Arguments)> Calls { get; } = [];
        public int ExitCode { get; set; }
        public Func<string, int>? ExitCodeFor { get; set; }
        public Action<string>? OnRun { get; set; }

        public int Run(string fileName, string arguments)
        {
            Calls.Add((fileName, arguments));
            OnRun?.Invoke(fileName);
            return ExitCodeFor?.Invoke(fileName) ?? ExitCode;
        }
    }

    private sealed class AdminFixture : IDisposable
    {
        public string Dir { get; } = TestSupport.NewTempDir();
        public FakeInstalls Installs { get; } = new();
        public FakeUninstallRegistry Registry { get; } = new();
        public FakeRunner Runner { get; } = new();
        public string SetupRoot => Path.Combine(Dir, "system-setup");
        public string PackageCache => Path.Combine(Dir, "Package Cache");
        public string Logs => Path.Combine(Dir, "logs");
        public SystemClientAdmin Admin { get; }

        public AdminFixture() =>
            Admin = new SystemClientAdmin(Installs, Registry, Runner, SetupRoot, Logs, PackageCache);

        public string StageSetup(Edition edition)
        {
            var folder = Path.Combine(SetupRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, edition.SetupPrefix + ".exe");
            File.WriteAllText(path, "setup");
            return path;
        }

        public void Dispose() => TestSupport.DeleteQuietly(Dir);
    }

    [TestMethod]
    public void AdminUpgradeRunsOnlyTheSetupQuietly()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "25.12.0.57162");
        var setup = fx.StageSetup(Enterprise);

        fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), setup);

        var call = fx.Runner.Calls.Single();
        Assert.AreEqual(setup, call.File);
        Assert.AreEqual($"/quiet /norestart /log \"{Path.Combine(fx.Logs, "system-install-IceEnterprise-26.9.0.61368.log")}\"", call.Arguments);
    }

    [TestMethod]
    public void AdminFreshInstallRunsOnlyTheSetup()
    {
        using var fx = new AdminFixture();

        fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), fx.StageSetup(Enterprise));

        Assert.AreEqual(1, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminDowngradeRemovesTheInstalledMsiClientBeforeTheSetup()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "26.11.0.61894");
        var setup = fx.StageSetup(Enterprise);

        fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), setup);

        Assert.AreEqual(2, fx.Runner.Calls.Count);
        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), fx.Runner.Calls[0].File);
        StringAssert.StartsWith(fx.Runner.Calls[0].Arguments, $"/x {IceMsiCode} /qn /norestart /l*v ");
        Assert.AreEqual(setup, fx.Runner.Calls[1].File);
    }

    [TestMethod]
    public void AdminDowngradeUsesTheBundleOfAnOlderInstallWhenItsFileExists()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "25.9.0.55929");
        var bundleDir = Path.Combine(fx.PackageCache, IceBundleCode);
        Directory.CreateDirectory(bundleDir);
        var bundle = Path.Combine(bundleDir, "Pilot-ICE-Enterprise-setup.exe");
        File.WriteAllText(bundle, "bundle");
        fx.Registry.Items.Clear();
        fx.Registry.Items.Add(new UninstallEntry("Pilot-ICE Enterprise", "25.9.0.55929", "25.9.0.55929", $"\"{bundle}\"  /uninstall"));

        fx.Admin.Replace(Enterprise, new Version(25, 3, 0, 50000), fx.StageSetup(Enterprise));

        Assert.AreEqual(bundle, fx.Runner.Calls[0].File);
        StringAssert.StartsWith(fx.Runner.Calls[0].Arguments, "/uninstall /quiet /norestart /log ");
        Assert.AreEqual(2, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminDowngradeWithoutAnUninstallRecordStopsBeforeRunningAnything()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "26.9.0.61368");
        fx.Registry.Items.Clear();

        var ex = Assert.ThrowsExactly<AdminOperationException>(
            () => fx.Admin.Replace(Enterprise, new Version(25, 12, 0, 57162), fx.StageSetup(Enterprise)));

        StringAssert.Contains(ex.Message, "26.9.0.61368");
        Assert.AreEqual(0, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminDoesNotInstallTheSetupWhenTheUninstallFailed()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "26.11.0.61894");
        fx.Runner.ExitCode = 1603;

        var ex = Assert.ThrowsExactly<AdminOperationException>(
            () => fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), fx.StageSetup(Enterprise)));

        StringAssert.Contains(ex.Message, "1603");
        StringAssert.Contains(ex.Message, "system-uninstall-IceEnterprise-26.11.0.61894.log");
        Assert.AreEqual(1, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminReportsTheLogWhenTheSetupFailsAndAcceptsRestartRequired()
    {
        using var fx = new AdminFixture();
        fx.Runner.ExitCode = 1603;
        var ex = Assert.ThrowsExactly<AdminOperationException>(
            () => fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), fx.StageSetup(Enterprise)));
        StringAssert.Contains(ex.Message, Path.Combine(fx.Logs, "system-install-IceEnterprise-26.9.0.61368.log"));

        fx.Runner.ExitCode = 3010;
        fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), fx.StageSetup(Enterprise));
    }

    [TestMethod]
    public void AdminRefusesToReplaceTheSameVersion()
    {
        using var fx = new AdminFixture();
        fx.Installs.Set(Enterprise, "26.9.0.61368");

        Assert.ThrowsExactly<AdminOperationException>(
            () => fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), fx.StageSetup(Enterprise)));
        Assert.AreEqual(0, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminSetupGuardAcceptsOnlyTheStagedFileOfTheEdition()
    {
        using var fx = new AdminFixture();
        var folder = new string('a', 32);
        var good = Path.Combine(fx.SetupRoot, folder, "Pilot-ICE-Enterprise-setup.exe");

        Assert.IsTrue(fx.Admin.IsAllowedSetup(good, Enterprise));
        Assert.IsTrue(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, folder, "PILOT-ICE-ENTERPRISE-SETUP.EXE"), Enterprise));

        Assert.IsFalse(fx.Admin.IsAllowedSetup(good, Editions.Get(EditionId.Bim)), "setup of another edition");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, folder, "evil.exe"), Enterprise));
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "no staging folder");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, "name", "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "folder is not a GUID");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, folder, "x", "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "too deep");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.Dir, folder, "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "outside the root");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot + "-other", folder, "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "sibling with the same prefix");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(Path.Combine(fx.SetupRoot, folder, "..", folder, "Pilot-ICE-Enterprise-setup.exe"), Enterprise), "dot-dot segment");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(@"system-setup\" + folder + @"\Pilot-ICE-Enterprise-setup.exe", Enterprise), "relative path");
        Assert.IsFalse(fx.Admin.IsAllowedSetup(@"C:\Windows\System32\cmd.exe", Enterprise));
    }

    [TestMethod]
    public void AdminRefusesASetupOutsideTheStagingFolderAndRunsNothing()
    {
        using var fx = new AdminFixture();
        var outside = Path.Combine(fx.Dir, "Pilot-ICE-Enterprise-setup.exe");
        File.WriteAllText(outside, "x");

        Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Replace(Enterprise, new Version(26, 9, 0, 61368), outside));
        Assert.AreEqual(0, fx.Runner.Calls.Count);
    }

    [TestMethod]
    public void AdminCommandReplaceClientValidatesEditionVersionAndPath()
    {
        using var fx = new AdminFixture();
        var accounts = new NoAccounts();
        var setup = fx.StageSetup(Enterprise);

        Assert.AreEqual(1, RunAdmin(fx, accounts, "IceEnterprise", "26.9.0.61368", Path.Combine(fx.Dir, "x.exe"), out var message));
        StringAssert.Contains(message, "не лежит");
        Assert.AreEqual(1, RunAdmin(fx, accounts, "Server", "26.9.0.61368", setup, out message));
        StringAssert.Contains(message, "редакция");
        Assert.AreEqual(1, RunAdmin(fx, accounts, "IceEnterprise", "26.9", setup, out message));
        StringAssert.Contains(message, "версия");
        Assert.AreEqual(1, RunAdmin(fx, accounts, "7", "26.9.0.61368", setup, out _), "numeric edition ids are not accepted");
        Assert.AreEqual(0, fx.Runner.Calls.Count);

        Assert.AreEqual(0, RunAdmin(fx, accounts, "IceEnterprise", "26.9.0.61368", setup, out _));
        Assert.AreEqual(1, fx.Runner.Calls.Count);
        Assert.AreEqual(0, accounts.Calls);
    }

    [TestMethod]
    public void AdminCommandReplaceClientIsRefusedWithoutASystemClientHandler()
    {
        var result = Path.Combine(TestSupport.NewTempDir(), "r.txt");
        try
        {
            var code = AdminCommands.Run(["replace-client", "--edition", "Ice", "--version", "1.2.3.4", "--setup", "x", "--result", result], new NoAccounts());

            Assert.AreEqual(1, code);
            Assert.IsTrue(File.Exists(result));
        }
        finally
        {
            TestSupport.DeleteQuietly(Path.GetDirectoryName(result)!);
        }
    }

    private static int RunAdmin(AdminFixture fx, NoAccounts accounts, string edition, string version, string setup, out string message)
    {
        var result = Path.Combine(fx.Dir, "result.txt");
        File.Delete(result);
        var code = AdminCommands.Run(
            ["replace-client", "--edition", edition, "--version", version, "--setup", setup, "--result", result], accounts, fx.Admin);
        message = File.Exists(result) ? File.ReadAllText(result) : "";
        return code;
    }

    private sealed class NoAccounts : ILocalAccounts
    {
        public int Calls { get; private set; }
        public bool Exists(string userName) { Calls++; return false; }
        public bool PasswordWorks(string userName, string password) { Calls++; return false; }
        public void Ensure(string userName, string password, string fullName, string description) => Calls++;
        public void Delete(string userName, bool deleteProfile) => Calls++;
    }

    // ---- user side ----

    private sealed class FakeProcesses : IClientProcesses
    {
        public bool Running { get; set; }
        public bool IsRunning(Edition edition) => Running;
    }

    private sealed class UserFixture : IDisposable
    {
        public string Dir { get; } = TestSupport.NewTempDir();
        public FakeInstalls Installs { get; } = new();
        public FakeProcesses Processes { get; } = new();
        public List<IReadOnlyList<string>> ElevatedCalls { get; } = [];
        public List<string> SetupContentSeenByAdmin { get; } = [];
        public string? InstalledByAdmin { get; set; }
        public FakeFileHandler Handler { get; } = new(ZipWithSetup());
        public SystemInstaller Installer { get; }
        public string SetupRoot => Path.Combine(Dir, "system-setup");

        public UserFixture()
        {
            Installer = new SystemInstaller(
                Installs, Processes, new ReleaseCatalog(new HttpClient(Handler), "unused"),
                new FileDownloader(new HttpClient(Handler)), new DiskSpace(_ => long.MaxValue),
                (arguments, _) =>
                {
                    ElevatedCalls.Add(arguments);
                    var setup = arguments[arguments.ToList().IndexOf("--setup") + 1];
                    SetupContentSeenByAdmin.Add(File.ReadAllText(setup));
                    if (InstalledByAdmin is not null)
                        Installs.Set(Enterprise, InstalledByAdmin);
                    return Task.CompletedTask;
                },
                SetupRoot, NullLog.Instance);
        }

        public void Dispose() => TestSupport.DeleteQuietly(Dir);
    }

    private static byte[] ZipWithSetup()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Pilot-ICE-Enterprise-setup.exe").Open());
            writer.Write("zipped setup");
        }

        return stream.ToArray();
    }

    private static ReleaseEntry Release(string version) =>
        new(Enterprise, Version.Parse(version), $"Pilot-ICE-Enterprise-setup_{version}.zip", 1000, null);

    [TestMethod]
    public async Task UserSideRefusesWhileTheClientRunsAndDoesNothing()
    {
        using var fx = new UserFixture();
        fx.Processes.Running = true;

        var ex = await Assert.ThrowsExactlyAsync<ClientRunningException>(
            () => fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.Download(Release("26.9.0.61368")), null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "ICE Enterprise");
        Assert.AreEqual(0, fx.ElevatedCalls.Count);
        Assert.AreEqual(0, fx.Handler.RangeRequests.Count, "nothing was downloaded");
        Assert.IsFalse(Directory.Exists(fx.SetupRoot), "nothing was staged");
        Assert.ThrowsExactly<ClientRunningException>(() => fx.Installer.EnsureClosed(Enterprise));
    }

    [TestMethod]
    public async Task UserSideDownloadsStagesRunsElevatedAndCleansUp()
    {
        using var fx = new UserFixture();
        fx.Installs.Set(Enterprise, "25.12.0.57162");
        fx.InstalledByAdmin = "26.9.0.61368";
        var seen = new List<InstallStage>();

        await fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.Download(Release("26.9.0.61368")),
            new SyncProgress(p => seen.Add(p.Stage)), CancellationToken.None);

        var call = fx.ElevatedCalls.Single();
        CollectionAssert.AreEqual(new[] { "replace-client", "--edition", "IceEnterprise", "--version", "26.9.0.61368" }, call.Take(5).ToArray());
        Assert.AreEqual("--setup", call[5]);
        StringAssert.StartsWith(call[6], fx.SetupRoot + Path.DirectorySeparatorChar);
        Assert.AreEqual("zipped setup", fx.SetupContentSeenByAdmin.Single());
        Assert.IsFalse(File.Exists(call[6]), "staged setup is deleted");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count(), "staging folder is deleted");
        CollectionAssert.IsSubsetOf(new[] { InstallStage.Downloading, InstallStage.ExtractingSetup, InstallStage.InstallingSystem }, seen);
        // the setup is accepted by the elevated side's own guard
        var admin = new SystemClientAdmin(fx.Installs, new FakeUninstallRegistry(), new FakeRunner(), fx.SetupRoot, fx.Dir);
        Assert.IsTrue(admin.IsAllowedSetup(call[6], Enterprise));
    }

    [TestMethod]
    public async Task UserSideInstallsFromAnOfflineFileWithoutTouchingIt()
    {
        using var fx = new UserFixture();
        fx.InstalledByAdmin = "26.9.0.61368";
        var offline = Path.Combine(fx.Dir, "my-setup.exe");
        File.WriteAllText(offline, "offline exe");

        await fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.LocalFile(offline), null, CancellationToken.None);

        Assert.AreEqual("offline exe", fx.SetupContentSeenByAdmin.Single());
        Assert.AreEqual("offline exe", File.ReadAllText(offline), "the user's file is untouched");
        Assert.AreEqual(0, fx.Handler.RangeRequests.Count, "nothing was downloaded");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count());
    }

    [TestMethod]
    public async Task UserSideUnpacksAnOfflineZipAndKeepsIt()
    {
        using var fx = new UserFixture();
        fx.InstalledByAdmin = "26.9.0.61368";
        var offline = Path.Combine(fx.Dir, "Pilot-ICE-Enterprise-setup_26.9.0.61368.zip");
        File.WriteAllBytes(offline, ZipWithSetup());

        await fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.LocalFile(offline), null, CancellationToken.None);

        Assert.AreEqual("zipped setup", fx.SetupContentSeenByAdmin.Single());
        Assert.IsTrue(File.Exists(offline), "the user's file is untouched");
    }

    [TestMethod]
    public async Task UserSideRejectsAnUnknownOfflineFileTypeAndCleansUp()
    {
        using var fx = new UserFixture();
        var offline = Path.Combine(fx.Dir, "setup.7z");
        File.WriteAllText(offline, "x");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.LocalFile(offline), null, CancellationToken.None));

        Assert.AreEqual(0, fx.ElevatedCalls.Count);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count());
    }

    [TestMethod]
    public async Task UserSideFailsWhenTheVersionIsNotThereAfterTheInstaller()
    {
        using var fx = new UserFixture();
        fx.Installs.Set(Enterprise, "25.12.0.57162");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.Download(Release("26.9.0.61368")), null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "25.12.0.57162");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count());
    }

    [TestMethod]
    public async Task UserSideDoesNotSwallowAnElevatedFailureAndStillCleansUp()
    {
        using var fx = new UserFixture();
        var installer = new SystemInstaller(fx.Installs, fx.Processes, new ReleaseCatalog(new HttpClient(), "unused"),
            new FileDownloader(new HttpClient()), new DiskSpace(_ => long.MaxValue),
            (_, _) => throw new AdminOperationException("Установщик завершился с кодом 1603."), fx.SetupRoot, NullLog.Instance);
        var offline = Path.Combine(fx.Dir, "s.exe");
        File.WriteAllText(offline, "x");

        var ex = await Assert.ThrowsExactlyAsync<AdminOperationException>(
            () => installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.LocalFile(offline), null, CancellationToken.None));

        StringAssert.Contains(ex.Message, "1603");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count());
    }

    [TestMethod]
    public async Task UserSideCancelledBeforeTheInstallerDoesNotElevate()
    {
        using var fx = new UserFixture();
        var offline = Path.Combine(fx.Dir, "s.exe");
        File.WriteAllText(offline, "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.LocalFile(offline), null, cts.Token));

        Assert.AreEqual(0, fx.ElevatedCalls.Count);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(fx.SetupRoot).Count());
    }

    [TestMethod]
    public async Task UserSideRefusesTheVersionThatIsAlreadyInstalled()
    {
        using var fx = new UserFixture();
        fx.Installs.Set(Enterprise, "26.9.0.61368");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fx.Installer.ReplaceAsync(Enterprise, new Version(26, 9, 0, 61368), SetupSource.Download(Release("26.9.0.61368")), null, CancellationToken.None));

        Assert.AreEqual(0, fx.ElevatedCalls.Count);
        Assert.AreEqual(SystemClientAction.AlreadyInstalled, fx.Installer.Plan(Enterprise, new Version(26, 9, 0, 61368)).Action);
        Assert.AreEqual(new Version(26, 9, 0, 61368), fx.Installer.Plan(Enterprise, new Version(26, 9, 0, 61368)).Installed);
    }

    [TestMethod]
    public void RealProcessProbeAndRegistryReadingDoNotThrow()
    {
        foreach (var edition in Editions.All)
            _ = new ClientProcesses().IsRunning(edition);
        _ = new UninstallRegistry().Entries().ToList();
    }

    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
