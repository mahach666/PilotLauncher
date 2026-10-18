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

        public Task DeleteAsync(string userName, bool deleteProfile, CancellationToken ct)
        {
            Calls.Add($"delete {userName} {deleteProfile}");
            return Task.CompletedTask;
        }
    }

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
            var ex = await Assert.ThrowsExactlyAsync<IOException>(() => library.DeleteStoredAsync(entry, deleteAccount: true, CancellationToken.None));

            StringAssert.Contains(ex.Message, "Close the Pilot windows");
            Assert.AreEqual(0, provisioner.Calls.Count);
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
