using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using PilotLauncher.App.Infrastructure;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Launch;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Net;
using PilotLauncher.Core.Notes;

namespace PilotLauncher.App;

public static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " ГБ",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " МБ",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " КБ",
        _ => bytes + " Б",
    };
}

public sealed class VersionItem
{
    public VersionItem(VersionEntry entry) => Entry = entry;

    public VersionEntry Entry { get; }
    public string Version => Entry.Version.ToString();

    public string Marker =>
        Entry.IsInSystem && Entry.IsInStore ? "в системе + копия" :
        Entry.IsInSystem ? "установлена в системе" :
        Entry.IsInStore ? "скачана" :
        Entry.Setup is not null ? "из файла, без сети" :
        Entry.Release is { } release ? $"скачать {Format.Size(release.SizeBytes)}" : "";

    public bool IsReady => Entry.IsReady;
    public bool IsSystem => Entry.IsInSystem;

    /// <summary>A build from the pre-release folder that is not among the releases.</summary>
    public bool IsPrerelease => Entry.Release?.IsPrerelease == true;

    /// <summary>What screen readers and UI automation announce for the list entry.</summary>
    public override string ToString() => $"{Version} {(IsPrerelease ? "бета " : "")}{Marker}".Trim();
}

/// <summary>One release in the "what is new" list: what its notes say about the selected edition.</summary>
public sealed class NotesSection
{
    public NotesSection(EditionDigest digest)
    {
        var release = digest.Release;
        Header = release.Date is { } date ? $"Версия {release.Version} от {date:dd.MM.yyyy}" : $"Версия {release.Version}";
        Notices = release.Notices;
        Changes = digest.Changes;
        Fixes = digest.Fixes;
        FixesHeader = $"Исправлено ошибок: {Fixes.Count}";
        Footnote = digest.UnattributedFixes > 0
            ? $"Ещё исправлений без указания продукта: {digest.UnattributedFixes}. Их можно увидеть на полной странице."
            : "";
        IsEmpty = Changes.Count == 0 && Fixes.Count == 0 && digest.UnattributedFixes == 0;
    }

    public string Header { get; }
    public IReadOnlyList<string> Notices { get; }
    public IReadOnlyList<string> Changes { get; }
    public IReadOnlyList<string> Fixes { get; }
    public string FixesHeader { get; }
    public string Footnote { get; }
    public bool IsEmpty { get; }
    public bool HasNotices => Notices.Count > 0;
    public bool HasChanges => Changes.Count > 0;
    public bool HasFixes => Fixes.Count > 0;
    public bool HasFootnote => Footnote.Length > 0;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Func<AppServices> _services;
    private IReadOnlyList<ReleaseEntry> _catalog = [];
    private IReadOnlyList<VersionEntry> _all = [];
    private IReadOnlyList<LocalSetup> _localSetups = [];
    private string? _catalogNotice;
    private string? _folderNotice;
    private CancellationTokenSource? _cts;
    private Edition _selectedEdition = Editions.All[0];
    private VersionItem? _selectedVersion;
    private bool _isBusy;
    private double _progress;
    private bool _progressIndeterminate;
    private string _status = "";
    private string? _error;
    private string? _notice;
    private string _sizeText = "";
    private IReadOnlyList<ReleaseNotes> _notes = [];
    private bool _notesLoaded;
    private bool _notesCompare;
    private VersionItem? _compareVersion;
    private string? _notesWarning;
    private string _notesCaption = "";
    private string _notesStatus = "";
    private bool _notesCertificateProblem;

