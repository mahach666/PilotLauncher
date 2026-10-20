using System.Windows;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.App;

/// <summary><c>PilotLauncher.exe --launch &lt;EditionId&gt; &lt;version&gt;</c>: what the desktop shortcuts run.</summary>
public static class HeadlessLaunch
{
    public static int Run(string[] args)
    {
        AppServices? services = null;
        try
        {
            if (args.Length != 2 || !Enum.TryParse<EditionId>(args[0], out var editionId) || !Version.TryParse(args[1], out var version))
                throw new ArgumentException("Usage: --launch <IceEnterprise|Ice|Bim|Ecm|MyAdmin> <version>");

            services = AppServices.Create();
            var entry = services.Library.Build([]).FirstOrDefault(e => e.Edition.Id == editionId && e.Version == version)
                        ?? throw new InvalidOperationException(
                            $"{Editions.Get(editionId).DisplayName} {version} is not installed any more. Open Pilot Launcher to download it again.");

            var client = entry.System is { } system
                ? services.Launch.LaunchSystem(system)
                : services.Launch.LaunchIsolatedAsync(entry.Stored!, CancellationToken.None).GetAwaiter().GetResult();

            // stay alive while the client runs: the title label is kept up to date by this process
            client.Process.WaitForExit();
            return 0;
        }
        catch (Exception ex)
        {
            services?.Log.Error("--launch failed", ex);
            MessageBox.Show(ex.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
