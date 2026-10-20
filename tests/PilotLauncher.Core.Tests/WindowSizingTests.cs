using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class WindowSizingTests
{
    private const double MinW = 700, MinH = 470;

    [TestMethod]
    public void WithoutASavedSizeTheWindowOpensWithTheLargerDefault()
    {
        var (w, h) = WindowSizing.Choose(null, null, 1920, 1040, MinW, MinH);

        Assert.AreEqual(WindowSizing.DefaultWidth, w);
        Assert.AreEqual(WindowSizing.DefaultHeight, h);
        Assert.IsTrue(w > 860 && h > 540, "larger than the old fixed 860x540");
    }

    [TestMethod]
    public void ASavedSizeIsUsedAsItIs()
    {
        var (w, h) = WindowSizing.Choose(1500, 900, 2000, 1200, MinW, MinH);

        Assert.AreEqual((1500d, 900d), (w, h));
    }

    [TestMethod]
    public void ASizeLargerThanTheScreenIsPulledInsideIt()
    {
        // saved on a bigger monitor, started on a smaller one
        Assert.AreEqual((1280d, 680d), WindowSizing.Choose(3000, 2000, 1280, 680, MinW, MinH));
        // the default is cut as well on a small screen
        Assert.AreEqual((1024d, 728d), WindowSizing.Choose(null, null, 1024, 728, MinW, MinH));
    }

    [TestMethod]
    public void TheWindowNeverGetsSmallerThanItsMinimum()
    {
        Assert.AreEqual((MinW, MinH), WindowSizing.Choose(100, 100, 1920, 1040, MinW, MinH), "a tiny saved size");
        Assert.AreEqual((MinW, MinH), WindowSizing.Choose(null, null, 600, 400, MinW, MinH), "a screen smaller than the minimum");
    }

    [TestMethod]
    [DataRow(null, 900.0)]
    [DataRow(1500.0, null)]
    [DataRow(0.0, 0.0)]
    [DataRow(-5.0, 900.0)]
    [DataRow(double.NaN, 900.0)]
    [DataRow(double.PositiveInfinity, 900.0)]
    public void ABrokenOrHalfSavedSizeFallsBackToTheDefault(double? width, double? height) =>
        Assert.AreEqual((WindowSizing.DefaultWidth, WindowSizing.DefaultHeight), WindowSizing.Choose(width, height, 2000, 1200, MinW, MinH));

    [TestMethod]
    public void TheSizeAndTheMaximizedFlagSurviveSavingAndOlderFilesHaveNone()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "StoreRoot": "D:\\Pilot", "Prerelease": true }""");
            var old = LauncherSettings.Load(path);
            Assert.IsNull(old.WindowWidth);
            Assert.IsNull(old.WindowHeight);
            Assert.IsFalse(old.WindowMaximized);

            old.WindowWidth = 1234.5;
            old.WindowHeight = 777;
            old.WindowMaximized = true;
            old.Save(path);
            var loaded = LauncherSettings.Load(path);

            Assert.AreEqual(1234.5, loaded.WindowWidth);
            Assert.AreEqual(777d, loaded.WindowHeight);
            Assert.IsTrue(loaded.WindowMaximized);
            Assert.IsTrue(loaded.Prerelease, "the other settings are kept");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }
}
