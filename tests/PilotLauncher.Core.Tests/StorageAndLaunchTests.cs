using System.Diagnostics;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Launch;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class StorageAndLaunchTests
{
    private sealed class FakeRegistry : IAsconRegistry
    {
        public Dictionary<string, string> InstallPaths { get; } = new();
        public IEnumerable<string> ProductNames() => InstallPaths.Keys;
        public string? GetValue(string productName, string valueName) => valueName == "InstallPath" ? InstallPaths.GetValueOrDefault(productName) : null;
    }

    private sealed class FakeWindows : IWindowApi
    {
        public List<WindowRef> Windows { get; } = [];
        public List<string> Titles { get; } = [];

        public IReadOnlyList<WindowRef> MainWindows(int processId) => Windows.ToList();

        public void SetTitle(IntPtr handle, string title)
        {
            Titles.Add(title);
            Windows.RemoveAll(w => w.Handle == handle);
            Windows.Add(new WindowRef(handle, title));
        }
    }

    private sealed class FakeProvisioner : IAccountProvisioner
    {
        public List<string> Calls { get; } = [];

        public Task EnsureAsync(string userName, string fullName, CancellationToken ct)
        {
            Calls.Add($"ensure {userName}");
            return Task.CompletedTask;
        }

        public Exception? DeleteFails { get; set; }

        public Task DeleteAsync(string userName, bool deleteProfile, CancellationToken ct)
        {
            Calls.Add($"delete {userName} {deleteProfile}");
            return DeleteFails is null ? Task.CompletedTask : Task.FromException(DeleteFails);
        }
    }

    private sealed class FakeAccess : IProfileAccess
    {
        public List<(string Account, string Password, EditionId Edition, string Sid)> Calls { get; } = [];
        public bool Result { get; set; } = true;
        public Exception? Throws { get; set; }

        public Task<bool> GrantOwnerAsync(string account, string password, Edition edition, string ownerSid, CancellationToken ct)
        {
            Calls.Add((account, password, edition.Id, ownerSid));
            return Throws is null ? Task.FromResult(Result) : Task.FromException<bool>(Throws);
        }
    }

    private const string OwnerSid = "S-1-5-21-244962767-1130647507-1218246496-1003";

    private sealed class FakeStarter : IClientProcessStarter
    {
        public List<(string Exe, string Dir, string? User, string? Password)> Started { get; } = [];

        public Process Start(string exePath, string workingDirectory, (string UserName, string Password)? account)
        {
            Started.Add((exePath, workingDirectory, account?.UserName, account?.Password));
            return Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        }
    }

    private static StoredVersion Store(VersionStore store, EditionId id, string version)
    {
        var edition = Editions.Get(id);
        var v = Version.Parse(version);
        var app = store.AppDir(edition, v);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, edition.ExeCandidates[0]), "exe");
        new VersionManifest(edition.ExeCandidates[0], VersionSource.Catalog, DateTime.UtcNow).Save(store.ManifestPath(edition, v));
        return store.Find(edition, v)!;
    }

    [TestMethod]
    public void StoreListsOnlyCompleteVersions()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(dir);
            Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            Store(store, EditionId.Bim, "26.13.0.62219");
            Directory.CreateDirectory(Path.Combine(dir, "Pilot-BIM", "25.1.0.1.partial", "app"));
            Directory.CreateDirectory(Path.Combine(dir, "Pilot-BIM", "25.2.0.2")); // no manifest: interrupted install

            var found = store.Enumerate().Select(v => $"{v.Edition.Id} {v.Version}").OrderBy(s => s).ToList();

            CollectionAssert.AreEqual(new[] { "Bim 26.13.0.62219", "IceEnterprise 26.9.0.61368" }, found);
            Assert.IsNull(store.Find(Editions.Get(EditionId.Ice), new Version(26, 9, 0, 61368)));
            Assert.AreEqual(Path.Combine(dir, "Pilot-ICE-Enterprise", "26.9.0.61368", "app", "Ascon.Pilot.PilotEnterprise.exe"),
                store.Find(Editions.Get(EditionId.IceEnterprise), new Version(26, 9, 0, 61368))!.ExePath);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void PortableFlagMovesDataNextToTheExecutable()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            Assert.AreNotEqual(Path.Combine(dir, "data"), LauncherPaths.Resolve(dir).DataDir);
            File.WriteAllText(Path.Combine(dir, LauncherPaths.PortableFlagFile), "");
            Assert.AreEqual(Path.Combine(dir, "data"), LauncherPaths.Resolve(dir).DataDir);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void SettingsDefaultWhenMissingButDamagedFileIsAnError()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.AreEqual(VersionStore.DefaultRoot, LauncherSettings.Load(path).StoreRoot);

            new LauncherSettings { StoreRoot = @"D:\Pilot" }.Save(path);
            Assert.AreEqual(@"D:\Pilot", LauncherSettings.Load(path).StoreRoot);

            File.WriteAllText(path, "{ broken");
            Assert.ThrowsExactly<System.Text.Json.JsonException>(() => LauncherSettings.Load(path));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void SystemDetectionTrustsTheExecutableNotTheRegistry()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var live = Path.Combine(dir, "ice");
            Directory.CreateDirectory(live);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(live, "Ascon.Pilot.PilotEnterprise.exe"));
            var registry = new FakeRegistry();
            registry.InstallPaths["Pilot-ICE Enterprise"] = live + "\\";
            registry.InstallPaths["Pilot-BIM"] = Path.Combine(dir, "gone"); // stale registry entry, folder removed
            registry.InstallPaths["Pilot-Server"] = live;                   // not a client edition

            var found = new SystemInstallDetector(registry).Detect();

            var install = found.Single();
            Assert.AreEqual(EditionId.IceEnterprise, install.Edition.Id);
            Assert.AreEqual(live, install.InstallDir);
            var info = FileVersionInfo.GetVersionInfo(install.ExePath);
            Assert.AreEqual(new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart), install.Version);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void RealRegistryDetectionDoesNotThrow()
    {
        // whatever is installed on this machine, reading it must work and only return existing executables
        foreach (var install in new SystemInstallDetector(new AsconRegistry()).Detect())
            Assert.IsTrue(File.Exists(install.ExePath));
    }

    [TestMethod]
    public void TitleDecoratorAppendsLabelOnce()
    {
        Assert.AreEqual("Pilot-ICE Enterprise [26.9.0.61368]", TitleDecorator.Decorate("Pilot-ICE Enterprise", "26.9.0.61368"));
        Assert.IsNull(TitleDecorator.Decorate("Pilot-ICE Enterprise [26.9.0.61368]", "26.9.0.61368"));
        Assert.IsNull(TitleDecorator.Decorate("", "x"));
        Assert.IsNull(TitleDecorator.Decorate("   ", "x"));
    }

    [TestMethod]
    public async Task TitleWatcherReappliesLabelWhenTheClientRewritesItsTitle()
    {
        var api = new FakeWindows();
        api.Windows.Add(new WindowRef(1, "Pilot"));
        using var cts = new CancellationTokenSource();
        var task = TitleWatcher.WatchAsync(7, "ICE 26.9", api, () => false, TimeSpan.FromMilliseconds(5), cts.Token);

        await WaitUntil(() => api.Titles.Count >= 1);
        api.Windows.Clear();
        api.Windows.Add(new WindowRef(1, "Pilot - Documents")); // the client changed its title
        await WaitUntil(() => api.Titles.Count >= 2);
        cts.Cancel();
        await task;

        Assert.AreEqual("Pilot [ICE 26.9]", api.Titles[0]);
        Assert.AreEqual("Pilot - Documents [ICE 26.9]", api.Titles[1]);
    }

    [TestMethod]
    public async Task TitleWatcherStopsWhenTheProcessExits()
    {
        var exited = false;
        var task = TitleWatcher.WatchAsync(7, "x", new FakeWindows(), () => exited, TimeSpan.FromMilliseconds(5), CancellationToken.None);
        exited = true;

        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void LaunchingTheSystemInstallRunsAsTheCurrentUser()
    {
        var starter = new FakeStarter();
        var provisioner = new FakeProvisioner();
        var service = NewLaunchService(provisioner, starter, new CredentialStore(Path.Combine(Path.GetTempPath(), "unused-credentials.json"), new DpapiProtector()));
        var system = new SystemInstall(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), @"C:\Program Files\ASCON\Pilot-BIM", @"C:\Program Files\ASCON\Pilot-BIM\Ascon.Pilot.PilotBIM.exe");

        var client = service.LaunchSystem(system);

        Assert.IsFalse(client.Isolated);
        Assert.AreEqual("26.11.0.61894", client.Label);
        Assert.AreEqual(1, starter.Started.Count);
        Assert.IsNull(starter.Started[0].User);
        Assert.AreEqual(0, provisioner.Calls.Count);
        client.Process.WaitForExit();
    }

    private static (LaunchService Service, StoredVersion Stored, FakeAccess Access, FakeStarter Starter, string User) AccessFixture(string dir)
    {
        var store = new VersionStore(Path.Combine(dir, "store"));
        var stored = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
        var credentials = new CredentialStore(Path.Combine(dir, "credentials.json"), new DpapiProtector());
        var starter = new FakeStarter();
        var access = new FakeAccess();
        var service = new LaunchService(new FakeProvisioner(), credentials, starter, new FakeWindows(), "abcd", NullLog.Instance, access, OwnerSid);
        var user = service.AccountFor(stored.Edition, stored.Version);
        credentials.GetOrCreate(user);
        return (service, stored, access, starter, user);
    }

    [TestMethod]
    public async Task TheFirstIsolatedStartGivesTheMainUserAccessToThePilotDataOnceAndLeavesANote()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (service, stored, access, starter, user) = AccessFixture(dir);

            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();
            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();

            var call = access.Calls.Single();
            Assert.AreEqual(user, call.Account);
            Assert.AreEqual(EditionId.IceEnterprise, call.Edition);
            Assert.AreEqual(OwnerSid, call.Sid);
            Assert.AreEqual(2, starter.Started.Count, "the version started both times");
            Assert.AreEqual($"{user} {OwnerSid}", File.ReadAllText(Path.Combine(stored.Directory, "owner-access.txt")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task AFailedGrantNeverStopsTheStartAndIsTriedAgainNextTime()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (service, stored, access, starter, _) = AccessFixture(dir);

            access.Result = false;
            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();
            access.Throws = new System.ComponentModel.Win32Exception(1326, "logon failure");
            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();
            access.Throws = new TimeoutException("too slow");
            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();

            Assert.AreEqual(3, starter.Started.Count, "every start went ahead");
            Assert.AreEqual(3, access.Calls.Count, "and every start tried again, there is no note yet");
            Assert.IsFalse(File.Exists(Path.Combine(stored.Directory, "owner-access.txt")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ANoteMadeForAnotherUserDoesNotCount()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (service, stored, access, _, user) = AccessFixture(dir);
            File.WriteAllText(Path.Combine(stored.Directory, "owner-access.txt"), $"{user} S-1-5-21-1-2-3-9999");

            (await service.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();

            Assert.AreEqual(1, access.Calls.Count);
            Assert.AreEqual($"{user} {OwnerSid}", File.ReadAllText(Path.Combine(stored.Directory, "owner-access.txt")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task NothingIsGrantedWhenNoGrantingIsConfiguredOrForTheSystemInstallation()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (service, stored, access, _, _) = AccessFixture(dir);
            var system = new SystemInstall(stored.Edition, stored.Version, stored.AppDir, stored.ExePath);

            service.LaunchSystem(system).Process.WaitForExit();
            Assert.AreEqual(0, access.Calls.Count, "the system installation runs under the main user: nothing to grant");

            // a launcher built without the granting (as before) just starts the version
            var credentials = new CredentialStore(Path.Combine(dir, "plain.json"), new DpapiProtector());
            var starter = new FakeStarter();
            var plain = NewLaunchService(new FakeProvisioner(), starter, credentials);
            credentials.GetOrCreate(plain.AccountFor(stored.Edition, stored.Version));
            (await plain.LaunchIsolatedAsync(stored, CancellationToken.None)).Process.WaitForExit();
            Assert.AreEqual(1, starter.Started.Count);
            Assert.IsFalse(File.Exists(Path.Combine(stored.Directory, "owner-access.txt")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void TheHelperCommandCreatesTheFolderAndGrantsOnlyTheGivenUserInTheAccountOwnProfile()
    {
        var edition = Editions.Get(EditionId.IceEnterprise);

        var arguments = ProfileAccessGranter.BuildArguments(edition, OwnerSid);

        Assert.AreEqual(
            "/d /s /c \"md \"%APPDATA%\\ASCON\\Pilot-ICE Enterprise\" 2>nul & icacls \"%APPDATA%\\ASCON\\Pilot-ICE Enterprise\" /grant \"*" + OwnerSid + ":(OI)(CI)M\" >nul\"",
            arguments);
        Assert.AreEqual(@"C:\Users\x\AppData\Roaming\ASCON\Pilot-myAdmin", ProfileData.RoamingFolder(@"C:\Users\x", Editions.Get(EditionId.MyAdmin)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("HP")]
    [DataRow("S-1-5-21-1-2-3-1003 & calc")]
    [DataRow("S-1-5-21-1-2-3-1003\" /grant *S-1-1-0:F")]
    public void TheHelperCommandRefusesAnythingThatIsNotASid(string sid) =>
        Assert.ThrowsExactly<ArgumentException>(() => ProfileAccessGranter.BuildArguments(Editions.Get(EditionId.Bim), sid));

    [TestMethod]
    public void TheHelperCommandRefusesAFolderNameWithShellCharacters()
    {
        var odd = Editions.Get(EditionId.Bim) with { RegistryName = "Pilot-BIM & del x" };

        Assert.ThrowsExactly<ArgumentException>(() => ProfileAccessGranter.BuildArguments(odd, OwnerSid));
    }

    [TestMethod]
    public async Task LaunchingAStoredVersionPreparesItsAccountAndUsesItsPassword()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var stored = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var credentials = new CredentialStore(Path.Combine(dir, "credentials.json"), new DpapiProtector());
            var starter = new FakeStarter();
            var provisioner = new FakeProvisioner();
            var service = NewLaunchService(provisioner, starter, credentials);
            var user = service.AccountFor(stored.Edition, stored.Version);
            // the real provisioner generates the password while preparing the account
            credentials.GetOrCreate(user);

            var client = await service.LaunchIsolatedAsync(stored, CancellationToken.None);

            Assert.IsTrue(client.Isolated);
            CollectionAssert.AreEqual(new[] { $"ensure {user}" }, provisioner.Calls);
            Assert.AreEqual(stored.ExePath, starter.Started[0].Exe);
            Assert.AreEqual(stored.AppDir, starter.Started[0].Dir);
            Assert.AreEqual(user, starter.Started[0].User);
            Assert.AreEqual(credentials.Get(user), starter.Started[0].Password);
            client.Process.WaitForExit();
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void LibraryMergesTheThreeSourcesAndKnowsWhatIsReady()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var live = Path.Combine(dir, "ice");
            Directory.CreateDirectory(live);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(live, "Ascon.Pilot.PilotEnterprise.exe"));
            var registry = new FakeRegistry();
            registry.InstallPaths["Pilot-ICE Enterprise"] = live;
            var library = NewLibrary(store, registry, new FakeProvisioner());
            var catalog = ReleaseListingParser.Parse(File.ReadAllText(TestSupport.Fixture("release-listing.sample.html")));

            var entries = library.Build(catalog);

            var stored = entries.Single(e => e.Edition.Id == EditionId.IceEnterprise && e.Version == new Version(26, 9, 0, 61368));
            Assert.IsTrue(stored.IsInStore && stored.IsReady && !stored.CanDownload);
            Assert.IsNotNull(stored.Release);
            var latest = entries.First(e => e.Edition.Id == EditionId.IceEnterprise);
            Assert.AreEqual(new Version(26, 13, 0, 62219), latest.Version);
            Assert.IsTrue(latest.CanDownload && !latest.IsReady);
            Assert.IsTrue(entries.Any(e => e.IsInSystem)); // the copied notepad.exe, whatever version it has
            // newest first inside each edition
            foreach (var group in entries.GroupBy(e => e.Edition.Id))
                CollectionAssert.AreEqual(group.OrderByDescending(e => e.Version).ToList(), group.ToList());
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void SetupFilesFromTheInstallerFolderMakeVersionsAvailableWithoutTheCatalog()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var library = NewLibrary(store, new FakeRegistry(), new FakeProvisioner());
            var enterprise = Editions.Get(EditionId.IceEnterprise);
            var setups = new[]
            {
                new LocalSetup(enterprise, new Version(26, 9, 0, 61368), @"E:\Pilot-ICE-Enterprise-setup_26.9.0.61368.zip", 100),
                new LocalSetup(enterprise, new Version(25, 12, 0, 57162), @"E:\Pilot-ICE-Enterprise-setup_25.12.0.57162.zip", 100),
            };

            var entries = library.Build([], setups);

            var unpacked = entries.Single(e => e.Version == new Version(26, 9, 0, 61368));
            Assert.IsTrue(unpacked.IsReady && !unpacked.CanInstallFromFile, "already unpacked: start it, do not unpack again");
            var offline = entries.Single(e => e.Version == new Version(25, 12, 0, 57162));
            Assert.IsTrue(!offline.IsReady && !offline.CanDownload && offline.CanInstallFromFile);
            Assert.AreEqual(setups[1], offline.Setup);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ARunningRealProgramOfTheCopyStopsTheDeleteWithNothingChangedAndAfterItEndsTheDeleteWorks()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            // a real program started from the folder of the version, as Pilot is
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), a.ExePath, overwrite: true);
            var library = NewLibrary(store, new FakeRegistry(), new FakeProvisioner());
            var entry = library.Build([]).Single();
            using var running = Process.Start(new ProcessStartInfo(a.ExePath, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                await Assert.ThrowsExactlyAsync<VersionInUseException>(() => library.DeleteStoredAsync(entry, deleteAccount: false, CancellationToken.None));

                Assert.IsTrue(File.Exists(Path.Combine(a.Directory, VersionManifest.FileName)), "the manifest is still there");
                Assert.IsNotNull(store.Find(a.Edition, a.Version), "the version is still in the list");
                Assert.AreEqual(0, Directory.Exists(store.TrashDir) ? Directory.GetDirectories(store.TrashDir).Length : 0);
            }
            finally
            {
                running.Kill(entireProcessTree: true);
                running.WaitForExit();
            }

            await library.DeleteStoredAsync(entry, deleteAccount: false, CancellationToken.None);

            Assert.IsFalse(Directory.Exists(a.Directory));
            Assert.IsNull(store.Find(a.Edition, a.Version));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void OnlyAFileThatAnotherProgramHoldsCountsAsInUse()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var sub = Path.Combine(dir, "app", "sub");
            Directory.CreateDirectory(sub);
            var free = Path.Combine(dir, "app", "free.dll");
            var readOnly = Path.Combine(sub, "ro.dll");
            var held = Path.Combine(sub, "held.dll");
            File.WriteAllText(free, "x");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            File.WriteAllText(held, "x");

            Assert.IsNull(VersionStore.FindFileInUse(dir), "files nobody holds, a read-only one included");

            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read)) // somebody reads it, not writes
                Assert.AreEqual(held, VersionStore.FindFileInUse(dir), "a file held in a way that forbids writing");

            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ADeleteLeavesNothingInTheTrashAndTheVersionCanBeInstalledAgain()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var library = NewLibrary(store, new FakeRegistry(), new FakeProvisioner());

            await library.DeleteStoredAsync(library.Build([]).Single(), deleteAccount: false, CancellationToken.None);

            Assert.IsFalse(Directory.Exists(a.Directory));
            Assert.AreEqual(0, Directory.GetDirectories(store.TrashDir).Length);
            store.EnsureAbsent(a.Edition, a.Version); // nothing in the way
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void LeftoversOfInterruptedDeletesAndInstallsAreCleanedButCompleteVersionsStay()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var whole = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var broken = store.VersionDir(Editions.Get(EditionId.IceEnterprise), new Version(26, 13, 0, 62219)); // what a half delete leaves: no manifest
            Directory.CreateDirectory(Path.Combine(broken, "app"));
            File.WriteAllText(Path.Combine(broken, "app", "x.dll"), "x");
            var parked = Path.Combine(store.TrashDir, "abc");
            Directory.CreateDirectory(parked);
            File.WriteAllText(Path.Combine(parked, "y.dll"), "y");
            var oldStaging = store.VersionDir(Editions.Get(EditionId.IceEnterprise), new Version(26, 14, 0, 1)) + ".partial";
            var newStaging = store.VersionDir(Editions.Get(EditionId.IceEnterprise), new Version(26, 15, 0, 1)) + ".partial";
            Directory.CreateDirectory(oldStaging);
            Directory.CreateDirectory(newStaging);
            Directory.SetLastWriteTimeUtc(oldStaging, DateTime.UtcNow.AddDays(-2));

            store.CleanLeftovers(TimeSpan.FromHours(12));

            Assert.IsFalse(Directory.Exists(broken), "the broken half of a delete");
            Assert.IsFalse(Directory.Exists(parked), "the parked version");
            Assert.IsFalse(Directory.Exists(oldStaging), "an old staging folder");
            Assert.IsTrue(Directory.Exists(newStaging), "an install may be running right now");
            Assert.IsNotNull(store.Find(whole.Edition, whole.Version), "the complete version is untouched");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AVersionIsNotInstalledOverACompleteOneButTheBrokenRestOfADeleteIsRemoved()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var whole = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var edition = Editions.Get(EditionId.IceEnterprise);
            var brokenVersion = new Version(26, 13, 0, 62219);
            var broken = store.VersionDir(edition, brokenVersion);
            Directory.CreateDirectory(Path.Combine(broken, "app"));
            File.WriteAllText(Path.Combine(broken, "app", "x.dll"), "x");

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => store.EnsureAbsent(whole.Edition, whole.Version));
            StringAssert.Contains(ex.Message, "already in the store");
            Assert.IsTrue(Directory.Exists(whole.Directory), "a complete version is never removed by this");

            store.EnsureAbsent(edition, brokenVersion);
            Assert.IsFalse(Directory.Exists(broken));
            store.EnsureAbsent(edition, new Version(1, 0, 0, 0)); // nothing there: fine
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task WhenTheProfileCannotBeRemovedTheCopyIsStillDeletedAndTheErrorSaysWhatStayed()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var provisioner = new FakeProvisioner { DeleteFails = new AdminOperationException("Не удалось удалить профиль (код Windows 5).") };
            var library = NewLibrary(store, new FakeRegistry(), provisioner);
            var entry = library.Build([]).Single();

            var ex = await Assert.ThrowsExactlyAsync<ProfileNotRemovedException>(() => library.DeleteStoredAsync(entry, deleteAccount: true, CancellationToken.None));

            Assert.IsFalse(Directory.Exists(a.Directory), "the copy is gone");
            Assert.AreEqual(0, store.Enumerate().Count(), "so it is no longer a stored version");
            StringAssert.StartsWith(ex.Message, "Копия удалена");
            StringAssert.Contains(ex.Message, ex.Account);
            Assert.AreEqual(AccountNaming.UserName("abcd", entry.Edition, entry.Version), ex.Account);
            StringAssert.Contains(ex.Message, "код Windows 5", "the reason of Windows is shown");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ADeclinedWindowsPromptLeavesTheAccountAndSaysSo()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var provisioner = new FakeProvisioner { DeleteFails = new OperationCanceledException("The administrator confirmation was declined.") };
            var library = NewLibrary(store, new FakeRegistry(), provisioner);

            var ex = await Assert.ThrowsExactlyAsync<ProfileNotRemovedException>(
                () => library.DeleteStoredAsync(library.Build([]).Single(), deleteAccount: true, CancellationToken.None));

            StringAssert.Contains(ex.Message, "отклонили запрос Windows");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DeletingAgainAfterAnEarlierAttemptRemovedTheFolderGoesOnToTheAccount()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var provisioner = new FakeProvisioner();
            var library = NewLibrary(store, new FakeRegistry(), provisioner);
            var entry = library.Build([]).Single();
            Directory.Delete(a.Directory, recursive: true); // what the first attempt did before it failed

            await library.DeleteStoredAsync(entry, deleteAccount: true, CancellationToken.None);

            Assert.AreEqual(1, provisioner.Calls.Count, "the account is dealt with, no 'could not find the path' error");
            StringAssert.StartsWith(provisioner.Calls[0], "delete pl");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DeletingRemovesFilesAndOnlyWhenAskedTheAccount()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var b = Store(store, EditionId.IceEnterprise, "26.4.0.60408");
            var provisioner = new FakeProvisioner();
            var library = NewLibrary(store, new FakeRegistry(), provisioner);
            var entries = library.Build([]);

            await library.DeleteStoredAsync(entries.Single(e => e.Version == a.Version), deleteAccount: false, CancellationToken.None);
            Assert.IsFalse(Directory.Exists(a.Directory));
            Assert.AreEqual(0, provisioner.Calls.Count);

            await library.DeleteStoredAsync(entries.Single(e => e.Version == b.Version), deleteAccount: true, CancellationToken.None);
            Assert.IsFalse(Directory.Exists(b.Directory));
            Assert.AreEqual(1, provisioner.Calls.Count);
            StringAssert.StartsWith(provisioner.Calls[0], "delete pl");
            StringAssert.EndsWith(provisioner.Calls[0], "True");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DeletingAVersionThatIsInUseKeepsTheAccountAndSaysWhy()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var a = Store(store, EditionId.IceEnterprise, "26.9.0.61368");
            var provisioner = new FakeProvisioner();
            var library = NewLibrary(store, new FakeRegistry(), provisioner);
            var entry = library.Build([]).Single();

            using var locked = new FileStream(a.ExePath, FileMode.Open, FileAccess.Read, FileShare.None); // like a running exe
            var ex = await Assert.ThrowsExactlyAsync<VersionInUseException>(() => library.DeleteStoredAsync(entry, deleteAccount: true, CancellationToken.None));

            StringAssert.Contains(ex.Message, "нельзя удалить, пока она запущена");
            StringAssert.Contains(ex.Message, "Ничего не удалено");
            Assert.AreEqual(0, provisioner.Calls.Count);
            // nothing was touched: the version is whole and still listed
            Assert.IsTrue(File.Exists(Path.Combine(a.Directory, VersionManifest.FileName)));
            Assert.IsTrue(File.Exists(a.ExePath));
            Assert.IsNotNull(store.Find(a.Edition, a.Version));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task CopyingASystemInstallMakesAnIndependentStoredVersion()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var source = Path.Combine(dir, "system", "Pilot-BIM");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "Ascon.Pilot.PilotBIM.exe"), "exe");
            File.WriteAllText(Path.Combine(source, "sub", "lib.dll"), "dll");
            var system = new SystemInstall(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), source, Path.Combine(source, "Ascon.Pilot.PilotBIM.exe"));
            var store = new VersionStore(Path.Combine(dir, "store"));
            var installer = NewInstaller(store);

            await installer.CopySystemInstallAsync(system, null, CancellationToken.None);

            var copy = store.Find(system.Edition, system.Version)!;
            Assert.AreEqual(VersionSource.SystemCopy, copy.Manifest.Source);
            Assert.AreEqual("exe", File.ReadAllText(copy.ExePath));
            Assert.AreEqual("dll", File.ReadAllText(Path.Combine(copy.AppDir, "sub", "lib.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(source, "sub", "lib.dll")), "the system installation must stay untouched");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => installer.CopySystemInstallAsync(system, null, CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task CopyingASystemInstallChecksFreeSpaceFirst()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var source = Path.Combine(dir, "Pilot-BIM");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Ascon.Pilot.PilotBIM.exe"), "exe");
            var system = new SystemInstall(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), source, Path.Combine(source, "Ascon.Pilot.PilotBIM.exe"));
            var store = new VersionStore(Path.Combine(dir, "store"));
            var installer = new VersionInstaller(store, new ReleaseCatalog(new HttpClient(), "unused"), new FileDownloader(new HttpClient()), new DiskSpace(_ => 1024), NullLog.Instance);

            await Assert.ThrowsExactlyAsync<InsufficientDiskSpaceException>(() => installer.CopySystemInstallAsync(system, null, CancellationToken.None));
            Assert.IsFalse(Directory.Exists(store.VersionDir(system.Edition, system.Version)));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    private static VersionInstaller NewInstaller(VersionStore store) =>
        new(store, new ReleaseCatalog(new HttpClient(), "unused"), new FileDownloader(new HttpClient()), new DiskSpace(_ => long.MaxValue), NullLog.Instance);

    private static VersionLibrary NewLibrary(VersionStore store, IAsconRegistry registry, IAccountProvisioner provisioner) =>
        new(store, new SystemInstallDetector(registry), NewInstaller(store), provisioner,
            (e, v) => AccountNaming.UserName("abcd", e, v), NullLog.Instance);

    private static LaunchService NewLaunchService(IAccountProvisioner provisioner, IClientProcessStarter starter, CredentialStore credentials) =>
        new(provisioner, credentials, starter, new FakeWindows(), "abcd", NullLog.Instance);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not reached in time.");
            await Task.Delay(5);
        }
    }
}
