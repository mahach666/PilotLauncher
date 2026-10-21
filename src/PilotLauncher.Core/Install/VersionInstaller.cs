using System.IO.Compression;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Install;

public enum InstallStage
{
    Preparing,
    Downloading,
    ExtractingSetup,
    ExtractingPackage,
    UnpackingClient,
    Copying,
    Finishing,
    InstallingSystem,
    UninstallingSystem,
}

/// <param name="Fraction">0..1 when the stage can tell how far it is, otherwise null.</param>
public readonly record struct InstallProgress(InstallStage Stage, double? Fraction);

/// <summary>
/// Turns a catalog entry (or an existing system installation) into an unpacked version in the store. Downloaded and
/// intermediate files are deleted as soon as the next stage has them, so only the unpacked client stays on disk.
/// </summary>
public sealed class VersionInstaller
{
    private readonly VersionStore _store;
    private readonly ReleaseCatalog _catalog;
    private readonly FileDownloader _downloader;
    private readonly DiskSpace _disk;
    private readonly ILauncherLog _log;

    public VersionInstaller(VersionStore store, ReleaseCatalog catalog, FileDownloader downloader, DiskSpace disk, ILauncherLog log)
    {
        _store = store;
        _catalog = catalog;
        _downloader = downloader;
        _disk = disk;
        _log = log;
    }

