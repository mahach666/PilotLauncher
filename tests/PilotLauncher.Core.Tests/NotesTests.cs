using System.Net;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Notes;

namespace PilotLauncher.Core.Tests;

/// <summary>
/// Fixtures/release-notes.sample.html is the real page https://help.pilotems.com/ru/Content/RelNotes.htm cut down to nine releases
/// (rows of other products and most fixes dropped, the rest untouched, including the commented-out table that precedes the first release).
/// </summary>
[TestClass]
public class NotesTests
{
    private static string Sample => File.ReadAllText(TestSupport.Fixture("release-notes.sample.html"));

    private static IReadOnlyList<ReleaseNotes> All => ReleaseNotesParser.Parse(Sample);

    private static ReleaseNotes Release(string version) => All.Single(r => r.Version == Version.Parse(version));

    private static ProductNotes Product(string version, string name) => Release(version).Products.Single(p => p.Name == name);

    // ---- parser

    [TestMethod]
    public void ReleasesComeNewestFirstWithDates()
    {
        var all = All;

        CollectionAssert.AreEqual(
            new[] { "26.13.0.62219", "26.11.0.61894", "25.14.0.57594", "24.41.0.52174", "24.40.0.52162", "24.37.0.51703", "24.19.0.50468", "24.4.0.49743", "24.1.0.49302" },
            all.Select(r => r.Version.ToString()).ToArray());
        Assert.AreEqual(new DateTime(2026, 7, 7), all[0].Date);
        Assert.AreEqual(new DateTime(2024, 1, 31), all[^1].Date);
    }

    [TestMethod]
    public void CommentedOutTableIsNotARelease()
    {
        // the page keeps a draft table with "." items in a comment right before the first release
        Assert.IsTrue(Sample.Contains("<!--table>"));
        Assert.IsTrue(All.SelectMany(r => r.Products).SelectMany(p => p.Items).All(i => i.Text.Length > 1));
    }

    [TestMethod]
    public void ProductRowKeepsItemsWithoutMarkup()
    {
        var items = Product("26.13.0.62219", "Pilot-BIM Desktop").Items;

        Assert.AreEqual(4, items.Count); // a "<!--26.13-->" comment sits between items 2 and 3
        StringAssert.Contains(items[0].Text, "тел, описанных классом IFCAdvancedBrep и отображение");
        StringAssert.EndsWith(items[0].Text, "(PI#2-2510).");
        Assert.IsTrue(items.All(i => !i.Text.Contains('<') && i.Product is null));
        StringAssert.Contains(items[3].Text, "критерии поиска Заполнен и Не заполнен");
    }

    [TestMethod]
    public void NonBreakingSpacesAndLinksBecomeOrdinaryText()
    {
        var items = Product("26.13.0.62219", "Pilot-ICE Desktop").Items;

        StringAssert.Contains(items[1].Text, "Реализованы права на Удаление элементов в корзину");
        StringAssert.Contains(items[3].Text, "версии документа, не попавшей в выбранный фильтр");
    }

