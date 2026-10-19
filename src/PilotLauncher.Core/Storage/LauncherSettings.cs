using System.Text.Json;

namespace PilotLauncher.Core.Storage;

public sealed class LauncherSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string StoreRoot { get; set; } = VersionStore.DefaultRoot;

    /// <summary>Folder with setup zips/exes; its versions are offered without the internet. Null when not set.</summary>
    public string? SetupFolder { get; set; }

    /// <summary>
    /// The user allowed the launcher to trust the Russian Trusted Root CA for help.pilotems.com (release notes). Only the
    /// launcher is affected, nothing is installed in Windows.
    /// </summary>
    public bool TrustRussianRoot { get; set; }

    /// <summary>The version list and the notes come from the pre-release folder (<c>/beta/</c>) instead of the releases.</summary>
    public bool Prerelease { get; set; }

    /// <summary>At start the launcher asks GitHub whether a newer launcher was released. On unless the user turned it off.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>A missing file means defaults; a damaged file is an error, not silently reset.</summary>
    public static LauncherSettings Load(string path)
    {
        if (!File.Exists(path))
            return new LauncherSettings();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LauncherSettings>(stream, Json)
               ?? throw new InvalidDataException($"Empty settings file: {path}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