    public async Task InstallAsync(ReleaseEntry entry, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var finalDir = _store.VersionDir(entry.Edition, entry.Version);
        _store.EnsureAbsent(entry.Edition, entry.Version);

        progress?.Report(new InstallProgress(InstallStage.Preparing, null));
        Directory.CreateDirectory(_store.Root);
        _disk.EnsureFree(_store.Root, DiskSpace.RequiredForInstall(entry.SizeBytes));

        var work = NewWorkDir();
        try
        {
            var zip = Path.Combine(work, entry.FileName);
            var downloadProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p =>
                progress.Report(new InstallProgress(InstallStage.Downloading, p.Total is > 0 ? (double)p.Received / p.Total.Value : null)));
            _log.Info($"Downloading {entry.FileName}");
            await _downloader.DownloadAsync(_catalog.GetDownloadUri(entry), zip, downloadProgress, ct).ConfigureAwait(false);

            await Task.Run(() =>
            {
                progress?.Report(new InstallProgress(InstallStage.ExtractingSetup, null));
                var setupExe = Path.Combine(work, "setup.exe");
                ExtractSetupFromZip(zip, setupExe);
                File.Delete(zip);
                UnpackSetup(setupExe, ownsSetup: true, entry.Edition, entry.Version, VersionSource.Catalog, work, progress, ct);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteWorkDir(work);
        }
    }

    /// <summary>
    /// Downloads the zip of a catalog entry into <paramref name="folder"/> as it is, for carrying to a PC without internet.
    /// An interrupted download leaves <c>.part</c> next to the target and continues from it next time.
    /// </summary>
    public async Task DownloadToFolderAsync(ReleaseEntry entry, string folder, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var target = Path.Combine(folder, entry.FileName);
        if (File.Exists(target))
            throw new IOException($"{target} already exists.");
        Directory.CreateDirectory(folder);
        _disk.EnsureFree(folder, entry.SizeBytes + (64L << 20));
        _log.Info($"Downloading {entry.FileName} to {folder}");
        await _downloader.DownloadAsync(_catalog.GetDownloadUri(entry), target, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unpacks a setup package that is already on disk, a zip from the download center or the setup exe itself.
    /// The file is only read: it may be the user's only copy. Only the working files in the store's temp folder are deleted.
    /// </summary>
    public Task InstallFromFileAsync(string path, Edition edition, Version version, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var finalDir = _store.VersionDir(edition, version);
            _store.EnsureAbsent(edition, version);

            progress?.Report(new InstallProgress(InstallStage.Preparing, null));
            Directory.CreateDirectory(_store.Root);
            _disk.EnsureFree(_store.Root, DiskSpace.RequiredForInstall(new FileInfo(path).Length));

            var work = NewWorkDir();
            try
            {
                var setupExe = path;
                var ownsSetup = false;
                if (SetupFileInspector.IsZip(path))
                {
                    progress?.Report(new InstallProgress(InstallStage.ExtractingSetup, null));
                    setupExe = Path.Combine(work, "setup.exe");
                    ownsSetup = true;
                    ExtractSetupFromZip(path, setupExe);
                }

                UnpackSetup(setupExe, ownsSetup, edition, version, VersionSource.LocalFile, work, progress, ct);
            }
            finally
            {
                DeleteWorkDir(work);
            }
        }, ct);

    /// <summary>Unpacks a setup exe that is already on disk (also used by tests). The exe is left alone.</summary>
    public Task InstallFromSetupAsync(string setupExe, Edition edition, Version version, CancellationToken ct) =>
        InstallFromFileAsync(setupExe, edition, version, null, ct);

    /// <summary>Copies a system installation into the store so it can run under an isolated account.</summary>
    public Task CopySystemInstallAsync(SystemInstall system, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var finalDir = _store.VersionDir(system.Edition, system.Version);
            _store.EnsureAbsent(system.Edition, system.Version);

            progress?.Report(new InstallProgress(InstallStage.Preparing, null));
            Directory.CreateDirectory(_store.Root);
            var files = Directory.EnumerateFiles(system.InstallDir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            var total = files.Sum(f => f.Length);
            _disk.EnsureFree(_store.Root, total + (64L << 20));

            var staging = finalDir + ".partial";
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            var app = Path.Combine(staging, "app");
            try
            {
                var done = 0L;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(app, Path.GetRelativePath(system.InstallDir, file.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    file.CopyTo(target);
                    done += file.Length;
                    progress?.Report(new InstallProgress(InstallStage.Copying, total == 0 ? null : (double)done / total));
                }

                var exeRelative = Path.GetRelativePath(system.InstallDir, system.ExePath);
                Commit(staging, finalDir, new VersionManifest(exeRelative, VersionSource.SystemCopy, DateTime.UtcNow));
            }
            catch
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
                throw;
            }
        }, ct);

    /// <param name="ownsSetup">True when <paramref name="setupExe"/> is our own temporary copy that may go as soon as the cabinet is out.</param>
    private void UnpackSetup(string setupExe, bool ownsSetup, Edition edition, Version version, VersionSource source, string work, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress(InstallStage.ExtractingPackage, null));
        var cabinet = Path.Combine(work, "payload.cab");
        BurnBundle.ExtractLargestCabinet(setupExe, cabinet);
        if (ownsSetup)
            File.Delete(setupExe);
        ct.ThrowIfCancellationRequested();

        var payloads = Path.Combine(work, "payloads");
        ExternalTools.ExtractCabinet(cabinet, payloads);
        File.Delete(cabinet);
        ct.ThrowIfCancellationRequested();

        var msi = MsiPackage.SelectClient(
            Directory.EnumerateFiles(payloads).Where(MsiPackage.LooksLikeMsi).Select(MsiPackage.Read), edition);
        _log.Info($"Client package: {Path.GetFileName(msi.Path)} ({msi.ProductName}, x64={msi.IsX64})");

        progress?.Report(new InstallProgress(InstallStage.UnpackingClient, null));
        var image = Path.Combine(work, "image");
        ExternalTools.ExtractMsi(msi.Path, image);
        Directory.Delete(payloads, recursive: true);
        ct.ThrowIfCancellationRequested();

        var client = ClientLocator.Find(image, edition);
        var finalDir = _store.VersionDir(edition, version);
        var staging = finalDir + ".partial";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new InstallProgress(InstallStage.Finishing, null));
            Directory.Move(client.AppDir, Path.Combine(staging, "app"));
            Commit(staging, finalDir, new VersionManifest(client.ExeRelativePath, source, DateTime.UtcNow));
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }

        _log.Info($"Installed {edition.DisplayName} {version} to {finalDir}");
    }

    private void Commit(string staging, string finalDir, VersionManifest manifest)
    {
        manifest.Save(Path.Combine(staging, VersionManifest.FileName));
        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        Directory.Move(staging, finalDir);
    }

    internal static void ExtractSetupFromZip(string zipPath, string exePath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The archive does not contain a setup executable.");
        entry.ExtractToFile(exePath, overwrite: true);
    }

    private string NewWorkDir()
    {
        var dir = Path.Combine(_store.TempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void DeleteWorkDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            // cleanup must not hide the install result; the next start removes what is left
            _log.Warn($"Could not remove temporary folder {dir}: {ex.Message}");
        }
    }
}
