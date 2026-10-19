using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;
using PilotLauncher.Core.Update;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class UpdateTests
{
    private static readonly byte[] NewExe = Encoding.ASCII.GetBytes("new launcher build");

    private static ExtensionRelease Release(byte[] content, string? sha) =>
        new("v1.2.0", new Version(1, 2, 0), "PilotLauncher-Setup.exe", content.Length, new Uri("https://example.test/PilotLauncher-Setup.exe"), sha, null, null);

    private static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static LauncherUpdater Updater(HttpMessageHandler handler, string cacheDir) =>
        new(new GitHubReleaseClient(new HttpClient(handler), cacheDir), new FileDownloader(new HttpClient(handler)), NullLog.Instance);

    [TestMethod]
    [DataRow("1.2.0", "1.1.0.0", true)]
    [DataRow("1.1.1", "1.1.0.0", true)]
    [DataRow("1.1.0", "1.1.0.0", false)]
    [DataRow("1.0.9", "1.1.0.0", false)]
    public void ComparesThreeNumberTagsWithTheFourNumberAssemblyVersion(string release, string current, bool expected) =>
        Assert.AreEqual(expected, LauncherUpdater.IsNewer(Version.Parse(release), Version.Parse(current)));

    [TestMethod]
    public void ATagThatIsNotAVersionIsNeverAnUpdate() => Assert.IsFalse(LauncherUpdater.IsNewer(null, new Version(1, 1, 0, 0)));

    [TestMethod]
    public async Task FindsTheNewerReleaseOnGitHub()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var json = """
                {"tag_name":"v1.2.0","published_at":"2026-10-20T10:00:00Z","body":"### Добавлено\n\n- обновление",
                 "assets":[{"name":"PilotLauncher-Setup.exe","size":18,"digest":"sha256:abc","browser_download_url":"https://example.test/PilotLauncher-Setup.exe"}]}
                """;
            var handler = new FakeFileHandler([]) { Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) } };

            var newer = await Updater(handler, dir).FindNewerAsync(new Version(1, 1, 0, 0), CancellationToken.None);
            var same = await Updater(handler, dir).FindNewerAsync(new Version(1, 2, 0, 0), CancellationToken.None);

            Assert.AreEqual("abc", newer?.Sha256);
            Assert.IsNull(same);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DownloadKeepsAFileWithTheRightChecksumNextToTheExe()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "PilotLauncher.exe");
            var path = await Updater(new FakeFileHandler(NewExe), dir).DownloadAsync(Release(NewExe, Sha(NewExe)), exe, null, CancellationToken.None);

            Assert.AreEqual(exe + ".new", path);
            CollectionAssert.AreEqual(NewExe, File.ReadAllBytes(path));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task DownloadWithAWrongChecksumIsRejectedAndRemoved()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "PilotLauncher.exe");
            var updater = Updater(new FakeFileHandler(NewExe), dir);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAsync(Release(NewExe, new string('0', 64)), exe, null, CancellationToken.None));
            Assert.IsFalse(File.Exists(exe + ".new"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task AReleaseWithoutChecksumIsNotDownloaded()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var handler = new FakeFileHandler(NewExe);
            var updater = Updater(handler, dir);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAsync(Release(NewExe, null), Path.Combine(dir, "PilotLauncher.exe"), null, CancellationToken.None));
            Assert.AreEqual(0, handler.RangeRequests.Count, "nothing must be requested");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task AFolderWithoutWriteAccessGivesAnErrorWithTheReleasesPage()
    {
        var dir = TestSupport.NewTempDir();
        var info = new DirectoryInfo(dir);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.CreateFiles, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var updater = Updater(new FakeFileHandler(NewExe), dir);

            var ex = await Assert.ThrowsExactlyAsync<IOException>(() =>
                updater.DownloadAsync(Release(NewExe, Sha(NewExe)), Path.Combine(dir, "PilotLauncher.exe"), null, CancellationToken.None));

            StringAssert.Contains(ex.Message, "Нет права записи");
            StringAssert.Contains(ex.Message, "https://github.com/mahach666/PilotLauncher/releases");
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void UpdateCheckIsOnForNewAndForOlderSettingsFiles()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.IsTrue(LauncherSettings.Load(path).CheckUpdates, "no file");

            File.WriteAllText(path, """{ "StoreRoot": "D:\\Pilot", "SetupFolder": null, "TrustRussianRoot": false }""");
            Assert.IsTrue(LauncherSettings.Load(path).CheckUpdates, "file of 1.0.0 without the setting");

            var settings = LauncherSettings.Load(path);
            settings.CheckUpdates = false;
            settings.Save(path);
            Assert.IsFalse(LauncherSettings.Load(path).CheckUpdates, "turned off by the user");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void ReplacePutsTheNewExeInPlaceAndKeepsTheOldOneAside()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "PilotLauncher.exe");
            File.WriteAllText(exe, "current");
            File.WriteAllText(exe + ".old", "from an earlier update");
            File.WriteAllText(exe + ".new", "next");

            LauncherUpdater.Replace(exe, exe + ".new");

            Assert.AreEqual("next", File.ReadAllText(exe));
            Assert.AreEqual("current", File.ReadAllText(exe + ".old"));
            Assert.IsFalse(File.Exists(exe + ".new"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void ReplaceRestoresTheCurrentExeWhenTheNewOneCannotBePlaced()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "PilotLauncher.exe");
            File.WriteAllText(exe, "current");

            Assert.ThrowsExactly<IOException>(() => LauncherUpdater.Replace(exe, exe + ".new")); // no downloaded file

            Assert.AreEqual("current", File.ReadAllText(exe));
            Assert.IsFalse(File.Exists(exe + ".old"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task CleanUpRemovesTheOldExeAndLeavesALockedOneForLater()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var exe = Path.Combine(dir, "PilotLauncher.exe");
            File.WriteAllText(exe + ".old", "old");
            await using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
                await LauncherUpdater.CleanUpAsync(exe, NullLog.Instance, attempts: 2, delay: TimeSpan.FromMilliseconds(10));
            Assert.IsTrue(File.Exists(exe + ".old"), "a locked file stays without an error");

            await LauncherUpdater.CleanUpAsync(exe, NullLog.Instance);
            Assert.IsFalse(File.Exists(exe + ".old"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }
}
