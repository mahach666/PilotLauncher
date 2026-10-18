using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class ExtensionTests
{
    private static readonly ExtensionSource Lookup = ExtensionSource.Defaults[0];
    private static readonly ExtensionSource Dynamo = ExtensionSource.Defaults[1];
    private static readonly Edition Enterprise = Editions.Get(EditionId.IceEnterprise);

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(ExtensionSource source, string tag, byte[] zip, bool digest = true, string? sha = null) =>
        "{\"tag_name\":\"" + tag + "\",\"published_at\":\"2026-08-05T21:58:49Z\",\"body\":\"notes\",\"assets\":[{\"name\":\"" + source.AssetName +
        "\",\"size\":" + zip.Length + ",\"browser_download_url\":\"https://example.test/" + source.Id + "/" + tag + "/" + source.AssetName + "\"" +
        (digest ? ",\"digest\":\"sha256:" + (sha ?? Sha(zip)) + "\"" : "") + "}]}";

    /// <summary>A fake GitHub: answers the API and serves the zips; counts API calls.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, (string Tag, byte[] Zip, string? Sha)> Latest { get; } = new();
        public int ApiCalls { get; private set; }
        public List<string?> IfNoneMatch { get; } = [];
        public bool Offline { get; set; }
        public string ETag { get; set; } = "\"v1\"";
        public Dictionary<string, byte[]> Files { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline)
                throw new HttpRequestException("no network");
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "api.github.com")
            {
                ApiCalls++;
                var repo = path.Replace("/repos/", "").Replace("/releases/latest", "");
                var source = ExtensionSource.Defaults.First(s => s.Repo == repo);
                IfNoneMatch.Add(request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null);
                if (IfNoneMatch[^1] == ETag)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                if (!Latest.TryGetValue(source.Id, out var published))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var (tag, zip, sha) = published;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(source, tag, zip, sha: sha)) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
                return Task.FromResult(response);
            }

            var key = request.RequestUri.AbsolutePath.TrimStart('/');
            var parts = key.Split('/');
            var data = Latest.Where(kv => kv.Key == parts[0]).Select(kv => kv.Value).FirstOrDefault();
            var bytes = Files.TryGetValue(key, out var file) ? file : data.Zip;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class Env : IDisposable
    {
        public string Dir { get; } = TestSupport.NewTempDir();
        public VersionStore Store { get; }
        public FakeGitHub GitHub { get; } = new();
        public ExtensionStore Extensions { get; }
        public FakeLinker Linker { get; } = new();
        public string LocalAppData { get; }
        public ExtensionService Service { get; }

        public Env()
        {
            Store = new VersionStore(Path.Combine(Dir, "store"));
            Extensions = new ExtensionStore(Store.ExtensionsDir, NullLog.Instance);
            LocalAppData = Path.Combine(Dir, "local");
            var http = new HttpClient(GitHub);
            Service = new ExtensionService(Store, Extensions, new GitHubReleaseClient(http, Path.Combine(Dir, "cache")), new FileDownloader(http),
                new DiskSpace(_ => long.MaxValue), Linker, LocalAppData, "abcd", (e, v) => AccountNaming.UserName("abcd", e, v), NullLog.Instance);
        }

        public void Publish(ExtensionSource source, string tag, byte[] zip, string? sha = null) => GitHub.Latest[source.Id] = (tag, zip, sha);

        public void Dispose() => TestSupport.DeleteQuietly(Dir);

        public VersionEntry SystemEntry(string version = "26.11.0.61894") => new(Enterprise, Version.Parse(version), null, null,
            new SystemInstall(Enterprise, Version.Parse(version), @"C:\Program Files\ASCON\Pilot-ICE Enterprise", @"C:\Program Files\ASCON\Pilot-ICE Enterprise\x.exe"));

        public VersionEntry StoredEntry(string version = "26.9.0.61368")
        {
            var v = Version.Parse(version);
            var app = Store.AppDir(Enterprise, v);
            Directory.CreateDirectory(app);
            new VersionManifest("x.exe", VersionSource.Catalog, DateTime.UtcNow).Save(Store.ManifestPath(Enterprise, v));
            return new VersionEntry(Enterprise, v, null, Store.Find(Enterprise, v), null);
        }
    }

    private sealed class FakeLinker : IProfileLinker
    {
        public List<string> Linked { get; } = [];
        public bool IsLinked(DevelopmentTarget target) => Linked.Contains(target.Directory);

        public Task LinkAsync(DevelopmentTarget target, CancellationToken ct)
        {
            Linked.Add(target.Directory);
            return Task.CompletedTask;
        }
    }

    private static string LinkTarget(string developmentDir, ExtensionSource s) => Junction.Target(Path.Combine(developmentDir, s.FolderName))!;

    // --- junctions --------------------------------------------------------------------------------------------------

    [TestMethod]
    public void JunctionPointsAtAFolderAndRemovingItLeavesTheFolder()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var target = Path.Combine(dir, "real");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "a.txt"), "x");
            var link = Path.Combine(dir, "sub", "link");

            Junction.Create(link, target);

            Assert.IsTrue(Junction.IsLink(link));
            Assert.AreEqual(target, Junction.Target(link));
            Assert.AreEqual("x", File.ReadAllText(Path.Combine(link, "a.txt")), "files are reachable through the link");
            Assert.IsFalse(Junction.IsLink(target));
            Junction.Remove(link);
            Assert.IsFalse(Directory.Exists(link));
            Assert.IsTrue(File.Exists(Path.Combine(target, "a.txt")), "the folder behind the link must stay");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void JunctionRefusesToDeleteARealFolderOrReplaceAnExistingOne()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var real = Path.Combine(dir, "real");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "a.txt"), "x");

            Assert.ThrowsExactly<IOException>(() => Junction.Remove(real));
            Assert.ThrowsExactly<IOException>(() => Junction.Create(real, dir));
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => Junction.Create(Path.Combine(dir, "l"), Path.Combine(dir, "missing")));
            Assert.IsTrue(File.Exists(Path.Combine(real, "a.txt")));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DeleteTreeRemovesJunctionsAsLinksWithoutEnteringThem()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var shared = Path.Combine(dir, "shared");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "keep.dll"), "x");
            var profile = Path.Combine(dir, "profile");
            Junction.Create(Path.Combine(profile, "Development"), shared);

            Junction.DeleteTree(profile);

            Assert.IsFalse(Directory.Exists(profile));
            Assert.IsTrue(File.Exists(Path.Combine(shared, "keep.dll")), "the folder behind the link survives");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    // --- zip layout and the store -----------------------------------------------------------------------------------

    [TestMethod]
    public void ZipLayoutTellsRootedFromFlat()
    {
        static ZipLayout Detect(byte[] bytes)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            return ZipLayout.Detect(zip);
        }

        Assert.AreEqual("PilotLookUp", Detect(Zip(("PilotLookUp/a.dll", "1"), ("PilotLookUp/sub/b.dll", "22"))).RootFolder);
        Assert.IsNull(Detect(Zip(("a.dll", "1"), ("sub/b.dll", "2"))).RootFolder);
        Assert.IsNull(Detect(Zip(("PilotLookUp/a.dll", "1"), ("loose.txt", "2"))).RootFolder, "a loose file means the folder is not the root");
        Assert.AreEqual(3, Detect(Zip(("PilotLookUp/a.dll", "1"), ("PilotLookUp/sub/b.dll", "22"))).UnpackedBytes);
    }

    [TestMethod]
    public async Task StoreUnpacksARootedZipOnceAndKeepsNothingElse()
    {
        using var env = new Env();
        var zip = Zip(("PilotLookUp/PilotLookUp.ext2.dll", "dll"), ("PilotLookUp/lib/x.dll", "x"));
        env.Publish(Lookup, "2.8.0.0", zip);
        var release = (await new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "c")).GetLatestAsync(Lookup, false, default)).Release;

        await env.Extensions.EnsureInstalledAsync(Lookup, release, new FileDownloader(new HttpClient(env.GitHub)), new DiskSpace(_ => long.MaxValue), env.Store.TempDir, null, default);

        var content = env.Extensions.ContentDir(Lookup, "2.8.0.0");
        Assert.AreEqual("dll", File.ReadAllText(Path.Combine(content, "PilotLookUp.ext2.dll")));
        Assert.AreEqual("x", File.ReadAllText(Path.Combine(content, "lib", "x.dll")));
        Assert.IsTrue(env.Extensions.IsInstalled(Lookup, "2.8.0.0"));
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(env.Store.TempDir).Count(), "the zip and the work folder are deleted");
        Assert.IsFalse(Directory.Exists(env.Extensions.TagDir(Lookup.Id, "2.8.0.0") + ".partial"));
    }

    [TestMethod]
    public async Task StorePutsAFlatZipIntoTheExtensionFolder()
    {
        using var env = new Env();
        env.Publish(Lookup, "1.0.0.0", Zip(("PilotLookUp.ext2.dll", "dll")));
        var release = (await new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "c")).GetLatestAsync(Lookup, false, default)).Release;

        await env.Extensions.EnsureInstalledAsync(Lookup, release, new FileDownloader(new HttpClient(env.GitHub)), new DiskSpace(_ => long.MaxValue), env.Store.TempDir, null, default);

        Assert.IsTrue(File.Exists(Path.Combine(env.Extensions.ContentDir(Lookup, "1.0.0.0"), "PilotLookUp.ext2.dll")));
    }

    [TestMethod]
    public async Task StoreRefusesAWrongChecksumAndLeavesNothing()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")), sha: new string('0', 64));
        var release = (await new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "c")).GetLatestAsync(Lookup, false, default)).Release;

        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => env.Extensions.EnsureInstalledAsync(
            Lookup, release, new FileDownloader(new HttpClient(env.GitHub)), new DiskSpace(_ => long.MaxValue), env.Store.TempDir, null, default));

        StringAssert.Contains(ex.Message, "Контрольная сумма");
        Assert.IsFalse(Directory.Exists(env.Extensions.TagDir(Lookup.Id, "2.8.0.0")));
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(env.Store.TempDir).Count());
    }

    [TestMethod]
    public async Task StoreRefusesAnArchiveWhoseFolderHasAnotherName()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("SomethingElse/a.dll", "1")));
        var release = (await new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "c")).GetLatestAsync(Lookup, false, default)).Release;

        var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => env.Extensions.EnsureInstalledAsync(
            Lookup, release, new FileDownloader(new HttpClient(env.GitHub)), new DiskSpace(_ => long.MaxValue), env.Store.TempDir, null, default));

        StringAssert.Contains(ex.Message, "SomethingElse");
        Assert.IsFalse(Directory.Exists(env.Extensions.TagDir(Lookup.Id, "2.8.0.0")));
    }

    [TestMethod]
    public async Task StoreChecksFreeSpaceBeforeAndAfterTheDownload()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", new string('x', 5000))));
        var release = (await new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "c")).GetLatestAsync(Lookup, false, default)).Release;

        await Assert.ThrowsExactlyAsync<InsufficientDiskSpaceException>(() => env.Extensions.EnsureInstalledAsync(
            Lookup, release, new FileDownloader(new HttpClient(env.GitHub)), new DiskSpace(_ => 1000), env.Store.TempDir, null, default));

        Assert.IsFalse(Directory.Exists(env.Extensions.TagDir(Lookup.Id, "2.8.0.0")));
    }

    [TestMethod]
    public void ReferencesDecideWhichReleasesStay()
    {
        using var env = new Env();
        foreach (var tag in new[] { "1.0.0.0", "2.0.0.0" })
        {
            Directory.CreateDirectory(env.Extensions.ContentDir(Lookup, tag));
            File.WriteAllText(Path.Combine(env.Extensions.TagDir(Lookup.Id, tag), "package.json"), "{}");
        }

        env.Extensions.AddRef(Lookup.Id, "1.0.0.0", "acct-a", @"C:\dev");
        env.Extensions.AddRef(Lookup.Id, "1.0.0.0", "acct-b", @"C:\dev2");

        CollectionAssert.AreEqual(new[] { "2.0.0.0" }, env.Extensions.CollectUnused(Lookup.Id).ToArray(), "nobody uses 2.0.0.0");
        env.Extensions.RemoveRef(Lookup.Id, "1.0.0.0", "acct-a");
        Assert.AreEqual(0, env.Extensions.CollectUnused(Lookup.Id).Count, "acct-b still uses 1.0.0.0");
        env.Extensions.RemoveRefsOf("acct-b");
        CollectionAssert.AreEqual(new[] { "1.0.0.0" }, env.Extensions.CollectUnused(Lookup.Id).ToArray());
        Assert.AreEqual(0, env.Extensions.InstalledTags(Lookup.Id).Count);
    }

    [TestMethod]
    public void StoreRecognisesItsOwnFoldersByPath()
    {
        using var env = new Env();
        var inside = Path.Combine(env.Extensions.Root, "pilotlookup", "2.8.0.0", "PilotLookUp");

        Assert.IsTrue(env.Extensions.TryParseContentPath(inside, out var id, out var tag));
        Assert.AreEqual("pilotlookup", id);
        Assert.AreEqual("2.8.0.0", tag);
        Assert.IsFalse(env.Extensions.TryParseContentPath(Path.Combine(env.Dir, "elsewhere", "pilotlookup", "2.8.0.0", "x"), out _, out _));
        Assert.IsFalse(env.Extensions.TryParseContentPath(Path.Combine(env.Extensions.Root, "_replaced", "x", "y"), out _, out _), "the _replaced folder is not a release");
        Assert.IsFalse(env.Extensions.TryParseContentPath(null, out _, out _));
    }

    // --- releases from GitHub ---------------------------------------------------------------------------------------

    [TestMethod]
    public void ReleaseJsonIsReadWithDigestAndDate()
    {
        var zip = Zip(("PilotLookUp/a.dll", "1"));

        var release = GitHubReleaseClient.Parse(ReleaseJson(Lookup, "2.8.0.0", zip), Lookup);

        Assert.AreEqual("2.8.0.0", release.Tag);
        Assert.AreEqual(new Version(2, 8, 0, 0), release.Version);
        Assert.AreEqual(Sha(zip), release.Sha256!.ToLowerInvariant());
        Assert.AreEqual(zip.Length, release.Size);
        Assert.AreEqual(new DateTime(2026, 8, 5, 21, 58, 49, DateTimeKind.Utc), release.PublishedUtc);
        Assert.AreEqual("https://example.test/pilotlookup/2.8.0.0/PilotLookUp.zip", release.DownloadUri.ToString());
        Assert.IsNull(GitHubReleaseClient.Parse(ReleaseJson(Lookup, "v1.0", zip, digest: false), Lookup).Sha256);
        Assert.ThrowsExactly<InvalidDataException>(() => GitHubReleaseClient.Parse(ReleaseJson(Dynamo, "1.0.0.8", zip), Lookup));
    }

    [TestMethod]
    [DataRow("2.8.0.0", "2.6.0.0", true)]
    [DataRow("2.8.0.0", "2.8.0.0", false)]
    [DataRow("2.6.0.0", "2.8.0.0", false)]
    [DataRow("v1.0.0.9", "1.0.0.8", true)]
    [DataRow("nightly-2", "nightly-1", true)]
    [DataRow("nightly-2", "nightly-2", false)]
    public void UpdateIsOfferedOnlyForANewerRelease(string latest, string installed, bool expected)
    {
        var release = new ExtensionRelease(latest, ExtensionRelease.TryParseTag(latest, out var v) ? v : null, "a.zip", 1, new Uri("https://x/a.zip"), null, null, null);

        Assert.AreEqual(expected, release.IsNewerThan(installed));
    }

    [TestMethod]
    public async Task ReleaseAnswerIsSavedReusedAndRefreshedWithTheETag()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var client = new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "cache"));

        var first = await client.GetLatestAsync(Lookup, false, default);
        var again = await client.GetLatestAsync(Lookup, false, default);          // fresh: no request
        var forced = await client.GetLatestAsync(Lookup, true, default);          // asks again, GitHub says "not modified"

        Assert.AreEqual(1 + 1, env.GitHub.ApiCalls);
        Assert.IsNull(env.GitHub.IfNoneMatch[0]);
        Assert.AreEqual("\"v1\"", env.GitHub.IfNoneMatch[1]);
        Assert.AreEqual("2.8.0.0", forced.Release.Tag);
        Assert.IsNull(first.Error);
        Assert.IsNull(again.Error);
        Assert.IsNull(forced.Error);
    }

    [TestMethod]
    public async Task ANewReleaseReplacesTheSavedOne()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.6.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var client = new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "cache"));
        await client.GetLatestAsync(Lookup, false, default);

        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "2")));
        env.GitHub.ETag = "\"v2\"";
        var latest = await client.GetLatestAsync(Lookup, true, default);

        Assert.AreEqual("2.8.0.0", latest.Release.Tag);
    }

    [TestMethod]
    public async Task OfflineUsesTheSavedAnswerOrFailsWhenThereIsNone()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var client = new GitHubReleaseClient(new HttpClient(env.GitHub), Path.Combine(env.Dir, "cache"));
        env.GitHub.Offline = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetLatestAsync(Lookup, true, default));

        env.GitHub.Offline = false;
        await client.GetLatestAsync(Lookup, true, default);
        env.GitHub.Offline = true;
        var offline = await client.GetLatestAsync(Lookup, true, default);

        Assert.AreEqual("2.8.0.0", offline.Release.Tag);
        Assert.AreEqual("no network", offline.Error);
    }

    // --- installing, updating, removing -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InstallLinksTheSharedCopyIntoTheDevelopmentFolder()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/PilotLookUp.ext2.dll", "dll")));
        var entry = env.SystemEntry();
        var target = env.Service.TargetFor(entry)!;
        var status = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup);
        Assert.AreEqual(ExtensionState.NotInstalled, status.State);

        await env.Service.InstallAsync(target, Lookup, status.Latest!, false, null, default);

        Assert.AreEqual(Path.Combine(env.LocalAppData, "ASCON", "Pilot-ICE Enterprise", "Development"), target.Directory);
        Assert.AreEqual(env.Extensions.ContentDir(Lookup, "2.8.0.0"), LinkTarget(target.Directory, Lookup));
        Assert.AreEqual("dll", File.ReadAllText(Path.Combine(target.Directory, "PilotLookUp", "PilotLookUp.ext2.dll")));
        var after = (await env.Service.GetStatusAsync(target, false, default)).Single(s => s.Source == Lookup);
        Assert.AreEqual(ExtensionState.UpToDate, after.State);
        Assert.AreEqual("2.8.0.0", after.InstalledTag);
        Assert.AreEqual(0, env.Linker.Linked.Count, "the system installation needs no profile link");
    }

    [TestMethod]
    public async Task UpdateRepointsTheLinkAndFreesTheOldRelease()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.6.0.0", Zip(("PilotLookUp/a.dll", "old")));
        var target = env.Service.TargetFor(env.SystemEntry())!;
        var first = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup).Latest!;
        await env.Service.InstallAsync(target, Lookup, first, false, null, default);

        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "new")));
        env.GitHub.ETag = "\"v2\"";
        var status = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup);
        Assert.AreEqual(ExtensionState.UpdateAvailable, status.State);
        await env.Service.InstallAsync(target, Lookup, status.Latest!, false, null, default);

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(target.Directory, "PilotLookUp", "a.dll")));
        CollectionAssert.AreEqual(new[] { "2.8.0.0" }, env.Extensions.InstalledTags(Lookup.Id).ToArray(), "2.6.0.0 is not used any more");
        Assert.AreEqual(ExtensionState.UpToDate, (await env.Service.GetStatusAsync(target, false, default)).Single(s => s.Source == Lookup).State);
    }

    [TestMethod]
    public async Task RemoveDeletesTheLinkAndTheUnusedCopy()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var target = env.Service.TargetFor(env.SystemEntry())!;
        await env.Service.InstallAsync(target, Lookup, (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup).Latest!, false, null, default);

        await env.Service.RemoveAsync(target, Lookup, default);

        Assert.IsFalse(Directory.Exists(Path.Combine(target.Directory, "PilotLookUp")));
        Assert.AreEqual(0, env.Extensions.InstalledTags(Lookup.Id).Count);
        await env.Service.RemoveAsync(target, Lookup, default); // removing what is not there is fine
    }

    [TestMethod]
    public async Task TwoVersionsShareOneCopyAndItGoesWhenTheLastLinkGoes()
    {
        using var env = new Env();
        env.Publish(Dynamo, "1.0.0.8", Zip(("DynamoPilot.App/shell.zip", new string('z', 10_000))));
        var system = env.Service.TargetFor(env.SystemEntry())!;
        var isolated = env.Service.TargetFor(env.StoredEntry())!;
        var release = (await env.Service.GetStatusAsync(system, true, default)).Single(s => s.Source == Dynamo).Latest!;

        await env.Service.InstallAsync(system, Dynamo, release, false, null, default);
        await env.Service.InstallAsync(isolated, Dynamo, release, false, null, default);

        Assert.AreEqual(LinkTarget(system.Directory, Dynamo), LinkTarget(isolated.Directory, Dynamo), "both link to the same folder");
        Assert.AreEqual(1, env.Extensions.InstalledTags(Dynamo.Id).Count, "one copy on disk");
        await env.Service.RemoveAsync(system, Dynamo, default);
        Assert.AreEqual(1, env.Extensions.InstalledTags(Dynamo.Id).Count, "the other version still uses it");
        Assert.IsTrue(File.Exists(Path.Combine(isolated.Directory, "DynamoPilot.App", "shell.zip")));
        await env.Service.RemoveAsync(isolated, Dynamo, default);
        Assert.AreEqual(0, env.Extensions.InstalledTags(Dynamo.Id).Count);
    }

    [TestMethod]
    public async Task AnIsolatedProfileIsLinkedOnceBeforeTheFirstInstall()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        env.Publish(Dynamo, "1.0.0.8", Zip(("DynamoPilot.App/a.dll", "1")));
        var target = env.Service.TargetFor(env.StoredEntry())!;
        Assert.IsTrue(env.Service.NeedsLink(target));
        Assert.IsNotNull(target.Profile);
        Assert.AreEqual(env.Store.ProfileDevelopmentDir(target.Profile.UserName), target.Directory);

        var statuses = await env.Service.GetStatusAsync(target, true, default);
        await env.Service.InstallAsync(target, Lookup, statuses.Single(s => s.Source == Lookup).Latest!, false, null, default);
        await env.Service.InstallAsync(target, Dynamo, statuses.Single(s => s.Source == Dynamo).Latest!, false, null, default);

        CollectionAssert.AreEqual(new[] { target.Directory }, env.Linker.Linked.ToArray(), "linked once, not per extension");
        Assert.IsFalse(env.Service.NeedsLink(target));
    }

    [TestMethod]
    public async Task AFolderNotMadeByTheLauncherIsNotTouchedWithoutPermission()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/new.dll", "new")));
        var target = env.Service.TargetFor(env.SystemEntry())!;
        var manual = Path.Combine(target.Directory, "PilotLookUp");
        Directory.CreateDirectory(manual);
        File.WriteAllText(Path.Combine(manual, "mine.dll"), "mine");
        var latest = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup);
        Assert.AreEqual(ExtensionState.Foreign, latest.State);

        await Assert.ThrowsExactlyAsync<ForeignExtensionException>(() => env.Service.InstallAsync(target, Lookup, latest.Latest!, false, null, default));
        await Assert.ThrowsExactlyAsync<ForeignExtensionException>(() => env.Service.RemoveAsync(target, Lookup, default));
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(manual, "mine.dll")), "nothing changed");
        Assert.IsFalse(Junction.IsLink(manual));

        await env.Service.InstallAsync(target, Lookup, latest.Latest!, true, null, default);

        Assert.IsTrue(Junction.IsLink(manual));
        var aside = Directory.EnumerateDirectories(Path.Combine(env.Extensions.Root, "_replaced")).Single();
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(aside, "mine.dll")), "the old folder is kept, not deleted");
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(manual, "new.dll")));
    }

    [TestMethod]
    public async Task ALinkToSomewhereElseIsForeignAndOnlyTheLinkIsReplaced()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/new.dll", "new")));
        var target = env.Service.TargetFor(env.SystemEntry())!;
        var mine = Path.Combine(env.Dir, "my-build");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "dev.dll"), "dev");
        Junction.Create(Path.Combine(target.Directory, "PilotLookUp"), mine);
        var latest = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup);
        Assert.AreEqual(ExtensionState.Foreign, latest.State);

        await env.Service.InstallAsync(target, Lookup, latest.Latest!, true, null, default);

        Assert.AreEqual("dev", File.ReadAllText(Path.Combine(mine, "dev.dll")), "the folder behind the old link is untouched");
        Assert.AreEqual(env.Extensions.ContentDir(Lookup, "2.8.0.0"), LinkTarget(target.Directory, Lookup));
        Assert.IsFalse(Directory.Exists(Path.Combine(env.Extensions.Root, "_replaced")), "a link has nothing to move aside");
    }

    [TestMethod]
    public async Task StatusWithoutGitHubStillShowsWhatIsInstalled()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var target = env.Service.TargetFor(env.SystemEntry())!;
        var latest = (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup).Latest!;
        await env.Service.InstallAsync(target, Lookup, latest, false, null, default);
        env.GitHub.Offline = true;
        Directory.Delete(Path.Combine(env.Dir, "cache"), recursive: true); // nothing saved either

        var statuses = await env.Service.GetStatusAsync(target, true, default);

        Assert.AreEqual(ExtensionState.UpToDate, statuses.Single(s => s.Source == Lookup).State);
        Assert.AreEqual(ExtensionState.Unknown, statuses.Single(s => s.Source == Dynamo).State);
        Assert.IsNotNull(statuses.Single(s => s.Source == Dynamo).Error);
    }

    [TestMethod]
    public async Task ForgettingAnAccountDropsItsDevelopmentFolderAndReleasesTheCopies()
    {
        using var env = new Env();
        env.Publish(Lookup, "2.8.0.0", Zip(("PilotLookUp/a.dll", "1")));
        var entry = env.StoredEntry();
        var target = env.Service.TargetFor(entry)!;
        await env.Service.InstallAsync(target, Lookup, (await env.Service.GetStatusAsync(target, true, default)).Single(s => s.Source == Lookup).Latest!, false, null, default);
        var account = target.Profile!.UserName;

        env.Service.ForgetAccount(account);

        Assert.IsFalse(Directory.Exists(env.Store.ProfileDir(account)));
        Assert.AreEqual(0, env.Extensions.InstalledTags(Lookup.Id).Count);
    }

    [TestMethod]
    public void NoTargetForAVersionThatIsNotOnThisPc()
    {
        using var env = new Env();

        Assert.IsNull(env.Service.TargetFor(new VersionEntry(Enterprise, new Version(26, 13, 0, 62219), null, null, null)));
    }

    // --- the elevated half ------------------------------------------------------------------------------------------

    private sealed class FakeProfiles : IProfileLocator
    {
        public Dictionary<string, string> Folders { get; } = new();
        public string? Find(string userName) => Folders.GetValueOrDefault(userName);

        public string GetOrCreate(string userName)
        {
            Folders.TryAdd(userName, Path.Combine(Path.GetTempPath(), "never-used-" + userName));
            return Folders[userName];
        }
    }

    private const string Account = "plab12E26x13x62219";

    private static (string Store, string Target, string Profile, FakeProfiles Profiles, ProfileDevelopmentAdmin Admin, List<string> Grants) Admin(string dir)
    {
        var store = Path.Combine(dir, "store");
        var target = Path.Combine(store, ".profiles", Account, "Development");
        Directory.CreateDirectory(target);
        var profiles = new FakeProfiles();
        var profile = Path.Combine(dir, "Users", Account);
        Directory.CreateDirectory(profile);
        profiles.Folders[Account] = profile;
        var grants = new List<string>();
        return (store, target, profile, profiles, new ProfileDevelopmentAdmin(profiles, (d, u) => grants.Add($"{u}:{d}")), grants);
    }

    [TestMethod]
    public void AdminLinksTheProfileToTheStoreFolderAndGrantsTheAccountAccess()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, _, admin, grants) = Admin(dir);

            admin.Link(Account, "Pilot-ICE Enterprise", target, store);

            var development = ProfileDevelopmentAdmin.DevelopmentIn(profile, Enterprise);
            Assert.AreEqual(target, Junction.Target(development));
            CollectionAssert.AreEqual(new[] { $"{Account}:{target}" }, grants);
            admin.Link(Account, "Pilot-ICE Enterprise", target, store); // again: nothing breaks
            Assert.AreEqual(target, Junction.Target(development));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminMovesWhatTheAccountAlreadyHadIntoTheSharedFolder()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, _, admin, _) = Admin(dir);
            var development = ProfileDevelopmentAdmin.DevelopmentIn(profile, Enterprise);
            Directory.CreateDirectory(Path.Combine(development, "OldPlugin"));
            File.WriteAllText(Path.Combine(development, "OldPlugin", "o.dll"), "old");

            admin.Link(Account, "Pilot-ICE Enterprise", target, store);

            Assert.AreEqual("old", File.ReadAllText(Path.Combine(target, "OldPlugin", "o.dll")));
            Assert.IsTrue(Junction.IsLink(development));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminRefusesWhatIsNotItsBusiness()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, profiles, admin, grants) = Admin(dir);
            var other = Path.Combine(store, ".profiles", Account, "Other");
            Directory.CreateDirectory(other);

            Assert.ThrowsExactly<AdminOperationException>(() => admin.Link("Administrator", "Pilot-ICE Enterprise", target, store));
            Assert.ThrowsExactly<AdminOperationException>(() => admin.Link(Account, "Pilot-Server", target, store));
            Assert.ThrowsExactly<AdminOperationException>(() => admin.Link(Account, "Pilot-ICE Enterprise", other, store));
            Assert.ThrowsExactly<AdminOperationException>(() => admin.Link(Account, "Pilot-ICE Enterprise", Path.Combine(dir, "elsewhere"), store));
            Assert.ThrowsExactly<AdminOperationException>(() => admin.UnlinkAll("Administrator"));
            Assert.AreEqual(0, grants.Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(profile, "AppData")), "the profile was not touched");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminUnlinksBeforeAProfileIsDeletedAndKeepsTheSharedFiles()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, _, admin, _) = Admin(dir);
            File.WriteAllText(Path.Combine(target, "keep.dll"), "x");
            admin.Link(Account, "Pilot-ICE Enterprise", target, store);

            admin.UnlinkAll(Account);

            Assert.IsFalse(Directory.Exists(ProfileDevelopmentAdmin.DevelopmentIn(profile, Enterprise)));
            Assert.IsTrue(File.Exists(Path.Combine(target, "keep.dll")));
            admin.UnlinkAll(Account); // nothing left: fine
            new ProfileDevelopmentAdmin(new FakeProfiles()).UnlinkAll(Account); // an account that never signed in: fine
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminCommandLinkDevelopmentGoesThroughTheSameGuards()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, _, admin, _) = Admin(dir);
            var result = Path.Combine(dir, "result.txt");

            var bad = AdminCommands.Run(["link-development", "--user", "Administrator", "--product", "Pilot-ICE Enterprise", "--target", target, "--store", store, "--result", result],
                new StubAccounts(), null, admin);
            Assert.AreEqual(1, bad);
            StringAssert.Contains(File.ReadAllText(result), "Administrator");

            var good = AdminCommands.Run(["link-development", "--user", Account, "--product", "Pilot-ICE Enterprise", "--target", target, "--store", store, "--result", result],
                new StubAccounts(), null, admin);
            Assert.AreEqual(0, good);
            Assert.IsTrue(Junction.IsLink(ProfileDevelopmentAdmin.DevelopmentIn(profile, Enterprise)));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DeletingAnAccountUnlinksItsDevelopmentFolderFirst()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var (store, target, profile, _, admin, _) = Admin(dir);
            admin.Link(Account, "Pilot-ICE Enterprise", target, store);
            var accounts = new StubAccounts();
            accounts.OnDelete = () => Assert.IsFalse(Junction.IsLink(ProfileDevelopmentAdmin.DevelopmentIn(profile, Enterprise)), "the link must be gone before the profile is deleted");

            var code = AdminCommands.Run(["delete-user", "--user", Account, "--profile", "yes"], accounts, null, admin);

            Assert.AreEqual(0, code);
            Assert.AreEqual(1, accounts.Deleted);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    private sealed class StubAccounts : ILocalAccounts
    {
        public int Deleted { get; private set; }
        public Action? OnDelete { get; set; }
        public bool Exists(string userName) => true;
        public bool PasswordWorks(string userName, string password) => true;
        public void Ensure(string userName, string password, string fullName, string description) { }

        public void Delete(string userName, bool deleteProfile)
        {
            OnDelete?.Invoke();
            Deleted++;
        }
    }

    // --- the launcher's half ----------------------------------------------------------------------------------------

    private sealed class FakeProvisioner : IAccountProvisioner
    {
        public List<string> Ensured { get; } = [];

        public Task EnsureAsync(string userName, string fullName, CancellationToken ct)
        {
            Ensured.Add(userName);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string userName, bool deleteProfile, CancellationToken ct) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task LinkerPreparesTheAccountThenAsksForElevationOnceAndRemembersIt()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new VersionStore(Path.Combine(dir, "store"));
            var calls = new List<string[]>();
            var provisioner = new FakeProvisioner();
            var linker = new ElevatedProfileLinker(store, provisioner, NullLog.Instance, (args, _) => { calls.Add(args.ToArray()); return Task.CompletedTask; });
            var version = new Version(26, 13, 0, 62219);
            var target = new DevelopmentTarget(store.ProfileDevelopmentDir(Account), "x", Account, new IsolatedProfile(Enterprise, version, Account));
            Directory.CreateDirectory(target.Directory);
            Assert.IsFalse(linker.IsLinked(target));

            await linker.LinkAsync(target, default);

            CollectionAssert.AreEqual(new[] { Account }, provisioner.Ensured);
            CollectionAssert.AreEqual(
                new[] { "link-development", "--user", Account, "--product", "Pilot-ICE Enterprise", "--target", target.Directory, "--store", store.Root }, calls.Single());
            Assert.IsTrue(linker.IsLinked(target));
            var other = target with { Profile = new IsolatedProfile(Editions.Get(EditionId.Bim), version, Account) };
            Assert.IsFalse(linker.IsLinked(other), "a marker for another product does not count");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DefaultSourcesAreTheTwoRepositories()
    {
        CollectionAssert.AreEqual(new[] { "mahach666/PilotLookUp", "mahach666/DynamoPilot" }, ExtensionSource.Defaults.Select(s => s.Repo).ToArray());
        CollectionAssert.AreEqual(new[] { "PilotLookUp", "DynamoPilot.App" }, ExtensionSource.Defaults.Select(s => s.FolderName).ToArray());
        Assert.AreEqual(ExtensionSource.Defaults.Count, ExtensionSource.Defaults.Select(s => s.Id).Distinct().Count());
    }
}
