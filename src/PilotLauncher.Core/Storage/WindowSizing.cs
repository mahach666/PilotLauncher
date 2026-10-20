namespace PilotLauncher.Core.Storage;

/// <summary>Which size the main window opens with: the one the user left it at, else a default, always inside the screen.</summary>
public static class WindowSizing
{
    public const double DefaultWidth = 1100;
    public const double DefaultHeight = 780;

    /// <param name="savedWidth">The width saved when the window was last closed; null when there is none.</param>
    /// <param name="areaWidth">Work area of the screen (without the taskbar), in the units of the window.</param>
    /// <param name="minWidth">The window never gets smaller than this, even when the screen is.</param>
    public static (double Width, double Height) Choose(
        double? savedWidth, double? savedHeight, double areaWidth, double areaHeight, double minWidth, double minHeight)
    {
        // a half-written or hand-edited value is not used; both numbers come from the same place
        var saved = IsUsable(savedWidth) && IsUsable(savedHeight);
        var width = saved ? savedWidth!.Value : DefaultWidth;
        var height = saved ? savedHeight!.Value : DefaultHeight;
        return (Fit(width, areaWidth, minWidth), Fit(height, areaHeight, minHeight));
    }

    private static bool IsUsable(double? value) => value is > 0 and < 100_000;

    private static double Fit(double size, double area, double min) => Math.Clamp(size, min, Math.Max(min, area));
}
