using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class FavoritesTests
{
    private static readonly Version V13 = new(26, 13, 0, 62219);

    [TestMethod]
    public void AVersionIsAFavoriteOnlyForItsOwnEdition()
    {
        var settings = new LauncherSettings();
        Assert.IsFalse(settings.IsFavorite(EditionId.Bim, V13));

        settings.SetFavorite(EditionId.Bim, V13, true);

        Assert.IsTrue(settings.IsFavorite(EditionId.Bim, V13));
        Assert.IsFalse(settings.IsFavorite(EditionId.Ice, V13), "the same number in another edition is another version");
        Assert.IsFalse(settings.IsFavorite(EditionId.Bim, new Version(26, 13, 0, 62220)));
    }

    [TestMethod]
    public void AddingTwiceKeepsOneEntryAndRemovingForgetsIt()
    {
        var settings = new LauncherSettings();

        settings.SetFavorite(EditionId.MyAdmin, V13, true);
        settings.SetFavorite(EditionId.MyAdmin, V13, true);
        Assert.AreEqual(1, settings.Favorites.Count);

        settings.SetFavorite(EditionId.MyAdmin, V13, false);
        settings.SetFavorite(EditionId.MyAdmin, V13, false); // already gone: no error
        Assert.AreEqual(0, settings.Favorites.Count);
        Assert.IsFalse(settings.IsFavorite(EditionId.MyAdmin, V13));
    }

    [TestMethod]
    public void FavoritesSurviveSavingAndLoading()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var settings = new LauncherSettings();
            settings.SetFavorite(EditionId.IceEnterprise, V13, true);
            settings.SetFavorite(EditionId.MyAdmin, new Version(26, 9, 0, 61368), true);
            settings.Save(path);

            var loaded = LauncherSettings.Load(path);

            Assert.IsTrue(loaded.IsFavorite(EditionId.IceEnterprise, V13));
            Assert.IsTrue(loaded.IsFavorite(EditionId.MyAdmin, new Version(26, 9, 0, 61368)));
            Assert.IsFalse(loaded.IsFavorite(EditionId.Ice, V13));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void SettingsFilesOfEarlierVersionsHaveNoFavorites()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "StoreRoot": "D:\\Pilot", "SetupFolder": null, "TrustRussianRoot": false, "Prerelease": true, "CheckUpdates": false }""");

            var loaded = LauncherSettings.Load(path);

            Assert.AreEqual(0, loaded.Favorites.Count);
            Assert.IsFalse(loaded.CheckUpdates, "the rest of the file is read as before");
            loaded.SetFavorite(EditionId.Bim, V13, true); // the list is usable, not null
            Assert.IsTrue(loaded.IsFavorite(EditionId.Bim, V13));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    // ---- the version the notes are compared with, independent of the list order (favorites come first)

    private static Version[] List(params string[] versions) => versions.Select(Version.Parse).ToArray();

    [TestMethod]
    public void ComparisonPartnerIsTheNextOlderVersionWhateverTheOrderOfTheList()
    {
        var newestFirst = List("26.13.0.1", "26.11.0.1", "26.9.0.1", "25.14.0.1");
        var favoriteFirst = List("26.9.0.1", "26.13.0.1", "26.11.0.1", "25.14.0.1");

        Assert.AreEqual(new Version(26, 11, 0, 1), VersionLibrary.ComparisonPartner(newestFirst, new Version(26, 13, 0, 1)));
        Assert.AreEqual(new Version(26, 11, 0, 1), VersionLibrary.ComparisonPartner(favoriteFirst, new Version(26, 13, 0, 1)));
        Assert.AreEqual(new Version(25, 14, 0, 1), VersionLibrary.ComparisonPartner(favoriteFirst, new Version(26, 9, 0, 1)));
    }

    [TestMethod]
    public void ComparisonPartnerOfTheOldestIsTheNextNewerAndOfALoneVersionNothing()
    {
        Assert.AreEqual(new Version(26, 9, 0, 1), VersionLibrary.ComparisonPartner(List("26.13.0.1", "26.9.0.1", "25.14.0.1"), new Version(25, 14, 0, 1)));
        Assert.IsNull(VersionLibrary.ComparisonPartner(List("26.13.0.1"), new Version(26, 13, 0, 1)));
        Assert.IsNull(VersionLibrary.ComparisonPartner([], new Version(26, 13, 0, 1)));
    }
}
