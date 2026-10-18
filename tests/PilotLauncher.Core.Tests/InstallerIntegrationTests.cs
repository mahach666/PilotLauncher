using System.Diagnostics;
using System.IO.Compression;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

/// <summary>
/// Unpack real Pilot setups. They need the setup files and about 1 GB of temporary space, so they skip themselves
/// when the files are not on the machine.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize] // msiexec accepts one operation per machine at a time
public class InstallerIntegrationTests
{
    public static IEnumerable<(string, string, string)> Setups => TestSupport.LocalSetups;

    /// <summary>Size and last write time: enough to see that a file was neither deleted nor rewritten.</summary>
    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}/{info.LastWriteTimeUtc.Ticks}";
    }

    private static VersionInstaller NewInstaller(VersionStore store, HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new FakeFileHandler([]));
        return new VersionInstaller(store, new ReleaseCatalog(http, Path.Combine(store.Root, "cache.html")), new FileDownloader(http), new DiskSpace(), NullLog.Instance);
    }

    [TestMethod]
    [DynamicData(nameof(Setups))]
    public async Task UnpacksARealSetupWithoutInstallingAnything(string setupExe, string editionName, string versionText)
    {
        if (!File.Exists(setupExe))
            Assert.Inconclusive($"{setupExe} is not on this machine.");
        var root = TestSupport.NewTempDir();
        try
        {
            var edition = Editions.Get(Enum.Parse<EditionId>(editionName));
            var version = Version.Parse(versionText);
            var store = new VersionStore(Path.Combine(root, "store"));
            var before = Fingerprint(setupExe);

            await NewInstaller(store).InstallFromSetupAsync(setupExe, edition, version, CancellationToken.None);

            Assert.AreEqual(before, Fingerprint(setupExe), "the user's setup file must stay untouched");

            var stored = store.Find(edition, version) ?? throw new AssertFailedException("version was not stored");
            Assert.IsTrue(File.Exists(stored.ExePath));
            var info = FileVersionInfo.GetVersionInfo(stored.ExePath);
            Assert.AreEqual(version, new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart));
            Assert.IsTrue(Directory.EnumerateFiles(stored.AppDir, "*", SearchOption.AllDirectories).Count() > 500, "the whole client must be unpacked");
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(store.TempDir).Count(), "no intermediate files may stay behind");
            Assert.IsFalse(Directory.Exists(stored.Directory + ".partial"));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    public static IEnumerable<(string, string, string)> Zips =>
        TestSupport.LocalSetups.Select(s => (Path.GetDirectoryName(s.Path)! + ".zip", s.Edition, s.Version));

    [TestMethod]
    [DynamicData(nameof(Zips))]
    public async Task UnpacksARealZipFromDiskAndLeavesTheZipWhereItWas(string zipPath, string editionName, string versionText)
    {
        if (!File.Exists(zipPath))
            Assert.Inconclusive($"{zipPath} is not on this machine.");
        var root = TestSupport.NewTempDir();
        try
        {
            var edition = Editions.Get(Enum.Parse<EditionId>(editionName));
            var version = Version.Parse(versionText);
            var store = new VersionStore(Path.Combine(root, "store"));
            var before = Fingerprint(zipPath);
            var found = SetupFileInspector.Identify(zipPath, store.TempDir) ?? throw new AssertFailedException("zip was not recognised");
            Assert.AreEqual((edition.Id, version), (found.Edition.Id, found.Version));
            var stages = new List<InstallStage>();

            await NewInstaller(store).InstallFromFileAsync(found.Path, found.Edition, found.Version,
                new InlineProgress<InstallProgress>(p => { lock (stages) stages.Add(p.Stage); }), CancellationToken.None);

            var stored = store.Find(edition, version) ?? throw new AssertFailedException("version was not stored");
            Assert.IsTrue(File.Exists(stored.ExePath));
            Assert.AreEqual(VersionSource.LocalFile, stored.Manifest.Source);
            Assert.IsTrue(Directory.EnumerateFiles(stored.AppDir, "*", SearchOption.AllDirectories).Count() > 500, "the whole client must be unpacked");
            Assert.AreEqual(before, Fingerprint(zipPath), "the user's zip must stay untouched");
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(store.TempDir).Count(), "no intermediate files may stay behind");
            CollectionAssert.Contains(stages, InstallStage.ExtractingSetup);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NewInstaller(store).InstallFromFileAsync(zipPath, edition, version, null, CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public async Task DownloadsUnzipsAndUnpacksThroughTheWholePipeline()
    {
        var (setupExe, _, versionText) = TestSupport.LocalSetups[0];
        if (!File.Exists(setupExe))
            Assert.Inconclusive($"{setupExe} is not on this machine.");
        var root = TestSupport.NewTempDir();
        try
        {
            var version = Version.Parse(versionText);
            var zipPath = Path.Combine(root, $"Pilot-ICE-Enterprise-setup_{version}.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(setupExe, "Pilot-ICE-Enterprise-setup.exe", CompressionLevel.NoCompression);
            var zipBytes = await File.ReadAllBytesAsync(zipPath);
            File.Delete(zipPath);

            var store = new VersionStore(Path.Combine(root, "store"));
            var installer = NewInstaller(store, new FakeFileHandler(zipBytes));
            var release = new ReleaseEntry(Editions.Get(EditionId.IceEnterprise), version, Path.GetFileName(zipPath), zipBytes.Length, null);
            var stages = new List<InstallStage>();

            await installer.InstallAsync(release, new InlineProgress<InstallProgress>(p => { lock (stages) stages.Add(p.Stage); }), CancellationToken.None);

            Assert.IsTrue(store.Find(release.Edition, version) is not null);
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(store.TempDir).Count(), "zip and intermediates must be deleted");
            CollectionAssert.Contains(stages, InstallStage.Downloading);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => installer.InstallAsync(release, null, CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public async Task FailedUnpackLeavesNoHalfInstalledVersionAndNoTempFiles()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var notASetup = Path.Combine(root, "setup.exe");
            await File.WriteAllBytesAsync(notASetup, new byte[10_000]);
            var store = new VersionStore(Path.Combine(root, "store"));
            var edition = Editions.Get(EditionId.Bim);
            var version = new Version(26, 1, 0, 1);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NewInstaller(store).InstallFromSetupAsync(notASetup, edition, version, CancellationToken.None));

            Assert.AreEqual(10_000, new FileInfo(notASetup).Length, "a failed install must not delete the user's file either");

            Assert.AreEqual(0, store.Enumerate().Count());
            Assert.IsFalse(Directory.Exists(store.VersionDir(edition, version)));
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(store.TempDir).Count());
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }
}