    [TestMethod]
    public void EmptyFirstParagraphOfTheNameCellIsSkipped()
    {
        // 26.11.0.61894: <td><p>&#160;</p><p>Pilot-BIM Desktop</p></td>
        CollectionAssert.AreEqual(
            new[] { "Pilot-BIM Desktop", "Pilot-ICE Desktop", "Pilot-myAdmin Web" },
            Release("26.11.0.61894").Products.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void RemarkInTheNameCellIsNotPartOfTheName()
    {
        // 24.1.0.49302: Pilot-Web-Server, then a paragraph saying the product is an alpha version
        var names = Release("24.1.0.49302").Products.Select(p => p.Name).ToArray();

        CollectionAssert.Contains(names, "Pilot-Web-Server");
        Assert.IsTrue(names.All(n => n.Length < 30));
        Assert.AreEqual(6, Product("24.1.0.49302", "Pilot-Web-Server").Items.Count);
    }

    [TestMethod]
    public void NestedListBecomesBulletsOfItsItem()
    {
        var item = Product("26.11.0.61894", "Pilot-myAdmin Web").Items[0];

        var lines = item.Text.Split('\n');
        Assert.AreEqual(4, lines.Length);
        StringAssert.EndsWith(lines[0], "в том числе:");
        Assert.AreEqual("- добавлена вкладка Группы состояний;", lines[1]);
        Assert.AreEqual(3, Product("26.11.0.61894", "Pilot-myAdmin Web").Items.Count);
        Assert.IsTrue(Product("25.14.0.57594", "Pilot-ICE Desktop").Items[0].Text.Contains("\n- Назначение состояний"));
    }

    [TestMethod]
    public void SubHeadingListsAreAssignedToTheirProduct()
    {
        // 24.19.0.50468: <li>Pilot-ICE Desktop:</li><ul>...</ul><li>Pilot-ICE Web:</li><ul>...</ul> inside the single row "Pilot-ICE"
        var items = Product("24.19.0.50468", "Pilot-ICE").Items;

        Assert.AreEqual(7, items.Count);
        Assert.AreEqual(4, items.Count(i => i.Product == "Pilot-ICE Desktop"));
        Assert.AreEqual(3, items.Count(i => i.Product == "Pilot-ICE Web"));
        Assert.IsTrue(items.All(i => !i.Text.StartsWith("Pilot-ICE")));
    }

    [TestMethod]
    public void WarningsAreCollectedPerRelease()
    {
        Assert.AreEqual(1, Release("26.11.0.61894").Notices.Count);
        StringAssert.Contains(Release("26.11.0.61894").Notices[0], "созданных в версии 26.9");
        Assert.AreEqual(0, Release("26.13.0.62219").Notices.Count);

        var withList = Release("24.37.0.51703").Notices;
        Assert.AreEqual(2, withList.Count);
        StringAssert.Contains(withList[0], "необходимо: загрузить новую версию приложения; обновить конфигурацию базы данных.");
    }

    [TestMethod]
    public void FixesCarryTheirProductWithoutPrefix()
    {
        var fixes = Release("26.13.0.62219").Fixes;

        Assert.AreEqual(12, fixes.Count);
        Assert.AreEqual(2, fixes.Count(f => f.Product == "Pilot-BIM Desktop"));
        Assert.AreEqual(1, fixes.Count(f => f.Product == "Pilot-ICE Desktop"));
        Assert.IsTrue(fixes[0].Text.StartsWith("Исправлена ошибка \"Related object has no link"));
        Assert.IsTrue(fixes.All(f => !f.Text.StartsWith("Pilot")));
    }

    [TestMethod]
    public void FixesWithoutPrefixAndOutsideSubHeadingsHaveNoProduct()
    {
        Assert.AreEqual(3, Release("24.1.0.49302").Fixes.Count(f => f.Product is null));
        Assert.AreEqual(7, Release("24.4.0.49743").Fixes.Count(f => f.Product is null));
        Assert.AreEqual(0, Release("24.19.0.50468").Fixes.Count(f => f.Product is null)); // here the sub headings name the product
        Assert.AreEqual(2, Release("24.19.0.50468").Fixes.Count(f => f.Product == "Pilot-ICE Desktop"));
    }

    [TestMethod]
    public void ColonInsideFixTextIsNotAProductPrefix()
    {
        var page = Page("""<li>Исправлена ошибка "Access denied: path" при записи (PI#1-1).</li><li>Pilot-ICE Desktop: Исправлено: падение (PI#1-2).</li>""");

        var fixes = ReleaseNotesParser.Parse(page).Single().Fixes;

        Assert.IsNull(fixes[0].Product);
        Assert.AreEqual("Исправлена ошибка \"Access denied: path\" при записи (PI#1-1).", fixes[0].Text);
        Assert.AreEqual("Pilot-ICE Desktop", fixes[1].Product);
        Assert.AreEqual("Исправлено: падение (PI#1-2).", fixes[1].Text);
    }

    [TestMethod]
    public void ListItemWithoutClosingTagEndsAtTheNextOne()
    {
        var fixes = ReleaseNotesParser.Parse(Page("<li>Pilot-BIM Web: a<li>Pilot-BIM Web: b")).Single().Fixes;

        CollectionAssert.AreEqual(new[] { "a", "b" }, fixes.Select(f => f.Text).ToArray());
    }

    [TestMethod]
    public void ForeignPageHasNoReleases()
    {
        Assert.AreEqual(0, ReleaseNotesParser.Parse("<html><h2>Раздел</h2><table><tr><td>x</td></tr></table></html>").Count);
        Assert.AreEqual(0, ReleaseNotesParser.Parse("").Count);
    }

    [TestMethod]
    [DataRow("Pilot-ICE Desktop", "pilot-ice-desktop")]
    [DataRow("Pilot_ICE Desktop", "pilot-ice-desktop")]
    [DataRow("Pilot-BIM-Desktop", "pilot-bim-desktop")]
    [DataRow("  Pilot-ICE   WEB ", "pilot-ice-web")]
    public void ProductNamesTypedByHandAreComparedByKey(string name, string key) => Assert.AreEqual(key, ReleaseNotesParser.ProductKey(name));

    // ---- editions

    [TestMethod]
    public void IceFamilyReadsIceRowsAndBimReadsBimRows()
    {
        var release = Release("26.13.0.62219");

        var ice = EditionNotes.Digest(release, Editions.Get(EditionId.Ice));
        Assert.AreEqual(4, ice.Changes.Count); // the Pilot-ICE Web row (2 items) is not the desktop client
        StringAssert.Contains(ice.Changes[0], "Диалоговые окна");
        Assert.AreEqual(1, ice.Fixes.Count);

        var bim = EditionNotes.Digest(release, Editions.Get(EditionId.Bim));
        Assert.AreEqual(4, bim.Changes.Count);
        StringAssert.Contains(bim.Changes[0], "IFCAdvancedBrep");
        Assert.AreEqual(2, bim.Fixes.Count);

        foreach (var id in new[] { EditionId.IceEnterprise, EditionId.Ecm })
            CollectionAssert.AreEqual(ice.Changes.ToArray(), EditionNotes.Digest(release, Editions.Get(id)).Changes.ToArray());
    }

    [TestMethod]
    public void EarlyReleasesWithOneIceRowKeepOnlyTheDesktopSubList()
    {
        var release = Release("24.19.0.50468");

        var ice = EditionNotes.Digest(release, Editions.Get(EditionId.IceEnterprise));

        Assert.AreEqual(4, ice.Changes.Count);
        Assert.AreEqual(2, ice.Fixes.Count);
        Assert.AreEqual(0, ice.UnattributedFixes);
    }

    [TestMethod]
    public void CombinedFixPrefixOfEarlyReleasesBelongsToTheEdition()
    {
        // 24.41.0.52174 writes "Pilot-ICE:" for all four fixes
        Assert.AreEqual(4, EditionNotes.Digest(Release("24.41.0.52174"), Editions.Get(EditionId.Ice)).Fixes.Count);
        Assert.AreEqual(0, EditionNotes.Digest(Release("24.41.0.52174"), Editions.Get(EditionId.Bim)).Fixes.Count);
    }

    [TestMethod]
    public void FixesWithoutProductAreCountedNotAssigned()
    {
        var digest = EditionNotes.Digest(Release("24.4.0.49743"), Editions.Get(EditionId.Bim));

        Assert.AreEqual(7, digest.UnattributedFixes);
        Assert.IsFalse(digest.Fixes.Any(f => f.Contains("Pilot-Web-Server")));
    }

    [TestMethod]
    public void HandTypedProductNamesStillMatch()
    {
        var page = Page("<li>Pilot_ICE Desktop: a</li><li>Pilot-BIM-Desktop: b</li><li>Pilot-ICE WEB: c</li><li>Pilot-BIM Web: d</li>");
        var release = ReleaseNotesParser.Parse(page).Single();

        CollectionAssert.AreEqual(new[] { "a" }, EditionNotes.Digest(release, Editions.Get(EditionId.Ice)).Fixes.ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, EditionNotes.Digest(release, Editions.Get(EditionId.Bim)).Fixes.ToArray());
    }

    // ---- versions

    [TestMethod]
    public void CatalogVersionFindsItsNotesExactly()
    {
        var match = EditionNotes.Find(All, Version.Parse("26.11.0.61894"));

        Assert.IsNotNull(match);
        Assert.IsTrue(match.Exact);
        Assert.AreEqual(Version.Parse("26.11.0.61894"), match.Notes.Version);
    }

    [TestMethod]
    public void Release2440IsFoundByItsFirstNumbers()
    {
        // the download center has 24.40.0.51967, the notes say 24.40.0.52162
        var match = EditionNotes.Find(All, Version.Parse("24.40.0.51967"));

        Assert.IsNotNull(match);
        Assert.IsFalse(match.Exact);
        Assert.AreEqual(Version.Parse("24.40.0.52162"), match.Notes.Version);
    }

    [TestMethod]
    public void VersionWithoutNotesIsNotGuessed()
    {
        Assert.IsNull(EditionNotes.Find(All, Version.Parse("15.0.11.14613"))); // before the first release of the page
        Assert.IsNull(EditionNotes.Find(All, Version.Parse("24.5.0.49800"))); // no 24.5 on the page

        // the first three numbers decide when they match one release; the first two alone only when they match exactly one
        var twoMinor = new[] { Release("24.40.0.52162") with { Version = new Version(24, 40, 0, 1) }, Release("24.40.0.52162") with { Version = new Version(24, 40, 1, 2) } };
        Assert.AreEqual(new Version(24, 40, 0, 1), EditionNotes.Find(twoMinor, Version.Parse("24.40.0.51967"))!.Notes.Version);
        Assert.IsNull(EditionNotes.Find(twoMinor, Version.Parse("24.40.2.5")));
    }

    [TestMethod]
    public void RangeTakesReleasesAfterTheOlderVersionUpToTheNewerOne()
    {
        var versions = EditionNotes.Between(All, Version.Parse("24.40.0.52162"), Version.Parse("25.14.0.57594")).Select(r => r.Version.ToString());

        CollectionAssert.AreEqual(new[] { "25.14.0.57594", "24.41.0.52174" }, versions.ToArray());
    }

    [TestMethod]
    public void RangeDoesNotDependOnTheOrderOfTheArguments()
    {
        var a = EditionNotes.Between(All, Version.Parse("24.4.0.49743"), Version.Parse("24.37.0.51703")).Select(r => r.Version);
        var b = EditionNotes.Between(All, Version.Parse("24.37.0.51703"), Version.Parse("24.4.0.49743")).Select(r => r.Version);

        CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
        CollectionAssert.AreEqual(new[] { "24.37.0.51703", "24.19.0.50468" }, a.Select(v => v.ToString()).ToArray());
    }

    [TestMethod]
    public void RangeBoundsFollowTheMismatchOf2440()
    {
        // from 24.40.0.51967 (catalog) means from 24.40.0.52162 (notes): release 24.40 itself is not "after" it
        var after = EditionNotes.Between(All, Version.Parse("24.40.0.51967"), Version.Parse("24.41.0.52174")).Select(r => r.Version.ToString());
        // up to 24.40.0.51967 includes the notes of 24.40
        var upTo = EditionNotes.Between(All, Version.Parse("24.37.0.51703"), Version.Parse("24.40.0.51967")).Select(r => r.Version.ToString());

        CollectionAssert.AreEqual(new[] { "24.41.0.52174" }, after.ToArray());
        CollectionAssert.AreEqual(new[] { "24.40.0.52162" }, upTo.ToArray());
    }

    [TestMethod]
    public void RangeFromAVersionOlderThanThePageStartsAtTheFirstRelease()
    {
        var versions = EditionNotes.Between(All, Version.Parse("15.0.11.14613"), Version.Parse("24.4.0.49743")).Select(r => r.Version.ToString());

        CollectionAssert.AreEqual(new[] { "24.4.0.49743", "24.1.0.49302" }, versions.ToArray());
        Assert.AreEqual(0, EditionNotes.Between(All, Version.Parse("24.4.0.49743"), Version.Parse("24.4.0.49743")).Count);
    }

    // ---- download and saved copy

    private const string OneRelease = "<h2>Версия 26.13.0.62219 от 07.07.2026</h2><table><tr><td><p class=\"CellBody\">Pilot-ICE Desktop</p></td><td><ul><li>x</li></ul></td></tr></table>";

    private static HttpResponseMessage Ok(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    private static ReleaseNotesCatalog Catalog(string cache, Func<HttpRequestMessage, HttpResponseMessage> handler, out List<Uri?> requests)
    {
        var log = new List<Uri?>();
        requests = log;
        var fake = new FakeFileHandler([]) { Override = r => { log.Add(r.RequestUri); return handler(r); } };
        return new ReleaseNotesCatalog(new HttpClient(fake), cache);
    }

    [TestMethod]
    public async Task LoadDownloadsThePageAndSavesTheCopy()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            var catalog = Catalog(cache, _ => Ok(Sample), out var requests);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(9, result.Releases.Count);
            Assert.IsNull(result.Error);
            Assert.AreEqual("https://help.pilotems.com/ru/Content/RelNotes.htm", requests.Single()!.ToString());
            Assert.AreEqual(Sample, File.ReadAllText(cache));
            Assert.IsFalse(File.Exists(cache + ".tmp"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task CopyYoungerThanADayIsUsedWithoutAskingTheServer()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromHours(23));
            var catalog = Catalog(cache, _ => throw new HttpRequestException("must not be called"), out var requests);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(9, result.Releases.Count);
            Assert.IsNull(result.Error);
            Assert.AreEqual(0, requests.Count);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task CopyOlderThanADayIsReplacedByTheDownload()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromHours(25));
            var catalog = Catalog(cache, _ => Ok(OneRelease), out var requests);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(1, result.Releases.Count);
            Assert.IsNull(result.Error);
            Assert.AreEqual(OneRelease, File.ReadAllText(cache));
            Assert.IsTrue(DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromMinutes(5));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task OldCopyIsShownWithTheReasonWhenTheServerIsUnreachable()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            var saved = DateTime.UtcNow - TimeSpan.FromDays(3);
            File.SetLastWriteTimeUtc(cache, saved);
            var catalog = Catalog(cache, _ => throw new HttpRequestException("no network"), out _);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(9, result.Releases.Count);
            Assert.AreEqual("no network", result.Error);
            Assert.AreEqual(saved, result.FetchedUtc);
            Assert.AreEqual(saved, File.GetLastWriteTimeUtc(cache)); // a failed attempt does not make the copy look fresh
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task UntrustedCertificateIsExplainedNotJustReported()
    {
        // help.pilotems.com is served with a Russian Trusted Root CA certificate, which Windows does not trust by default
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromDays(2));
            var tls = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid: UntrustedRoot");
            var catalog = Catalog(cache, _ => throw new HttpRequestException("The SSL connection could not be established", tls), out _);

            var result = await catalog.LoadAsync(CancellationToken.None);

            StringAssert.Contains(result.Error, "UntrustedRoot");
            StringAssert.Contains(result.Error, "Минцифры");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task LoadFailsWhenOfflineWithoutACopy()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var catalog = Catalog(Path.Combine(dir, "notes.html"), _ => throw new HttpRequestException("no network"), out _);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => catalog.LoadAsync(CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ServerTimeoutIsTreatedLikeAnUnreachableServer()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromDays(2));
            var catalog = Catalog(cache, _ => throw new TaskCanceledException("timed out"), out _);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(9, result.Releases.Count);
            Assert.AreEqual("timed out", result.Error);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task PageWithoutReleasesDoesNotReplaceTheCopy()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, Sample);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromDays(2));
            var catalog = Catalog(cache, _ => Ok("<html>maintenance</html>"), out _);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(9, result.Releases.Count);
            StringAssert.Contains(result.Error, "no release notes");
            Assert.AreEqual(Sample, File.ReadAllText(cache));

            File.Delete(cache);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.LoadAsync(CancellationToken.None));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task UnreadableCopyIsIgnoredEvenWhenYoung()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "notes.html");
            await File.WriteAllTextAsync(cache, "<html>half a page");
            var catalog = Catalog(cache, _ => Ok(Sample), out var requests);

            var result = await catalog.LoadAsync(CancellationToken.None);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(9, result.Releases.Count);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    private static string Page(string fixItems) =>
        $"""<h2>Версия 26.1.0.1 от 01.01.2026</h2><table><tbody><tr><td><p class="CellBody">Исправлены ошибки</p></td><td><ul>{fixItems}</ul></td></tr></tbody></table>""";
}
