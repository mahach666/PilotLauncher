namespace PilotLauncher.Core.Extensions;

/// <summary>An extension published as a zip in the releases of a GitHub repository.</summary>
/// <param name="Id">Lower-case name of the folder in the shared store.</param>
/// <param name="AssetName">Name of the release file, e.g. <c>PilotLookUp.zip</c>.</param>
/// <param name="FolderName">Name of the extension's folder inside Pilot's <c>Development</c> folder.</param>
public sealed record ExtensionSource(string Id, string DisplayName, string Repo, string AssetName, string FolderName, string Description)
{
    public static readonly IReadOnlyList<ExtensionSource> Defaults =
    [
        new("pilotlookup", "PilotLookUp", "mahach666/PilotLookUp", "PilotLookUp.zip", "PilotLookUp",
            "Просмотр объектов Pilot для разработчиков и администраторов"),
        new("dynamopilot", "DynamoPilot", "mahach666/DynamoPilot", "DynamoPilot.App.zip", "DynamoPilot.App",
            "Dynamo внутри Pilot: узлы для работы с объектами Pilot"),
    ];

    public Uri ReleasesPage => new($"https://github.com/{Repo}/releases");
}
