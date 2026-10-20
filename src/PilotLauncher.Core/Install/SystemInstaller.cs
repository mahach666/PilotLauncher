using System.Diagnostics;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Install;

public sealed class ClientRunningException : InvalidOperationException
{
    public ClientRunningException(Edition edition)
        : base($"Pilot {edition.DisplayName} сейчас запущен. Закройте все его окна и повторите.") { }
}

public interface IClientProcesses
{
    /// <summary>True when any process of the edition's client runs, under any user.</summary>
    bool IsRunning(Edition edition);
}

public sealed class ClientProcesses : IClientProcesses
{
    public bool IsRunning(Edition edition)
    {
        foreach (var exe in edition.ExeCandidates)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            var running = processes.Length > 0;
            foreach (var process in processes)
                process.Dispose();
            if (running)
                return true;
        }

        return false;
    }
}

/// <summary>Where the setup comes from: the download center (fetched again, the launcher keeps no setups) or a file of the user.</summary>
public sealed class SetupSource
{
    private SetupSource(ReleaseEntry? release, string? filePath)
    {
        Release = release;
        FilePath = filePath;
    }

    public ReleaseEntry? Release { get; }

    /// <summary>A zip from the download center or its setup exe. The file is only read, never changed or deleted.</summary>
    public string? FilePath { get; }

    public static SetupSource Download(ReleaseEntry release) => new(release, null);

    public static SetupSource LocalFile(string path) => new(null, path);
}

/// <summary>
/// Installs a version into the system with the real setup, replacing the installed client of the same edition. The setup
/// is staged in the launcher's own folder, run by the elevated copy of the launcher (<see cref="SystemClientAdmin"/>) and
/// deleted afterwards.
/// </summary>
public sealed class SystemInstaller
{
    private readonly ISystemInstalls _installs;
    private readonly IClientProcesses _processes;
    private readonly ReleaseCatalog _catalog;
    private readonly FileDownloader _downloader;
    private readonly DiskSpace _disk;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _runElevated;
    private readonly string _setupRoot;
    private readonly ILauncherLog _log;

    public SystemInstaller(
        ISystemInstalls installs, IClientProcesses processes, ReleaseCatalog catalog, FileDownloader downloader, DiskSpace disk,
        Func<IReadOnlyList<string>, CancellationToken, Task> runElevated, string setupRoot, ILauncherLog log)
    {
        _installs = installs;
        _processes = processes;
        _catalog = catalog;
        _downloader = downloader;
        _disk = disk;
        _runElevated = runElevated;
        _setupRoot = setupRoot;
        _log = log;
    }

    /// <summary>Staging folder of setups for one launcher data folder; the elevated side accepts files from here only.</summary>
    public static string SetupRoot(string dataDir) => Path.Combine(dataDir, "system-setup");

    public void EnsureClosed(Edition edition)
    {
        if (_processes.IsRunning(edition))
            throw new ClientRunningException(edition);
    }

    public SystemInstallPlan Plan(Edition edition, Version target)
    {
        var installed = _installs.Detect().FirstOrDefault(i => i.Edition.Id == edition.Id)?.Version;
        return new SystemInstallPlan(SystemClientPlan.Choose(installed, target), installed);
    }

