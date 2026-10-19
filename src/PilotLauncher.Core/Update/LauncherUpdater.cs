using System.Security.Cryptography;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Update;

/// <summary>
/// Updates the launcher's own single-file exe from its GitHub releases. Windows lets a running exe be renamed but not
/// overwritten, so the new file is downloaded next to it, the running one is renamed to <c>*.old</c> and the new one takes
/// its name; the restarted launcher removes <c>*.old</c>.
/// </summary>
public sealed class LauncherUpdater
{
    /// <summary>The installer asset is the same exe as the one in the portable zip, so it serves both.</summary>
    public static readonly ExtensionSource Source = new("launcher", "Pilot Launcher", "mahach666/PilotLauncher", "PilotLauncher-Setup.exe", "", "");

    public static readonly Uri ReleasesPage = Source.ReleasesPage;

    private readonly GitHubReleaseClient _github;
    private readonly FileDownloader _downloader;
    private readonly ILauncherLog _log;

    public LauncherUpdater(GitHubReleaseClient github, FileDownloader downloader, ILauncherLog log)
    {
        _github = github;
        _downloader = downloader;
        _log = log;
    }

    /// <summary>The latest release when it is newer than <paramref name="current"/>, else null.</summary>
    public async Task<ExtensionRelease?> FindNewerAsync(Version current, CancellationToken ct)
    {
        var result = await _github.GetLatestAsync(Source, force: true, ct).ConfigureAwait(false);
        if (result.Error is not null)
            _log.Info($"Update check used the saved answer from {result.FetchedUtc:u}: {result.Error}");
        return IsNewer(result.Release.Version, current) ? result.Release : null;
    }

    /// <summary>Tags have three numbers (v1.2.0), the assembly four (1.2.0.0); a tag that is not a version is never an update.</summary>
    public static bool IsNewer(Version? release, Version current) =>
        release is not null && Normalize(release) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Downloads the release next to <paramref name="exePath"/> and checks its size and SHA-256; returns the downloaded file.</summary>
    public async Task<string> DownloadAsync(ExtensionRelease release, string exePath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // the file replaces the program itself: without a checksum from GitHub it is not taken
        if (release.Sha256 is null)
            throw new InvalidDataException($"GitHub не сообщил контрольную сумму {release.AssetName} в релизе {release.Tag}. Обновите лаунчер вручную: {ReleasesPage}");

        var target = exePath + ".new";
        try
        {
            await _downloader.DownloadAsync(release.DownloadUri, target, progress, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Нет права записи в папку {Path.GetDirectoryName(exePath)}, поэтому лаунчер не может обновить себя сам. Обновите его вручную: {ReleasesPage}", ex);
        }

        string actual;
        await using (var stream = File.OpenRead(target))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase) || new FileInfo(target).Length != release.Size)
        {
            File.Delete(target);
            throw new InvalidDataException($"Скачанный файл обновления {release.Tag} повреждён: контрольная сумма {actual}, ожидалась {release.Sha256}. Попробуйте ещё раз.");
        }

        return target;
    }

    /// <summary>Puts <paramref name="newExe"/> in place of the running <paramref name="exePath"/>, which stays as <c>*.old</c> until the next start.</summary>
    public static void Replace(string exePath, string newExe)
    {
        var old = exePath + ".old";
        try
        {
            if (File.Exists(old))
                File.Delete(old); // left by an earlier update
            File.Move(exePath, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(newExe);
            throw new IOException($"Не удалось заменить файл лаунчера в папке {Path.GetDirectoryName(exePath)}: {ex.Message} Обновите лаунчер вручную: {ReleasesPage}", ex);
        }

        try
        {
            File.Move(newExe, exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, exePath);
            throw new IOException($"Не удалось поставить новую версию лаунчера: {ex.Message} Текущая версия осталась на месте.", ex);
        }
    }

    /// <summary>
    /// Removes <c>*.old</c> left by an update. The previous launcher may still be closing, so a locked file is tried again a few
    /// times; if it stays locked it is left for the next start.
    /// </summary>
    public static async Task CleanUpAsync(string exePath, ILauncherLog log, int attempts = 10, TimeSpan? delay = null)
    {
        var old = exePath + ".old";
        for (var i = 1; File.Exists(old); i++)
        {
            try
            {
                File.Delete(old);
                log.Info($"Removed {old} left by the update.");
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts)
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Info($"Could not remove {old}, it is tried again at the next start: {ex.Message}");
                return;
            }
        }
    }
}
