using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using PilotLauncher.Core.Net;
using System.Security.Principal;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Launch;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Notes;
using PilotLauncher.Core.Sharing;
using PilotLauncher.Core.Storage;
using PilotLauncher.Core.Update;

namespace PilotLauncher.App;

/// <summary>Composition root: everything the windows and the command line modes share.</summary>
public sealed class AppServices
{
    public required LauncherPaths Paths { get; init; }
    public required LauncherSettings Settings { get; init; }
    public required ILauncherLog Log { get; init; }
    public required ReleaseCatalog Catalog { get; init; }
    public required ReleaseNotesCatalog Notes { get; init; }
    public required ReleaseCatalog BetaCatalog { get; init; }
    public required ReleaseNotesCatalog BetaNotes { get; init; }
    public required VersionLibrary Library { get; init; }
    public required LaunchService Launch { get; init; }
    public required SystemInstaller SystemInstaller { get; init; }
    public required PinnedRootTrust RussianRoot { get; init; }
    public required ExtensionService Extensions { get; init; }
    public required LauncherUpdater Updater { get; init; }
    public required SharedFolderService SharedFolders { get; init; }

    public static string ExeDirectory => AppContext.BaseDirectory;

    public static AppServices Create()
    {
        var paths = LauncherPaths.Resolve(ExeDirectory);
        var settings = LauncherSettings.Load(paths.SettingsFile);
        var log = new FileLog(paths.LogsDir);
        var store = new VersionStore(settings.StoreRoot);

        var russianRoot = PinnedRootTrust.LoadRussianRoot();
        var http = CreateHttpClient(russianRoot.CreateCallback(() => settings.TrustRussianRoot));
        var catalog = new ReleaseCatalog(http, paths.CatalogCacheFile);
        var notes = new ReleaseNotesCatalog(http, paths.NotesCacheFile);
        var betaCatalog = new ReleaseCatalog(http, paths.BetaCatalogCacheFile, ReleaseCatalog.BetaBaseUri);
        var betaNotes = new ReleaseNotesCatalog(http, paths.BetaNotesCacheFile, ReleaseNotesCatalog.BetaPageUri);
        var installer = new VersionInstaller(store, catalog, new FileDownloader(http), new DiskSpace(), log);
        var credentials = new CredentialStore(paths.CredentialsFile, new DpapiProtector());
        var accountService = new LocalAccountService();
        var provisioner = new ElevatedAccountProvisioner(accountService, credentials, paths.CredentialsFile, log);
        var ownerTag = AccountNaming.OwnerTag(WindowsIdentity.GetCurrent().User!.Value);
        Func<Edition, Version, string> accountName = (e, v) => AccountNaming.UserName(ownerTag, e, v);

        var detector = new SystemInstallDetector(new AsconRegistry());
        var library = new VersionLibrary(store, detector, installer, provisioner, accountName, log);
        var launch = new LaunchService(provisioner, credentials, new ClientProcessStarter(), new Win32WindowApi(), ownerTag, log);
        var systemInstaller = new SystemInstaller(detector, new ClientProcesses(), catalog, new FileDownloader(http), new DiskSpace(),
            ElevatedRunner.RunAsync, SystemInstaller.SetupRoot(paths.DataDir), log);
        var extensions = new ExtensionService(store, new ExtensionStore(store.ExtensionsDir, log), new GitHubReleaseClient(http, paths.DataDir),
            new FileDownloader(http), new DiskSpace(), new ElevatedProfileLinker(store, provisioner, log),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ownerTag, accountName, log);
        var sharedFolders = new SharedFolderService(ownerTag, credentials, paths.DataDir, ElevatedRunner.RunAsync, log);
        var updater = new LauncherUpdater(new GitHubReleaseClient(http, paths.DataDir), new FileDownloader(http), log);
        return new AppServices { Paths = paths, Settings = settings, Log = log, Catalog = catalog, Notes = notes, BetaCatalog = betaCatalog, BetaNotes = betaNotes, Library = library, Launch = launch, SystemInstaller = systemInstaller, RussianRoot = russianRoot, Extensions = extensions, Updater = updater, SharedFolders = sharedFolders };
    }

    private static HttpClient CreateHttpClient(RemoteCertificateValidationCallback certificateValidation)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials, // corporate proxies with Windows sign-in
            ConnectTimeout = TimeSpan.FromSeconds(20),
            SslOptions = { RemoteCertificateValidationCallback = certificateValidation },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PilotLauncher", "1.0"));
        return http;
    }
}
