using System.IO.Compression;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

/// <summary>Setup files from disk: recognising them, the installer folder, zips saved for carrying to a PC without internet.</summary>
[TestClass]
public class OfflineSetupTests
{
    private const string RealZip = @"C:\Users\HP\Downloads\Pilot-ICE-Enterprise-setup_26.9.0.61368.zip";
    private const string RealExe = @"C:\Users\HP\Downloads\Pilot-ICE-Enterprise-setup_26.9.0.61368\Pilot-ICE-Enterprise-setup.exe";

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
            Assert.Inconclusive($"{path} is not on this machine.");
    }

    [TestMethod]
    public void ParsesSetupNamesOfBothKindsAndTheCatalogParserStaysZipOnly()
    {
        Assert.IsTrue(Editions.TryParseSetupName("Pilot-BIM-setup_26.11.0.61894.exe", out var edition, out var version, out var extension));
        Assert.AreEqual(EditionId.Bim, edition.Id);
        Assert.AreEqual(new Version(26, 11, 0, 61894), version);
        Assert.AreEqual(".exe", extension);
        Assert.IsTrue(Editions.TryParseSetupName("Pilot-ICE-Enterprise-setup_25.12.0.57162.zip", out edition, out _, out extension));
        Assert.AreEqual(EditionId.IceEnterprise, edition.Id);
        Assert.AreEqual(".zip", extension);

        Assert.IsFalse(Editions.TryParseSetupFile("Pilot-BIM-setup_26.11.0.61894.exe", out _, out _));
        Assert.IsFalse(Editions.TryParseSetupName("Pilot-BIM-setup.exe", out _, out _, out _));
        Assert.IsFalse(Editions.TryParseSetupName("Pilot-BIM-setup_26.11.0.61894.zip.part", out _, out _, out _));
        Assert.IsFalse(Editions.TryParseSetupName("Pilot-Server-setup_26.11.0.61894.exe", out _, out _, out _));
    }

    [TestMethod]
    public void NameDecidesWithoutOpeningTheFile()
    {
        var found = SetupFileInspector.Identify(@"X:\nowhere\Pilot-ECM-setup_25.5.0.50000.zip", Path.GetTempPath());

        Assert.IsNotNull(found);
        Assert.AreEqual(EditionId.Ecm, found.Edition.Id);
        Assert.AreEqual(new Version(25, 5, 0, 50000), found.Version);
    }

    [TestMethod]
    public void ExeWithoutVersionInNameIsRecognisedByItsVersionInfo()
    {
        RequireFile(RealExe);

        var found = SetupFileInspector.Identify(RealExe, Path.GetTempPath());

        Assert.IsNotNull(found);
        Assert.AreEqual(EditionId.IceEnterprise, found.Edition.Id);
        Assert.AreEqual(new Version(26, 9, 0, 61368), found.Version);
        Assert.AreEqual(RealExe, found.Path);
    }

    [TestMethod]
    public void ZipWithAnUnhelpfulNameIsRecognisedByTheExeInside()
    {
        RequireFile(RealZip);
        var root = TestSupport.NewTempDir();
        try
        {
            var renamed = Path.Combine(root, "Pilot (1).zip");
            File.Copy(RealZip, renamed);
            var tempDir = Path.Combine(root, "tmp");

            var found = SetupFileInspector.Identify(renamed, tempDir);

            Assert.IsNotNull(found);
            Assert.AreEqual(EditionId.IceEnterprise, found.Edition.Id);
            Assert.AreEqual(new Version(26, 9, 0, 61368), found.Version);
            Assert.AreEqual(renamed, found.Path, "the result points at the zip the user gave, not at the extracted copy");
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(tempDir).Count(), "the extracted exe must be removed");
            Assert.IsTrue(File.Exists(renamed));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public void UnrecognisableFilesGiveNullSoTheUserCanBeAsked()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var notPilot = Path.Combine(root, "setup.exe");
            File.WriteAllBytes(notPilot, new byte[1000]); // no version resource at all
            var otherProduct = Path.Combine(Environment.SystemDirectory, "notepad.exe"); // has a version, but not a Pilot one
            var zipWithOtherExe = Path.Combine(root, "tool.zip");
            using (var zip = ZipFile.Open(zipWithOtherExe, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(otherProduct, "tool.exe");

            Assert.IsNull(SetupFileInspector.Identify(notPilot, root));
            Assert.IsNull(SetupFileInspector.Identify(otherProduct, root));
            Assert.IsNull(SetupFileInspector.Identify(zipWithOtherExe, Path.Combine(root, "tmp")));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public void ZipWithoutAnExeAndNotAZipAreErrorsNotSilentNulls()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var noExe = Path.Combine(root, "readme.zip");
            using (var zip = ZipFile.Open(noExe, ZipArchiveMode.Create))
                zip.CreateEntry("readme.txt");
            var broken = Path.Combine(root, "broken.zip");
            File.WriteAllText(broken, "not a zip");

            Assert.ThrowsExactly<InvalidDataException>(() => SetupFileInspector.Identify(noExe, Path.Combine(root, "tmp")));
            Assert.ThrowsExactly<InvalidDataException>(() => SetupFileInspector.Identify(broken, Path.Combine(root, "tmp")));
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(Path.Combine(root, "tmp")).Count());
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public void ScannerFindsSetupFilesOnlyAndPrefersTheExe()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Pilot-ICE-Enterprise-setup_26.9.0.61368.zip"), new byte[10]);
            File.WriteAllBytes(Path.Combine(root, "Pilot-ICE-Enterprise-setup_26.9.0.61368.exe"), new byte[20]);
            File.WriteAllBytes(Path.Combine(root, "Pilot-ICE-Enterprise-setup_25.12.0.57162.zip"), new byte[30]);
            File.WriteAllBytes(Path.Combine(root, "Pilot-BIM-setup_26.11.0.61894.zip"), new byte[40]);
            File.WriteAllBytes(Path.Combine(root, "Pilot-BIM-setup_26.12.0.1.zip.part"), new byte[5]);
            File.WriteAllBytes(Path.Combine(root, "notes.txt"), new byte[5]);
            File.WriteAllBytes(Path.Combine(root, "Pilot-ICE-Enterprise-setup.exe"), new byte[5]); // no version in the name
            var nested = Directory.CreateDirectory(Path.Combine(root, "old"));
            File.WriteAllBytes(Path.Combine(nested.FullName, "Pilot-ECM-setup_25.1.0.1.zip"), new byte[5]);

            var found = SetupFolderScanner.Scan(root);

            CollectionAssert.AreEqual(
                new[] { "IceEnterprise 26.9.0.61368 .exe 20", "IceEnterprise 25.12.0.57162 .zip 30", "Bim 26.11.0.61894 .zip 40" },
                found.Select(s => $"{s.Edition.Id} {s.Version} {Path.GetExtension(s.Path)} {s.SizeBytes}").ToArray());
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public void ScannerOfAMissingFolderFailsLoudly()
    {
        var missing = Path.Combine(TestSupport.NewTempDir(), "gone");

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SetupFolderScanner.Scan(missing));
    }

    [TestMethod]
    public async Task ZipForOfflineLandsInTheFolderAsIsAndIsNotUnpacked()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var data = new byte[300_000];
            Random.Shared.NextBytes(data);
            var http = new HttpClient(new FakeFileHandler(data));
            var store = new VersionStore(Path.Combine(root, "store"));
            var installer = new VersionInstaller(store, new ReleaseCatalog(http, Path.Combine(root, "cache.html"), new Uri("http://test/release/")),
                new FileDownloader(http), new DiskSpace(_ => long.MaxValue), NullLog.Instance);
            var entry = new ReleaseEntry(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), "Pilot-BIM-setup_26.11.0.61894.zip", data.Length, null);
            var folder = Path.Combine(root, "usb", "pilot");
            var reports = new List<DownloadProgress>();

            await installer.DownloadToFolderAsync(entry, folder, new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

            var target = Path.Combine(folder, entry.FileName);
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(target));
            Assert.AreEqual(entry.FileName, Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Single(), "no .part file may stay");
            Assert.AreEqual(data.Length, reports[^1].Received);
            Assert.IsFalse(Directory.Exists(store.Root), "nothing is unpacked into the store");
            Assert.AreEqual(1, SetupFolderScanner.Scan(folder).Count, "the saved zip is found by the folder scan");

            await Assert.ThrowsExactlyAsync<IOException>(() => installer.DownloadToFolderAsync(entry, folder, null, CancellationToken.None));
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(target), "an existing file is never overwritten");
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public async Task InterruptedZipDownloadResumesFromTheLeftoverPartFile()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var data = new byte[200_000];
            Random.Shared.NextBytes(data);
            var handler = new FakeFileHandler(data);
            var http = new HttpClient(handler);
            var store = new VersionStore(Path.Combine(root, "store"));
            var installer = new VersionInstaller(store, new ReleaseCatalog(http, Path.Combine(root, "cache.html"), new Uri("http://test/release/")),
                new FileDownloader(http), new DiskSpace(_ => long.MaxValue), NullLog.Instance);
            var entry = new ReleaseEntry(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), "Pilot-BIM-setup_26.11.0.61894.zip", data.Length, null);
            var folder = Path.Combine(root, "usb");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, entry.FileName + ".part"), data[..50_000]);

            await installer.DownloadToFolderAsync(entry, folder, null, CancellationToken.None);

            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(Path.Combine(folder, entry.FileName)));
            CollectionAssert.AreEqual(new string?[] { "50000-" }, handler.RangeRequests);
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public async Task ZipForOfflineChecksFreeSpaceFirst()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var http = new HttpClient(new FakeFileHandler([1, 2, 3]));
            var installer = new VersionInstaller(new VersionStore(Path.Combine(root, "store")),
                new ReleaseCatalog(http, Path.Combine(root, "cache.html")), new FileDownloader(http), new DiskSpace(_ => 1024), NullLog.Instance);
            var entry = new ReleaseEntry(Editions.Get(EditionId.Bim), new Version(26, 11, 0, 61894), "Pilot-BIM-setup_26.11.0.61894.zip", 500L << 20, null);

            await Assert.ThrowsExactlyAsync<InsufficientDiskSpaceException>(() => installer.DownloadToFolderAsync(entry, Path.Combine(root, "usb"), null, CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    [TestMethod]
    public void SetupFolderSettingSurvivesSaveAndOldFilesMeanNotSet()
    {
        var root = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{ "StoreRoot": "D:\\Pilot" }""");
            Assert.IsNull(LauncherSettings.Load(path).SetupFolder);

            new LauncherSettings { StoreRoot = @"D:\Pilot", SetupFolder = @"E:\Installers" }.Save(path);

            Assert.AreEqual(@"E:\Installers", LauncherSettings.Load(path).SetupFolder);
        }
        finally
        {
            TestSupport.DeleteQuietly(root);
        }
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; the tests want to look at the reports right after the call.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