    public MainViewModel(Func<AppServices> services)
    {
        _services = services;
        LaunchCommand = new AsyncCommand(() => LaunchAsync(forceIsolated: false), () => CanAct, ShowError);
        LaunchIsolatedCommand = new AsyncCommand(() => LaunchAsync(forceIsolated: true), () => CanAct && SelectedEntry is { IsInSystem: true, IsInStore: true }, ShowError);
        CopyToSeparateUserCommand = new AsyncCommand(CopyToSeparateUserAsync, () => CanAct && SelectedEntry is { IsInSystem: true, IsInStore: false }, ShowError);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanAct && SelectedEntry?.IsInStore == true, ShowError);
        InstallToSystemCommand = new AsyncCommand(InstallToSystemAsync, () => CanAct && SelectedEntry is { IsInSystem: false } e && SetupSourceFor(e) is not null, ShowError);
        RefreshCommand = new AsyncCommand(RefreshAllAsync, () => !IsBusy, ShowError);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedEntry?.IsReady == true);
        CreateShortcutCommand = new RelayCommand(CreateShortcut, () => SelectedEntry?.IsReady == true);
        AddFromFileCommand = new AsyncCommand(PickAndAddFilesAsync, () => !IsBusy, ShowError);
        DownloadForOfflineCommand = new AsyncCommand(DownloadForOfflineAsync, () => !IsBusy && SelectedEntry?.Release is not null, ShowError);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts is not null);
        DismissErrorCommand = new RelayCommand(() => Error = null);
        OpenNotesPageCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(ActiveNotes.PageUri.ToString()) { UseShellExecute = true }));
        TrustRussianRootCommand = new AsyncCommand(() => SetRussianRootTrustAsync(true), () => CanTrustRussianRoot, ShowError);
        StopTrustingRussianRootCommand = new AsyncCommand(() => SetRussianRootTrustAsync(false), () => IsRussianRootTrusted, ShowError);
        Extensions = new ExtensionsViewModel(services, () => SelectedEntry, RunBusyAsync, () => IsIdle, p => ShowProgress(p), s => Status = s, ShowError);
    }

    /// <summary>Extensions of the selected version: install, update, remove.</summary>
    public ExtensionsViewModel Extensions { get; }

    /// <summary>The refresh button also asks GitHub again for the latest extension releases, ignoring what was saved.</summary>
    private async Task RefreshAllAsync()
    {
        await ReloadAsync(refreshCatalog: true);
        await Extensions.RefreshAsync(forceNetwork: true);
    }

    public IReadOnlyList<Edition> EditionList => Editions.All;
    public ObservableCollection<VersionItem> Versions { get; } = [];
    public ObservableCollection<RunningClient> Running { get; } = [];
    public ObservableCollection<NotesSection> NotesSections { get; } = [];

    public AsyncCommand LaunchCommand { get; }
    public AsyncCommand LaunchIsolatedCommand { get; }
    public AsyncCommand CopyToSeparateUserCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand InstallToSystemCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AddFromFileCommand { get; }
    public AsyncCommand DownloadForOfflineCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CreateShortcutCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand OpenNotesPageCommand { get; }
    public AsyncCommand TrustRussianRootCommand { get; }
    public AsyncCommand StopTrustingRussianRootCommand { get; }

    /// <summary>Set by the window: shows which certificate would be trusted and asks to go on.</summary>
    public Func<PinnedRootTrust, bool>? ConfirmTrustRussianRoot { get; set; }

    /// <summary>Set by the window: asks whether to remove the data of the version too. Null means cancelled.</summary>
    public Func<VersionEntry, bool?>? ConfirmDelete { get; set; }

    /// <summary>Set by the window: lets the user choose setup files. Empty means cancelled.</summary>
    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>Set by the window: asks for a folder, starting at the given one. Null means cancelled.</summary>
    public Func<string?, string?>? PickFolder { get; set; }

    /// <summary>Set by the window: asks edition and version of a file that tells neither. Null means cancelled.</summary>
    public Func<string, SetupFile?>? AskSetup { get; set; }

    /// <summary>Set by the window: shows what the system install will do and asks to go on.</summary>
    public Func<VersionEntry, SystemInstallPlan, bool>? ConfirmInstallToSystem { get; set; }

    public event Action? RunningChanged;

    public Edition SelectedEdition
    {
        get => _selectedEdition;
        set
        {
            if (Set(ref _selectedEdition, value))
                FillVersions(null);
        }
    }

    public VersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (Set(ref _selectedVersion, value))
                OnSelectionChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(IsIdle));
                RefreshCommands();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>The version list also offers the pre-release builds from <c>/beta/</c>, and the notes come from there.</summary>
    public bool Prerelease => _services().Settings.Prerelease;

    private ReleaseNotesCatalog ActiveNotes => Prerelease ? _services().BetaNotes : _services().Notes;

    /// <summary>Called by the settings window after the mode was changed and saved: loads the list and notes of the new mode.</summary>
    public async Task PrereleaseChangedAsync()
    {
        Raise(nameof(Prerelease));
        // the other folder's versions must not stay offered while the new list loads
        _catalog = [];
        await ReloadAsync(refreshCatalog: false);
        await ReloadAsync(refreshCatalog: true);
    }

    public bool NotesSingle
    {
        get => !NotesCompare;
        set
        {
            if (value)
                NotesCompare = false;
        }
    }

    /// <summary>True: show what changed between <see cref="CompareVersion"/> and the selected version instead of the notes of the selected one.</summary>
    public bool NotesCompare
    {
        get => _notesCompare;
        set
        {
            if (Set(ref _notesCompare, value))
            {
                Raise(nameof(NotesSingle));
                RebuildNotes();
            }
        }
    }

    public VersionItem? CompareVersion
    {
        get => _compareVersion;
        set
        {
            if (Set(ref _compareVersion, value))
                RebuildNotes();
        }
    }

    public string? NotesWarning { get => _notesWarning; private set { Set(ref _notesWarning, value); Raise(nameof(HasNotesWarning)); } }
    public bool HasNotesWarning => !string.IsNullOrEmpty(NotesWarning);

    /// <summary>The user allowed the launcher to trust the Russian Trusted Root CA (only inside the launcher).</summary>
    public bool IsRussianRootTrusted => _services().Settings.TrustRussianRoot;

    /// <summary>The last notes download failed on the site's certificate and trust has not been given yet.</summary>
    public bool CanTrustRussianRoot => _notesCertificateProblem && !IsRussianRootTrusted;
    public string NotesCaption { get => _notesCaption; private set { Set(ref _notesCaption, value); Raise(nameof(HasNotesCaption)); } }
    public bool HasNotesCaption => NotesCaption.Length > 0;
    public string NotesStatus { get => _notesStatus; private set { Set(ref _notesStatus, value); Raise(nameof(HasNotesStatus)); } }
    public bool HasNotesStatus => NotesStatus.Length > 0;

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string? Notice { get => _notice; private set { Set(ref _notice, value); Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    public VersionEntry? SelectedEntry => SelectedVersion?.Entry;
    public bool CanAct => !IsBusy && SelectedEntry is not null && (SelectedEntry.IsReady || SelectedEntry.CanDownload || SelectedEntry.CanInstallFromFile);

    public string Title => SelectedEntry is { } e ? $"{e.Edition.DisplayName} {e.Version}" : "Версия не выбрана";

    public string LaunchText => SelectedEntry switch
    {
        null => "Запустить",
        { IsReady: true } => "Запустить",
        { CanInstallFromFile: true } => "Распаковать и запустить",
        { CanDownload: true } => "Скачать и запустить",
        _ => "Недоступна",
    };

    public string StateText => (SelectedVersion?.IsPrerelease == true ? "Предварительная версия, ещё не выпущена. " : "") + SelectedEntry switch
    {
        null => "",
        { IsInSystem: true, IsInStore: true } => "Установлена в системе. Есть и отдельная копия для запуска в своём профиле.",
        { IsInSystem: true } => "Установлена в системе. Запускается от вашей учётной записи, с вашими настройками.",
        { IsInStore: true } => "Скачана. Запускается в отдельном профиле, настройки не смешиваются с другими версиями.",
        { CanInstallFromFile: true } => "Не распакована. Установщик лежит на диске, при запуске он будет распакован, интернет не нужен.",
        { CanDownload: true } => "Не скачана. Загрузится при запуске.",
        _ => "Нет в центре загрузок.",
    };

    /// <summary>Pilot's update service installs into the system, which an isolated copy is meant to stay out of.</summary>
    public bool ShowUpdateWarning => SelectedEntry is { } e && (e.IsInStore || e.CanInstallFromFile || e.CanDownload);

    public string ReleasedText => SelectedEntry?.Release?.Published is { } d ? $"Выпущена: {d:dd.MM.yyyy}" : "";

    public string FolderText => SelectedEntry switch
    {
        { Stored: { } s } => s.AppDir,
        { System: { } s } => s.InstallDir,
        { Setup: { } s } => s.Path,
        _ => "",
    };

    public string RunningText => Running.Count == 0 ? "" : $"Запущено клиентов: {Running.Count}";

    public async Task InitializeAsync()
    {
        _services().Library.Store.CleanTemp(TimeSpan.FromHours(12));
        var system = _services().Library.Build([]).FirstOrDefault(e => e.IsInSystem);
        if (system is not null)
            _selectedEdition = system.Edition;
        Raise(nameof(SelectedEdition));
        // installed and offline versions are shown before the network answers (or fails)
        await ReloadAsync(refreshCatalog: false);
        await ReloadAsync(refreshCatalog: true);
    }

    public async Task ReloadAsync(bool refreshCatalog)
    {
        if (refreshCatalog)
        {
            var prerelease = Prerelease;
            Status = "Загрузка списка версий...";
            ProgressIndeterminate = true;
            var services = _services();
            var releases = LoadListingAsync(services.Catalog, prerelease: false);
            var betas = prerelease ? LoadListingAsync(services.BetaCatalog, prerelease: true) : Task.FromResult(NoListing);
            var (releaseEntries, releaseNotice) = await releases;
            var (betaEntries, betaNotice) = await betas;
            // the mode was switched meanwhile: the reload of the new mode fills the list
            if (prerelease != Prerelease)
                return;
            ProgressIndeterminate = false;
            Status = "";
            // a listing that failed keeps its last good entries
            // shortcut: with no release list ever loaded, released builds from /beta/ are marked as pre-releases too
            _catalog = ReleaseCatalog.WithPrereleases(
                releaseEntries ?? _catalog.Where(e => !e.IsPrerelease).ToList(),
                prerelease ? betaEntries ?? _catalog.Where(e => e.IsPrerelease).ToList() : []);
            _catalogNotice = releaseNotice is null && betaNotice is null
                ? null
                : string.Join(Environment.NewLine, new[] { releaseNotice, betaNotice }.Where(n => n is not null));
        }

        await ScanSetupFolderAsync();
        _all = _services().Library.Build(_catalog, _localSetups);
        FillVersions(SelectedVersion?.Entry);

        if (refreshCatalog)
            await LoadNotesAsync();
    }

    private static readonly (IReadOnlyList<ReleaseEntry>? Entries, string? Notice) NoListing = (null, null);

    /// <summary>One listing and what to tell about it; no entries when there is neither a connection nor a saved copy.</summary>
    private async Task<(IReadOnlyList<ReleaseEntry>? Entries, string? Notice)> LoadListingAsync(ReleaseCatalog catalog, bool prerelease)
    {
        try
        {
            var result = await catalog.LoadAsync(CancellationToken.None);
            return (result.Entries, !result.FromCache ? null : prerelease
                ? $"Нет связи с папкой предварительных версий, показан сохранённый список. {result.Error}"
                : $"Нет связи с центром загрузок, показан сохранённый список. {result.Error}");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            _services().Log.Error("Version listing failed", ex);
            return (null, prerelease
                ? $"Списка предварительных версий нет: нет связи с pilot.ascon.ru/beta и нет сохранённого списка. ({ex.Message})"
                : "Каталога версий нет: нет связи с центром загрузок и нет сохранённого списка. " +
                  "Установленные в системе и скопированные версии, а также версии из папки с установщиками запускаются как обычно; " +
                  $"файл установщика можно добавить через меню «Файл». ({ex.Message})");
        }
    }

    private async Task LoadNotesAsync()
    {
        _notesLoaded = false;
        RebuildNotes();
        var notes = ActiveNotes;
        var host = notes.PageUri.Host;
        // the launcher's own trust in the Russian root covers only some hosts; elsewhere the button would not help
        var canTrust = _services().RussianRoot.Hosts.Contains(host);
        try
        {
            var result = await notes.LoadAsync(CancellationToken.None);
            if (notes != ActiveNotes)
                return;
            _notes = result.Releases;
            _notesCertificateProblem = result.CertificateProblem && canTrust;
            NotesWarning = result.Error is null
                ? null
                : $"Не удалось обновить заметки с {host}, показана сохранённая копия от {result.FetchedUtc.ToLocalTime():dd.MM.yyyy}. Причина: {result.Error}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _services().Log.Error("Release notes failed", ex);
            if (notes != ActiveNotes)
                return;
            _notes = [];
            _notesCertificateProblem = ReleaseNotesCatalog.IsCertificateProblem(ex) && canTrust;
            NotesWarning = $"Не удалось получить заметки о выпуске с {host}: {ReleaseNotesCatalog.Describe(ex)}";
        }

        _notesLoaded = true;
        RaiseRussianRootState();
        RebuildNotes();
    }

    /// <summary>Turns trust in the Russian root on or off for help.pilotems.com. Only the launcher is affected.</summary>
    private async Task SetRussianRootTrustAsync(bool trust)
    {
        Error = null;
        var services = _services();
        if (trust && ConfirmTrustRussianRoot?.Invoke(services.RussianRoot) != true)
            return;
        services.Settings.TrustRussianRoot = trust;
        services.Settings.Save(services.Paths.SettingsFile);
        services.Log.Info(trust
            ? $"Trust in the Russian Trusted Root CA (SHA-256 {services.RussianRoot.Sha256}) enabled for {string.Join(", ", services.RussianRoot.Hosts)}."
            : "Trust in the Russian Trusted Root CA disabled.");
        RaiseRussianRootState();
        await LoadNotesAsync();
    }

    private void RaiseRussianRootState()
    {
        Raise(nameof(IsRussianRootTrusted));
        Raise(nameof(CanTrustRussianRoot));
        TrustRussianRootCommand.RaiseCanExecuteChanged();
        StopTrustingRussianRootCommand.RaiseCanExecuteChanged();
    }

    private async Task ScanSetupFolderAsync()
    {
        var folder = _services().Settings.SetupFolder;
        _localSetups = [];
        _folderNotice = null;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                _localSetups = await Task.Run(() => SetupFolderScanner.Scan(folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _folderNotice = $"Папка с установщиками недоступна: {folder}. {ex.Message}";
            }
        }

        Notice = string.Join(Environment.NewLine, new[] { _catalogNotice, _folderNotice }.Where(n => n is not null));
    }

    private void FillVersions(VersionEntry? keep)
    {
        var items = _all.Where(e => e.Edition.Id == SelectedEdition.Id).Select(e => new VersionItem(e)).ToList();
        Versions.Clear();
        foreach (var item in items)
            Versions.Add(item);
        SelectedVersion =
            items.FirstOrDefault(i => keep is not null && i.Entry.Version == keep.Version && i.Entry.Edition.Id == keep.Edition.Id)
            ?? items.FirstOrDefault(i => i.Entry.IsInSystem)
            ?? items.FirstOrDefault(i => i.Entry.IsInStore)
            ?? items.FirstOrDefault();
    }

    private void OnSelectionChanged()
    {
        foreach (var name in new[] { nameof(SelectedEntry), nameof(CanAct), nameof(Title), nameof(LaunchText), nameof(StateText), nameof(ReleasedText), nameof(FolderText), nameof(ShowUpdateWarning) })
            Raise(name);
        SizeText = SelectedEntry switch
        {
            { Setup: { } s, IsReady: false } => $"Размер установщика: {Format.Size(s.SizeBytes)}",
            { Release: { } r, IsReady: false } => $"Размер загрузки: около {Format.Size(r.SizeBytes)}",
            _ => "",
        };
        RefreshCommands();
        PickCompareVersion();
        RebuildNotes();
        _ = Extensions.RefreshAsync(forceNetwork: false);

        if (SelectedEntry is { Stored: { } stored } entry)
            _ = ShowSizeOnDiskAsync(entry, stored);
        else if (SelectedEntry is { System: { } system } systemEntry)
            _ = ShowSizeOnDiskAsync(systemEntry, system.InstallDir);
    }

    /// <summary>Keeps the version to compare with in the list and different from the selected one; the default is the next older version.</summary>
    private void PickCompareVersion()
    {
        if (CompareVersion is not null && Versions.Contains(CompareVersion) && CompareVersion != SelectedVersion)
            return;
        var index = SelectedVersion is null ? -1 : Versions.IndexOf(SelectedVersion);
        _compareVersion = index < 0 ? null : index + 1 < Versions.Count ? Versions[index + 1] : index > 0 ? Versions[index - 1] : null;
        Raise(nameof(CompareVersion));
    }

    private void RebuildNotes()
    {
        NotesSections.Clear();
        NotesCaption = "";
        NotesStatus = "";
        if (SelectedEntry is not { } entry)
            return;
        if (!_notesLoaded)
        {
            NotesStatus = "Загрузка заметок о выпуске...";
            return;
        }

        if (_notes.Count == 0)
        {
            NotesStatus = "Заметки о выпуске недоступны.";
            return;
        }

        var edition = entry.Edition;
        if (NotesCompare)
        {
            if (CompareVersion is null)
            {
                NotesStatus = "Выберите версию для сравнения.";
                return;
            }

            var other = CompareVersion.Entry.Version;
            var releases = EditionNotes.Between(_notes, other, entry.Version);
            var (older, newer) = other <= entry.Version ? (other, entry.Version) : (entry.Version, other);
            NotesCaption = $"Изменения после версии {older} и до версии {newer} включительно (выпусков: {releases.Count}).";
            var sections = releases.Select(r => new NotesSection(EditionNotes.Digest(r, edition))).Where(s => !s.IsEmpty || s.HasNotices).ToList();
            foreach (var section in sections)
                NotesSections.Add(section);
            if (sections.Count == 0)
                NotesStatus = releases.Count == 0 ? "Между этими версиями нет выпусков в заметках." : $"В этих выпусках нет изменений для {edition.DisplayName}.";
            return;
        }

        var match = EditionNotes.Find(_notes, entry.Version);
        if (match is null)
        {
            NotesStatus = $"В заметках нет версии {entry.Version}: они начинаются с версии {_notes[^1].Version}.";
            return;
        }

        NotesCaption = match.Exact
            ? ""
            : $"В центре загрузок версия {entry.Version}, в заметках {match.Notes.Version}: номера сборки отличаются, выпуск определён по первым числам версии.";
        var single = new NotesSection(EditionNotes.Digest(match.Notes, edition));
        NotesSections.Add(single);
        if (single.IsEmpty)
            NotesStatus = $"В заметках этого выпуска нет изменений для {edition.DisplayName}.";
    }

    private async Task ShowSizeOnDiskAsync(VersionEntry entry, object source)
    {
        try
        {
            var bytes = await Task.Run(() => source switch
            {
                Core.Storage.StoredVersion s => VersionLibrary.SizeOnDisk(s),
                string dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                _ => 0L,
            });
            if (ReferenceEquals(SelectedEntry, entry))
                SizeText = $"Занимает на диске: {Format.Size(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(SelectedEntry, entry))
                SizeText = $"Размер на диске неизвестен: {ex.Message}";
        }
    }

    private void RefreshCommands()
    {
        Raise(nameof(CanAct));
        foreach (var command in new[] { LaunchCommand, LaunchIsolatedCommand, CopyToSeparateUserCommand, DeleteCommand, InstallToSystemCommand, RefreshCommand, AddFromFileCommand, DownloadForOfflineCommand })
            command.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        Extensions.RaiseCommandStates();
        CreateShortcutCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private async Task LaunchAsync(bool forceIsolated)
    {
        Error = null;
        var entry = SelectedEntry ?? throw new InvalidOperationException("No version selected.");
        var services = _services();

        if (!forceIsolated && entry.System is { } system)
        {
            Register(services.Launch.LaunchSystem(system));
            Status = $"Запущено: {entry.Edition.DisplayName} {entry.Version}";
            return;
        }

        if (entry.Stored is null)
        {
            var progress = new Progress<InstallProgress>(ShowProgress);
            // a setup file on disk wins over the download: no internet needed
            await RunBusyAsync(ct => entry.Setup is not null
                ? services.Library.InstallFromSetupFolderAsync(entry, progress, ct)
                : services.Library.DownloadAsync(entry, progress, ct));
            await ReloadAsync(refreshCatalog: false);
            entry = SelectedEntry ?? throw new InvalidOperationException("The installed version disappeared from the list.");
        }

        var stored = entry.Stored ?? throw new InvalidOperationException("The version was not stored.");
        await RunBusyAsync(async ct =>
        {
            ShowProgress(new InstallProgress(InstallStage.Preparing, null), "Подготовка профиля... Windows может попросить подтверждение.");
            Register(await services.Launch.LaunchIsolatedAsync(stored, ct));
        });
        Status = $"Запущено: {entry.Edition.DisplayName} {entry.Version}";
    }

    private async Task PickAndAddFilesAsync()
    {
        var files = PickFiles?.Invoke() ?? [];
        if (files.Count > 0)
            await AddFilesAsync(files);
    }

    /// <summary>
    /// Unpacks setup zips/exes the user has on disk, one after another; a file that does not work is reported at the end
    /// and does not stop the rest. The files themselves are never changed or deleted.
    /// </summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy)
        {
            Error = "Дождитесь окончания текущей операции.";
            return;
        }

        Error = null;
        try
        {
            await AddFilesCoreAsync(paths);
        }
        catch (OperationCanceledException)
        {
            Status = "Добавление отменено.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task AddFilesCoreAsync(IReadOnlyList<string> paths)
    {
        var services = _services();
        var added = new List<SetupFile>();
        var skipped = new List<string>();
        try
        {
            await RunBusyAsync(async ct =>
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = paths[i];
                    var name = Path.GetFileName(path);
                    var prefix = paths.Count == 1 ? name : $"Файл {i + 1} из {paths.Count}, {name}";
                    try
                    {
                        if (Directory.Exists(path) || !(path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            skipped.Add($"{name}: нужен файл установщика Pilot (.zip или .exe).");
                            continue;
                        }

                        ProgressIndeterminate = true;
                        Status = $"{prefix}: определение версии...";
                        var file = await Task.Run(() => services.Library.IdentifySetup(path), ct) ?? AskSetup?.Invoke(path);
                        if (file is null)
                        {
                            skipped.Add($"{name}: редакция и версия не указаны, файл пропущен.");
                            continue;
                        }

                        if (services.Library.Store.Find(file.Edition, file.Version) is not null)
                        {
                            skipped.Add($"{name}: {file.Edition.DisplayName} {file.Version} уже есть в списке.");
                            continue;
                        }

                        await services.Library.InstallFromFileAsync(file, new Progress<InstallProgress>(p =>
                        {
                            ShowProgress(p);
                            Status = $"{prefix}: {Status}";
                        }), ct);
                        added.Add(file);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // one bad file must not stop the others; it is logged and shown in the summary
                        services.Log.Error($"Could not add {path}", ex);
                        skipped.Add($"{name}: {ex.Message}");
                    }
                }
            });
        }
        finally
        {
            await ReloadAsync(refreshCatalog: false);
        }

        if (added.Count > 0)
        {
            var last = added[^1];
            if (SelectedEdition.Id != last.Edition.Id)
                SelectedEdition = last.Edition;
            SelectedVersion = Versions.FirstOrDefault(v => v.Entry.Version == last.Version) ?? SelectedVersion;
        }

        Status = added.Count == 0 ? "" : $"Добавлено версий: {added.Count}.";
        if (skipped.Count > 0)
            Error = "Не добавлено:" + Environment.NewLine + string.Join(Environment.NewLine, skipped);
    }

    private async Task DownloadForOfflineAsync()
    {
        Error = null;
        var entry = SelectedEntry ?? throw new InvalidOperationException("No version selected.");
        var release = entry.Release ?? throw new InvalidOperationException("Этой версии нет в центре загрузок.");
        var folder = PickFolder?.Invoke(_services().Settings.SetupFolder);
        if (folder is null)
            return;
        var target = Path.Combine(folder, release.FileName);
        if (File.Exists(target))
        {
            Status = $"Файл уже лежит в этой папке: {target}";
            return;
        }

        await RunBusyAsync(ct => _services().Library.DownloadToFolderAsync(entry, folder, new Progress<DownloadProgress>(p =>
            ShowProgress(new InstallProgress(InstallStage.Downloading, p.Total is > 0 ? (double)p.Received / p.Total.Value : null))), ct));
        await ReloadAsync(refreshCatalog: false);
        Status = $"Сохранено: {target}. Файл можно перенести на ПК без интернета и добавить через меню «Файл».";
    }

    private async Task CopyToSeparateUserAsync()
    {
        Error = null;
        var entry = SelectedEntry ?? throw new InvalidOperationException("No version selected.");
        await RunBusyAsync(ct => _services().Library.CopySystemAsync(entry, new Progress<InstallProgress>(ShowProgress), ct));
        await ReloadAsync(refreshCatalog: false);
        Status = "Копия готова. Её можно запустить в отдельном профиле.";
    }

    private async Task DeleteAsync()
    {
        Error = null;
        var entry = SelectedEntry ?? throw new InvalidOperationException("No version selected.");
        var deleteData = ConfirmDelete?.Invoke(entry);
        if (deleteData is null)
            return;
        await RunBusyAsync(async ct =>
        {
            Status = "Удаление...";
            ProgressIndeterminate = true;
            await _services().Library.DeleteStoredAsync(entry, deleteData.Value, ct);
            // the account is gone: so are its extension links and the shared copies nobody else uses
            if (deleteData.Value)
                _services().Extensions.ForgetAccount(_services().Launch.AccountFor(entry.Edition, entry.Version));
        });
        await ReloadAsync(refreshCatalog: false);
        Status = "Версия удалена.";
    }

    /// <summary>Where the setup of an entry comes from; null when there is none (not in the download center).</summary>
    private static SetupSource? SetupSourceFor(VersionEntry entry) => entry.Release is { } release ? SetupSource.Download(release) : null;

    private async Task InstallToSystemAsync()
    {
        Error = null;
        var entry = SelectedEntry ?? throw new InvalidOperationException("No version selected.");
        var source = SetupSourceFor(entry) ?? throw new InvalidOperationException("Установщик этой версии недоступен.");
        var installer = _services().SystemInstaller;
        installer.EnsureClosed(entry.Edition);
        if (ConfirmInstallToSystem?.Invoke(entry, installer.Plan(entry.Edition, entry.Version)) != true)
            return;
        await RunBusyAsync(ct => installer.ReplaceAsync(entry.Edition, entry.Version, source, new Progress<InstallProgress>(ShowProgress), ct));
        await ReloadAsync(refreshCatalog: false);
        Status = $"Версия {entry.Version} установлена в систему для всех пользователей этого ПК.";
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        try
        {
            await work(cts.Token);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            Progress = 0;
            _cts = null;
        }
    }

    private void ShowProgress(InstallProgress p) => ShowProgress(p, null);

    private void ShowProgress(InstallProgress p, string? text)
    {
        var (label, determinate) = p.Stage switch
        {
            InstallStage.Preparing => ("Подготовка...", false),
            InstallStage.Downloading => ("Скачивание", true),
            InstallStage.ExtractingSetup => ("Распаковка архива...", false),
            InstallStage.ExtractingPackage => ("Извлечение пакета...", false),
            InstallStage.UnpackingClient => ("Распаковка клиента...", false),
            InstallStage.Copying => ("Копирование файлов", true),
            InstallStage.InstallingSystem => ("Установка в систему... Windows запросит подтверждение, это может занять несколько минут.", false),
            _ => ("Завершение...", false),
        };
        ProgressIndeterminate = !(determinate && p.Fraction is not null);
        Progress = (p.Fraction ?? 0) * 100;
        Status = text ?? (determinate && p.Fraction is { } f ? $"{label} {f:P0}" : label);
    }

    private void Register(RunningClient client)
    {
        Running.Add(client);
        Raise(nameof(RunningText));
        RunningChanged?.Invoke();
        client.Process.Exited += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            Running.Remove(client);
            Raise(nameof(RunningText));
            RunningChanged?.Invoke();
        });
        // a process that already exited before we subscribed would never raise the event
        if (client.Process.HasExited)
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Running.Remove(client);
                Raise(nameof(RunningText));
                RunningChanged?.Invoke();
            });
    }

    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(FolderText))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FolderText}\"") { UseShellExecute = true });
    }

    private void CreateShortcut()
    {
        try
        {
            var entry = SelectedEntry!;
            var exe = entry.Stored?.ExePath ?? entry.System!.ExePath;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Pilot {entry.Edition.DisplayName} {entry.Version}.lnk");
            ShortcutFile.Create(path, Environment.ProcessPath!, ShortcutFile.LaunchArguments(entry.Edition, entry.Version), exe + ",0",
                $"Pilot {entry.Edition.DisplayName} {entry.Version}");
            Status = "Ярлык создан на рабочем столе.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        _services().Log.Error("Operation failed", ex);
        Error = ex.Message;
        Status = "";
    }
}
