using System.Net;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class CatalogTests
{
    private static string Sample => File.ReadAllText(TestSupport.Fixture("release-listing.sample.html"));

    [TestMethod]
    [DataRow("Pilot-ICE-Enterprise-setup_26.13.0.62219.zip", EditionId.IceEnterprise, "26.13.0.62219")]
    [DataRow("Pilot-ICE-setup_15.0.11.14613.zip", EditionId.Ice, "15.0.11.14613")]
    [DataRow("Pilot-BIM-setup_20.3.0.32988.zip", EditionId.Bim, "20.3.0.32988")]
    [DataRow("Pilot-ECM-setup_17.0.22.19582.zip", EditionId.Ecm, "17.0.22.19582")]
    public void ParsesSetupFileNames(string file, EditionId expectedEdition, string expectedVersion)
    {
        Assert.IsTrue(Editions.TryParseSetupFile(file, out var edition, out var version));
        Assert.AreEqual(expectedEdition, edition.Id);
        Assert.AreEqual(Version.Parse(expectedVersion), version);
    }

    [TestMethod]
    [DataRow("Pilot-ICE-Enterprise-setup.zip")]
    [DataRow("Pilot-Server-setup_26.13.0.62219.zip")]
    [DataRow("Pilot-BIM-Server-setup_26.13.0.62219.zip")]
    [DataRow("Pilot-ICE-setup_26.13.0.62219.zip.part")]
    [DataRow("pilot-bim-setup_26.13.0.62219.zip")]
    public void IgnoresOtherFiles(string file) => Assert.IsFalse(Editions.TryParseSetupFile(file, out _, out _));

    [TestMethod]
    public void ListingKeepsOnlyClientPackagesNewestFirst()
    {
        var entries = ReleaseListingParser.Parse(Sample);

        Assert.AreEqual(17, entries.Count); // 4 + 4 + 5 + 4, unversioned "latest" files and servers are skipped
        Assert.AreEqual(4, entries.Count(e => e.Edition.Id == EditionId.IceEnterprise));
        Assert.AreEqual(5, entries.Count(e => e.Edition.Id == EditionId.Bim));

        var enterprise = entries.Where(e => e.Edition.Id == EditionId.IceEnterprise).Select(e => e.Version.ToString()).ToList();
        CollectionAssert.AreEqual(new[] { "26.13.0.62219", "26.9.0.61368", "26.4.0.60408", "15.0.61.19861" }, enterprise);
    }

    [TestMethod]
    public void ListingReadsSizeAndDate()
    {
        var entry = ReleaseListingParser.Parse(Sample).Single(e => e.FileName == "Pilot-ICE-Enterprise-setup_26.9.0.61368.zip");

        Assert.AreEqual(176L * 1024 * 1024, entry.SizeBytes);
        Assert.AreEqual(new DateTime(2026, 5, 6, 13, 18, 0), entry.Published);
    }

    [TestMethod]
    [DataRow("-", 0L)]
    [DataRow("3260", 3260L)]
    [DataRow("27K", 27L * 1024)]
    [DataRow("176M", 176L * 1024 * 1024)]
    [DataRow("1.5G", 1610612736L)]
    public void ParsesSizes(string text, long expected) => Assert.AreEqual(expected, ReleaseListingParser.ParseSize(text));

    [TestMethod]
    public void RejectsGarbageSize() => Assert.ThrowsExactly<FormatException>(() => ReleaseListingParser.ParseSize("many"));

    [TestMethod]
    public async Task LoadSavesCacheAndFallsBackToItWhenOffline()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "cache.html");
            var online = new FakeFileHandler([]) { Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sample) } };
            var live = await new ReleaseCatalog(new HttpClient(online), cache).LoadAsync(CancellationToken.None);
            Assert.IsFalse(live.FromCache);
            Assert.AreEqual(17, live.Entries.Count);
            Assert.IsTrue(File.Exists(cache));

            var offline = new FakeFileHandler([]) { Override = _ => throw new HttpRequestException("no network") };
            var cached = await new ReleaseCatalog(new HttpClient(offline), cache).LoadAsync(CancellationToken.None);
            Assert.IsTrue(cached.FromCache);
            Assert.AreEqual("no network", cached.Error);
            Assert.AreEqual(17, cached.Entries.Count);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task LoadFailsWhenOfflineWithoutCache()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var offline = new FakeFileHandler([]) { Override = _ => throw new HttpRequestException("no network") };
            var catalog = new ReleaseCatalog(new HttpClient(offline), Path.Combine(dir, "cache.html"));
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => catalog.LoadAsync(CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task EmptyListingIsAnErrorNotAnEmptyCatalog()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var handler = new FakeFileHandler([]) { Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") } };
            var catalog = new ReleaseCatalog(new HttpClient(handler), Path.Combine(dir, "cache.html"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.LoadAsync(CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DownloadUriPointsInsideTheReleaseFolder()
    {
        var catalog = new ReleaseCatalog(new HttpClient(), "unused");
        var entry = ReleaseListingParser.Parse(Sample).First();

        Assert.AreEqual($"https://pilot.ascon.ru/release/{entry.FileName}", catalog.GetDownloadUri(entry).ToString());
    }
}
