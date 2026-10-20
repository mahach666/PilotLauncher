using System.Diagnostics;
using System.Windows;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Sharing;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.App;

/// <summary>
/// One executable, several modes: the normal window, <c>--admin</c> (elevated helper, no window),
/// <c>--launch</c> (start one version from a shortcut), <c>--install</c> / <c>--uninstall</c> (per-user setup),
/// and a copy named <c>*-Setup.exe</c> that installs itself.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--admin":
                var paths = LauncherPaths.Resolve(AppServices.ExeDirectory);
                var systemClient = new SystemClientAdmin(new SystemInstallDetector(new AsconRegistry()), new UninstallRegistry(), new ProcessRunner(),
                    SystemInstaller.SetupRoot(paths.DataDir), paths.LogsDir);
                var localAccounts = new LocalAccountService();
                return AdminCommands.Run(args.Skip(1).ToArray(), localAccounts, systemClient, new ProfileDevelopmentAdmin(new ProfileLocator()),
                    new SharedFolderAdmin(localAccounts, localAccounts, paths.DataDir));
            case "--launch":
                return HeadlessLaunch.Run(args.Skip(1).ToArray());
            case "--install":
                return SelfInstaller.Run(install: true, quiet: args.Contains("--quiet"));
            case "--uninstall":
                return SelfInstaller.Run(install: false, quiet: args.Contains("--quiet"));
            case null when SelfInstaller.IsSetupName(Environment.ProcessPath):
                var result = SelfInstaller.Run(install: true, quiet: true);
                if (result == 0)
                    Process.Start(new ProcessStartInfo(SelfInstaller.InstalledExe) { UseShellExecute = true });
                else
                    MessageBox.Show("Не удалось установить Pilot Launcher. Подробности: запустите установщик с параметром --install.",
                        "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                return result;
            case null:
                break;
            default:
                MessageBox.Show($"Неизвестный параметр: {args[0]}", "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 2;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
