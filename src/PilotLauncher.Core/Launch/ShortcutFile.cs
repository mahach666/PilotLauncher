namespace PilotLauncher.Core.Launch;

public static class ShortcutFile
{
    public static void Create(string shortcutPath, string target, string arguments, string iconPath, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.IconLocation = iconPath;
            link.Description = description;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>The command line stored in desktop shortcuts: <c>PilotLauncher.exe --launch &lt;edition&gt; &lt;version&gt;</c>.</summary>
    public static string LaunchArguments(Catalog.Edition edition, Version version) => $"--launch {edition.Id} {version}";
}
