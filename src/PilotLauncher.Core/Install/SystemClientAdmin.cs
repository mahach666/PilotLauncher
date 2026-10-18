using System.Diagnostics;
using System.Text.RegularExpressions;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

public interface IProcessRunner
{
    /// <summary>Starts the program without a shell, waits for it and returns the exit code.</summary>
    int Run(string fileName, string arguments);
}

public sealed class ProcessRunner : IProcessRunner
{
    public int Run(string fileName, string arguments)
    {
        var info = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(fileName)}.");
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>
/// The elevated half of "install into the system" (<c>--admin replace-client</c>). It accepts an edition, a version and
/// the path of one staged setup, and works out the rest itself: what is installed, which record removes it. It cannot be
/// asked to run any other program.
/// </summary>
public sealed class SystemClientAdmin
{
    /// <summary>Success, "restart started" and "restart required": all three mean the package is in place.</summary>
    private static readonly int[] SuccessCodes = [0, 1641, 3010];

    private static readonly Regex StagingFolder = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly ISystemInstalls _installs;
    private readonly IUninstallRegistry _uninstall;
    private readonly IProcessRunner _runner;
    private readonly string _setupRoot;
    private readonly string _logsDir;
    private readonly string _packageCacheRoot;

    public SystemClientAdmin(
        ISystemInstalls installs, IUninstallRegistry uninstall, IProcessRunner runner,
        string setupRoot, string logsDir, string? packageCacheRoot = null)
    {
        _installs = installs;
        _uninstall = uninstall;
        _runner = runner;
        _setupRoot = Path.GetFullPath(setupRoot);
        _logsDir = logsDir;
        _packageCacheRoot = packageCacheRoot
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
    }

    public static bool TryParseEdition(string text, out Edition edition)
    {
        var known = Enum.TryParse<EditionId>(text, ignoreCase: false, out var id) && Enum.IsDefined(id);
        edition = known ? Editions.Get(id) : null!;
        return known;
    }

    /// <summary>Only <c>&lt;setup root&gt;\&lt;32 hex digits&gt;\&lt;edition setup name&gt;.exe</c> in its plain, normalised form.</summary>
    public bool IsAllowedSetup(string path, Edition edition)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            return false;
        var root = _setupRoot.TrimEnd('\\') + '\\';
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = path[root.Length..].Split('\\');
        return parts.Length == 2
               && StagingFolder.IsMatch(parts[0])
               && string.Equals(parts[1], edition.SetupPrefix + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    public void Replace(Edition edition, Version target, string setupPath)
    {
        if (!IsAllowedSetup(setupPath, edition))
            throw new AdminOperationException($"Файл '{setupPath}' не лежит в рабочей папке установщиков лаунчера; отказ.");
        if (!File.Exists(setupPath))
            throw new AdminOperationException($"Установщик не найден: {setupPath}.");

        var installed = _installs.Detect().FirstOrDefault(i => i.Edition.Id == edition.Id);
        switch (SystemClientPlan.Choose(installed?.Version, target))
        {
            case SystemClientAction.AlreadyInstalled:
                throw new AdminOperationException($"{edition.DisplayName} {target} уже установлена в системе.");
            case SystemClientAction.UninstallThenInstall:
                Uninstall(edition, installed!.Version);
                break;
        }

        var log = LogPath("install", edition, target);
        Succeeded(_runner.Run(setupPath, $"/quiet /norestart /log \"{log}\""), "Установщик", log);
    }

    private void Uninstall(Edition edition, Version installed)
    {
        var candidates = ClientUninstallLocator.Find(_uninstall.Entries(), edition, installed, _packageCacheRoot);
        var log = LogPath("uninstall", edition, installed);
        var code = candidates.OfType<BundleUninstall>().FirstOrDefault(b => File.Exists(b.ExePath)) is { } bundle
            ? _runner.Run(bundle.ExePath, $"/uninstall /quiet /norestart /log \"{log}\"")
            : candidates.OfType<MsiUninstall>().FirstOrDefault() is { } msi
                ? _runner.Run(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {msi.ProductCode} /qn /norestart /l*v \"{log}\"")
                : throw new AdminOperationException(
                    $"В списке установленных программ нет записи об удалении {edition.DisplayName} {installed}. Удалите её вручную и повторите.");
        Succeeded(code, $"Удаление {edition.DisplayName} {installed}", log);
    }

    private string LogPath(string kind, Edition edition, Version version) =>
        Path.Combine(_logsDir, $"system-{kind}-{edition.Id}-{version}.log");

    private static void Succeeded(int exitCode, string what, string log)
    {
        if (!SuccessCodes.Contains(exitCode))
            throw new AdminOperationException($"{what} завершилось с кодом {exitCode}. Журнал установщика: {log} (рядом лежат журналы MSI).");
    }
}
