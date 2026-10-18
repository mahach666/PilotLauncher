using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class InstallPartsTests
{
    [TestMethod]
    public async Task DownloaderWritesFileAndRemovesPartFile()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var data = RandomBytes(300_000);
            var target = Path.Combine(dir, "a.zip");
            var reports = new List<DownloadProgress>();

            await new FileDownloader(new HttpClient(new FakeFileHandler(data)))
                .DownloadAsync(new Uri("http://test/a.zip"), target, new InlineProgress<DownloadProgress>(reports.Add), CancellationToken.None);

            CollectionAssert.AreEqual(data, File.ReadAllBytes(target));
            Assert.IsFalse(File.Exists(target + ".part"));
            Assert.IsTrue(reports.Count > 0);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DownloaderResumesFromPartFile()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var data = RandomBytes(200_000);
            var target = Path.Combine(dir, "a.zip");
            File.WriteAllBytes(target + ".part", data[..70_000]);
            var handler = new FakeFileHandler(data);

            await new FileDownloader(new HttpClient(handler)).DownloadAsync(new Uri("http://test/a.zip"), target, null, CancellationToken.None);

            CollectionAssert.AreEqual(data, File.ReadAllBytes(target));
            CollectionAssert.AreEqual(new[] { "70000-" }, handler.RangeRequests);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DownloaderRestartsWhenServerIgnoresRange()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var data = RandomBytes(120_000);
            var target = Path.Combine(dir, "a.zip");
            File.WriteAllBytes(target + ".part", data[..50_000]);

            await new FileDownloader(new HttpClient(new FakeFileHandler(data) { IgnoreRange = true }))
                .DownloadAsync(new Uri("http://test/a.zip"), target, null, CancellationToken.None);

            CollectionAssert.AreEqual(data, File.ReadAllBytes(target));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DownloaderRestartsWhenPartFileIsLongerThanTheFile()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var data = RandomBytes(10_000);
            var target = Path.Combine(dir, "a.zip");
            File.WriteAllBytes(target + ".part", RandomBytes(40_000)); // stale leftovers of some other file

            await new FileDownloader(new HttpClient(new FakeFileHandler(data)))
                .DownloadAsync(new Uri("http://test/a.zip"), target, null, CancellationToken.None);

            CollectionAssert.AreEqual(data, File.ReadAllBytes(target));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DiskSpaceRefusesWhenTooLittleIsFree()
    {
        var disk = new DiskSpace(_ => 100L << 20);
        var ex = Assert.ThrowsExactly<InsufficientDiskSpaceException>(() => disk.EnsureFree(@"C:\x", DiskSpace.RequiredForInstall(176L << 20)));
        Assert.AreEqual(100L << 20, ex.FreeBytes);
        disk.EnsureFree(@"C:\x", 50L << 20);
    }

    [TestMethod]
    public void DiskSpaceReadsRealFreeSpaceOfAnExistingFolder()
    {
        Assert.IsTrue(DiskSpace.GetFreeBytes(Path.GetTempPath()) > 0);
        // a folder that does not exist yet is measured through its nearest existing parent
        Assert.IsTrue(DiskSpace.GetFreeBytes(Path.Combine(Path.GetTempPath(), "not-yet", "created")) > 0);
    }

    [TestMethod]
    public void BurnBundleFindsTheLargestCabinet()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "setup.exe");
            var small = FakeCabinet(500);
            var big = FakeCabinet(5000);
            using (var f = File.Create(exe))
            {
                f.Write(RandomBytes(1000));
                f.Write(small);
                f.Write(RandomBytes(300));
                f.Write(big);
                f.Write(RandomBytes(77));
            }

            var found = BurnBundle.FindLargestCabinet(exe);

            Assert.AreEqual((1000L + 500 + 300, 5000L), found);
            var cab = Path.Combine(dir, "out.cab");
            BurnBundle.ExtractLargestCabinet(exe, cab);
            CollectionAssert.AreEqual(big, File.ReadAllBytes(cab));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void BurnBundleFindsSignatureThatCrossesTheReadBuffer()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "setup.exe");
            var cab = FakeCabinet(2000);
            // the first read fills 1 MiB + 8 bytes (buffer length); start the signature 3 bytes before that boundary
            var offset = (1 << 20) + 8 - 3;
            using (var f = File.Create(exe))
            {
                f.Write(new byte[offset]);
                f.Write(cab);
            }

            Assert.AreEqual((offset, 2000L), BurnBundle.FindLargestCabinet(exe));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void BurnBundleRejectsFilesWithoutCabinet()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "plain.exe");
            // a bare "MSCF" without a valid header must not count
            File.WriteAllBytes(exe, [.. RandomBytes(200), .. "MSCF\0\0\0\0"u8.ToArray(), .. new byte[100]]);

            Assert.IsNull(BurnBundle.FindLargestCabinet(exe));
            Assert.ThrowsExactly<InvalidDataException>(() => BurnBundle.ExtractLargestCabinet(exe, Path.Combine(dir, "x.cab")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void ClientLocatorPrefersTheKnownExecutable()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            Touch(dir, @"ProgramFiles64Folder\ASCON\Pilot-ICE Enterprise\Ascon.Pilot.PilotEnterprise.exe");
            Touch(dir, @"ProgramFiles64Folder\ASCON\Pilot-ICE Enterprise\xpsconverter.exe");

            var found = ClientLocator.Find(dir, Editions.Get(EditionId.IceEnterprise));

            Assert.AreEqual("Ascon.Pilot.PilotEnterprise.exe", found.ExeRelativePath);
            Assert.AreEqual(Path.Combine(dir, @"ProgramFiles64Folder\ASCON\Pilot-ICE Enterprise"), found.AppDir);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void ClientLocatorAcceptsASingleUnknownPilotExecutable()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            Touch(dir, @"app\Ascon.Pilot.PilotClient.exe");
            Touch(dir, @"app\helper.exe");

            Assert.AreEqual("Ascon.Pilot.PilotClient.exe", ClientLocator.Find(dir, Editions.Get(EditionId.Ice)).ExeRelativePath);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void ClientLocatorNamesWhatItFoundWhenNothingFits()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            Touch(dir, @"app\a.exe");
            Touch(dir, @"app\Ascon.Pilot.PilotOne.exe");
            Touch(dir, @"app\Ascon.Pilot.PilotTwo.exe");

            var ex = Assert.ThrowsExactly<FileNotFoundException>(() => ClientLocator.Find(dir, Editions.Get(EditionId.Ecm)));
            StringAssert.Contains(ex.Message, "Ascon.Pilot.PilotECM.exe");
            StringAssert.Contains(ex.Message, "a.exe");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void MsiSelectionPicksTheEditionsProductAndPrefers64Bit()
    {
        var packages = new[]
        {
            new MsiInfo("a", "Pilot-ICE Enterprise", false),
            new MsiInfo("b", "Pilot-ICE Enterprise", true),
            new MsiInfo("c", "Something Else", true),
        };

        Assert.AreEqual("b", MsiPackage.SelectClient(packages, Editions.Get(EditionId.IceEnterprise)).Path);
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => MsiPackage.SelectClient(packages, Editions.Get(EditionId.Bim)));
        StringAssert.Contains(ex.Message, "Something Else");
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(42 + count).NextBytes(bytes);
        // keep accidental signatures out of the test data
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == 0x4D) bytes[i] = 0x4E;
        return bytes;
    }

    private static byte[] FakeCabinet(int size)
    {
        var cab = new byte[size];
        "MSCF"u8.CopyTo(cab);
        BitConverter.TryWriteBytes(cab.AsSpan(8), (uint)size);
        BitConverter.TryWriteBytes(cab.AsSpan(16), 44u);
        cab[24] = 3;
        cab[25] = 1;
        return cab;
    }

    private static void Touch(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }
}
