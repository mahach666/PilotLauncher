namespace PilotLauncher.Core.Extensions;

public enum LinkState
{
    None,

    /// <summary>A junction into the shared extension store.</summary>
    Ours,

    /// <summary>A real folder, a file, or a link that points elsewhere.</summary>
    Foreign,
}

public sealed record LinkInfo(LinkState State, string Path, string? Id, string? Tag);

/// <summary>The entries of a Development folder: which ones the launcher made (links into its store) and which are not its business.</summary>
public static class ExtensionLinks
{
    public static LinkInfo Inspect(string developmentDir, ExtensionSource source, ExtensionStore store)
    {
        var path = Path.Combine(developmentDir, source.FolderName);
        if (!Path.Exists(path))
            return new LinkInfo(LinkState.None, path, null, null);
        if (Junction.IsLink(path) && store.TryParseContentPath(Junction.Target(path), out var id, out var tag) && id == source.Id)
            return new LinkInfo(LinkState.Ours, path, id, tag);
        return new LinkInfo(LinkState.Foreign, path, null, null);
    }

    /// <summary>Deletes the link if it is one of ours; anything else is left alone.</summary>
    public static void Remove(string developmentDir, ExtensionSource source, ExtensionStore store)
    {
        var info = Inspect(developmentDir, source, store);
        if (info.State == LinkState.Ours)
            Junction.Remove(info.Path);
    }
}