    /// <summary>Cancelling is possible until the elevated installer starts; after that it runs to the end.</summary>
    public async Task ReplaceAsync(Edition edition, Version target, SetupSource source, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        EnsureClosed(edition);
        var plan = Plan(edition, target);
        if (plan.Action == SystemClientAction.AlreadyInstalled)
            throw new InvalidOperationException($"{edition.DisplayName} {target} уже установлена в системе.");

        progress?.Report(new InstallProgress(InstallStage.Preparing, null));
        RemoveOldStaging(TimeSpan.FromHours(12));
        var staging = Path.Combine(_setupRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var setup = Path.Combine(staging, edition.SetupPrefix + ".exe");
            await StageSetupAsync(source, staging, setup, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            progress?.Report(new InstallProgress(InstallStage.InstallingSystem, null));
            _log.Info($"System install of {edition.DisplayName} {target}: {plan.Action}, installed now: {plan.Installed?.ToString() ?? "none"}.");
            await _runElevated(
                ["replace-client", "--edition", edition.Id.ToString(), "--version", target.ToString(), "--setup", setup],
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DeleteDir(staging);
        }

        var now = _installs.Detect().FirstOrDefault(i => i.Edition.Id == edition.Id);
        if (now?.Version != target)
            throw new InvalidOperationException(
                $"Установщик завершился без ошибок, но в системе не найдена версия {target} (найдено: {now?.Version.ToString() ?? "ничего"}).");
        _log.Info($"System install of {edition.DisplayName} {target} finished.");
    }

    /// <summary>
    /// Removes the installed client of the edition with its own uninstaller, without installing anything instead. The version must be
    /// the one installed now. Cancelling is possible until the elevated copy starts; after that the removal runs to the end.
    /// </summary>
    public async Task UninstallAsync(Edition edition, Version installedVersion, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        EnsureClosed(edition);
        var installed = _installs.Detect().FirstOrDefault(i => i.Edition.Id == edition.Id);
        if (installed is null)
            throw new InvalidOperationException($"{edition.DisplayName} не установлена в системе.");
        if (installed.Version != installedVersion)
            throw new InvalidOperationException($"В системе установлена {edition.DisplayName} {installed.Version}, а не {installedVersion}. Обновите список и повторите.");

        ct.ThrowIfCancellationRequested();
        progress?.Report(new InstallProgress(InstallStage.UninstallingSystem, null));
        _log.Info($"System uninstall of {edition.DisplayName} {installedVersion}.");
        await _runElevated(
            ["uninstall-client", "--edition", edition.Id.ToString(), "--version", installedVersion.ToString()],
            CancellationToken.None).ConfigureAwait(false);

        if (_installs.Detect().Any(i => i.Edition.Id == edition.Id))
            throw new InvalidOperationException($"Удаление завершилось без ошибок, но {edition.DisplayName} всё ещё найдена в системе.");
        _log.Info($"System uninstall of {edition.DisplayName} {installedVersion} finished.");
    }

    private async Task StageSetupAsync(SetupSource source, string staging, string setup, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        string zip;
        if (source.Release is { } release)
        {
            _disk.EnsureFree(_setupRoot, DiskSpace.RequiredForInstall(release.SizeBytes));
            zip = Path.Combine(staging, release.FileName);
            var downloadProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p =>
                progress.Report(new InstallProgress(InstallStage.Downloading, p.Total is > 0 ? (double)p.Received / p.Total.Value : null)));
            _log.Info($"Downloading {release.FileName} for the system install.");
            await _downloader.DownloadAsync(_catalog.GetDownloadUri(release), zip, downloadProgress, ct).ConfigureAwait(false);
        }
        else
        {
            var file = source.FilePath ?? throw new ArgumentException("The setup source has neither a release nor a file.", nameof(source));
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                _disk.EnsureFree(_setupRoot, new FileInfo(file).Length * 2);
                File.Copy(file, setup);
                return;
            }

            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Нужен файл установщика Pilot (.zip или .exe), а выбран {Path.GetFileName(file)}.");
            _disk.EnsureFree(_setupRoot, new FileInfo(file).Length * 3);
            zip = file;
        }

        progress?.Report(new InstallProgress(InstallStage.ExtractingSetup, null));
        await Task.Run(() =>
        {
            VersionInstaller.ExtractSetupFromZip(zip, setup);
            if (source.Release is not null)
                File.Delete(zip);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Leftovers of an interrupted install; younger folders may belong to a launcher running right now.</summary>
    private void RemoveOldStaging(TimeSpan olderThan)
    {
        if (!Directory.Exists(_setupRoot))
            return;
        foreach (var dir in Directory.EnumerateDirectories(_setupRoot))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > olderThan)
                DeleteDir(dir);
        }
    }

    private void DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            // cleanup must not hide the install result
            _log.Warn($"Could not remove {dir}: {ex.Message}");
        }
    }
}
