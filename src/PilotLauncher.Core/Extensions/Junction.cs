using System.Diagnostics;

namespace PilotLauncher.Core.Extensions;

/// <summary>
/// Directory junctions: a folder that is another folder. Unlike symbolic links they can be created without administrator
/// rights, and Windows programs open them as ordinary folders.
/// </summary>
public static class Junction
{
    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Where the junction points, or null when <paramref name="path"/> is not a link.</summary>
    public static string? Target(string path) => IsLink(path) ? new DirectoryInfo(path).LinkTarget : null;

    public static void Create(string link, string target)
    {
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"The junction target does not exist: {target}");
        if (Path.Exists(link))
            throw new IOException($"{link} already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start cmd.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"Could not link {link} to {target}: {output.Trim()}");
    }

    /// <summary>
    /// Deletes a folder with everything in it, but removes junctions inside as links without entering them.
    /// <c>Directory.Delete(path, recursive: true)</c> cannot do this: it fails with "access denied" on a junction.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        if (IsLink(path))
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path).ToList())
            DeleteTree(directory);
        foreach (var file in Directory.EnumerateFiles(path).ToList())
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        Directory.Delete(path, recursive: false);
    }

    /// <summary>Removes the link only; the folder it points to is not touched.</summary>
    public static void Remove(string link)
    {
        if (!IsLink(link))
            throw new IOException($"{link} is not a link; refusing to delete it.");
        Directory.Delete(link, recursive: false);
    }
}
