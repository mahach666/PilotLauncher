using System.Text.Json;

namespace PilotLauncher.Core.Storage;

public enum VersionSource
{
    /// <summary>Downloaded from the download center.</summary>
    Catalog,

    /// <summary>Copied from a system-wide installation.</summary>
    SystemCopy,

    /// <summary>Unpacked from a setup zip or exe the user had on disk.</summary>
    LocalFile,
}

public sealed record VersionManifest(string ExeRelativePath, VersionSource Source, DateTime InstalledUtc)
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static VersionManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<VersionManifest>(stream, Json)
               ?? throw new InvalidDataException($"Empty manifest: {path}");
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
