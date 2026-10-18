using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

/// <summary>
/// Talks to the real GitHub and downloads one small release (PilotLookUp, a few MB) into temporary folders; nothing outside
/// them is touched. Off by default: set the environment variable PILOTLAUNCHER_LIVE_GITHUB=1 to run it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public class LiveGitHubTests
{
    private sealed class NoLinker : IProfileLinker
    {
        public bool IsLinked(DevelopmentTarget target) => true;
        public Task LinkAsync(DevelopmentTarget target, CancellationToken ct) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task RealPilotLookUpReleaseIsDownloadedVerifiedLinkedUpdatedStateAndRemoved()
    {
        if (Environment.GetEnvironmentVariable("PILOTLAUNCHER_LIVE_GITHUB") != "1")
            Assert.Inconclusive("Set PILOTLAUNCHER_LIVE_GITHUB=1 to run the live GitHub check.");
        var dir = TestSupport.NewTempDir();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PilotLauncher-tests/1.0");
            var store = new VersionStore(Path.Combine(dir, "store"));
            var extensions = new ExtensionStore(store.ExtensionsDir, NullLog.Instance);
            var localAppData = Path.Combine(dir, "local");
            var service = new ExtensionService(store, extensions, new GitHubReleaseClient(http, Path.Combine(dir, "cache")), new FileDownloader(http),
                new DiskSpace(), new NoLinker(), localAppData, "abcd", (e, v) => AccountNaming.UserName("abcd", e, v), NullLog.Instance)
            {
                Sources = [ExtensionSource.Defaults[0]],
            };
            var edition = Editions.Get(EditionId.IceEnterprise);
            var entry = new VersionEntry(edition, new Version(26, 11, 0, 61894), null, null,
                new SystemInstall(edition, new Version(26, 11, 0, 61894), @"C:\Program Files\ASCON\Pilot-ICE Enterprise", @"C:\Program Files\ASCON\Pilot-ICE Enterprise\x.exe"));
            var target = service.TargetFor(entry)!;
            var source = ExtensionSource.Defaults[0];

            var status = (await service.GetStatusAsync(target, true, default)).Single();
            Assert.AreEqual(ExtensionState.NotInstalled, status.State, status.Error);
            Assert.IsNotNull(status.Latest!.Sha256, "GitHub reports a SHA-256 for the asset");
            Assert.IsTrue(status.Latest.Size > 100_000);

            await service.InstallAsync(target, source, status.Latest, false, null, default);

            var link = Path.Combine(target.Directory, "PilotLookUp");
            Assert.IsTrue(Junction.IsLink(link));
            Assert.IsTrue(File.Exists(Path.Combine(link, "PilotLookUp.ext2.dll")), "the extension's main file is reachable through the link");
            Assert.AreEqual(ExtensionState.UpToDate, (await service.GetStatusAsync(target, false, default)).Single().State);
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(store.TempDir).Count(), "the zip is deleted after unpacking");

            await service.RemoveAsync(target, source, default);

            Assert.IsFalse(Directory.Exists(link));
            Assert.AreEqual(0, extensions.InstalledTags(source.Id).Count, "the shared copy goes with the last link");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }
}
