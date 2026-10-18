using System.Collections.ObjectModel;
using PilotLauncher.App.Infrastructure;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;

namespace PilotLauncher.App;

public sealed class ExtensionRow
{
    public ExtensionRow(ExtensionStatus status, AsyncCommand install, AsyncCommand remove)
    {
        Status = status;
        InstallCommand = install;
        RemoveCommand = remove;
    }

    public ExtensionStatus Status { get; }
    public AsyncCommand InstallCommand { get; }
    public AsyncCommand RemoveCommand { get; }

    public string Name => Status.Source.DisplayName;
    public string Description => Status.Source.Description;

    public string StateText => Status.State switch
    {
        ExtensionState.NotInstalled => Status.Latest is { } l ? $"не установлено, последняя {l.Tag}, загрузка {Format.Size(l.Size)}" : "не установлено",
        ExtensionState.UpToDate => $"установлено {Status.InstalledTag} (последняя версия)",
        ExtensionState.UpdateAvailable => $"установлено {Status.InstalledTag}, доступно обновление {Status.Latest!.Tag}",
        ExtensionState.Foreign => "папка с таким именем уже есть, создана не лаунчером" +
            (Status.Latest is { } f ? $"; на GitHub {f.Tag}, загрузка {Format.Size(f.Size)}" : ""),
        _ => "последняя версия неизвестна: нет связи с GitHub",
    };

    public string InstallText => Status.State switch
    {
        ExtensionState.UpdateAvailable => "Обновить",
        ExtensionState.Foreign => "Заменить",
        _ => "Установить",
    };

    public bool CanInstall => Status.Latest is not null && Status.State is ExtensionState.NotInstalled or ExtensionState.UpdateAvailable or ExtensionState.Foreign;
    public bool CanRemove => Status.State is ExtensionState.UpToDate or ExtensionState.UpdateAvailable;
}

/// <summary>The "Extensions" block of the window: what the selected version has and the buttons to change it.</summary>
public sealed class ExtensionsViewModel : ObservableObject
{
    private readonly Func<AppServices> _services;
    private readonly Func<VersionEntry?> _current;
    private readonly Func<Func<CancellationToken, Task>, Task> _runBusy;
    private readonly Func<bool> _isIdle;
    private readonly Action<InstallProgress> _showProgress;
    private readonly Action<string> _setStatus;
    private readonly Action<Exception> _showError;
    private bool _visible;
    private string _header = "Расширения";
    private string? _note;

    public ExtensionsViewModel(
        Func<AppServices> services, Func<VersionEntry?> current, Func<Func<CancellationToken, Task>, Task> runBusy,
        Func<bool> isIdle, Action<InstallProgress> showProgress, Action<string> setStatus, Action<Exception> showError)
    {
        _isIdle = isIdle;
        _services = services;
        _current = current;
        _runBusy = runBusy;
        _showProgress = showProgress;
        _setStatus = setStatus;
        _showError = showError;
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, () => _isIdle() && HasUpdates, showError);
    }

    public ObservableCollection<ExtensionRow> Rows { get; } = [];
    public AsyncCommand UpdateAllCommand { get; }

    /// <summary>Set by the window: asks whether a folder the launcher did not make may be moved aside. False means no.</summary>
    public Func<string, bool>? ConfirmReplace { get; set; }

    public bool IsVisible { get => _visible; private set => Set(ref _visible, value); }
    public string Header { get => _header; private set => Set(ref _header, value); }
    public string? Note { get => _note; private set { Set(ref _note, value); Raise(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasUpdates => Rows.Any(r => r.Status.State == ExtensionState.UpdateAvailable);

    /// <summary>Re-reads whether the buttons may be used: they are off while the launcher is busy with something else.</summary>
    public void RaiseCommandStates()
    {
        UpdateAllCommand.RaiseCanExecuteChanged();
        foreach (var row in Rows)
        {
            row.InstallCommand.RaiseCanExecuteChanged();
            row.RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshAsync(bool forceNetwork)
    {
        var entry = _current();
        var services = _services();
        var target = entry is { IsReady: true } ? services.Extensions.TargetFor(entry) : null;
        if (target is null)
        {
            Rows.Clear();
            IsVisible = false;
            return;
        }

        try
        {
            var statuses = await services.Extensions.GetStatusAsync(target, forceNetwork, CancellationToken.None);
            if (!ReferenceEquals(_current(), entry))
                return; // the selection moved on while GitHub answered
            Rows.Clear();
            foreach (var status in statuses)
                Rows.Add(new ExtensionRow(status, new AsyncCommand(() => InstallAsync(status), _isIdle, _showError), new AsyncCommand(() => RemoveAsync(status), _isIdle, _showError)));

            Header = $"Расширения: {target.Label}";
            var problems = statuses.Where(s => s.Error is not null).Select(s => $"{s.Source.DisplayName}: {s.Error}").ToList();
            var notes = new List<string>();
            if (problems.Count > 0)
                notes.Add("Не удалось проверить обновления на GitHub (показаны сохранённые данные): " + string.Join("; ", problems));
            if (services.Extensions.NeedsLink(target))
                notes.Add("Первая установка в отдельный профиль попросит подтверждение Windows: нужно один раз связать профиль с общей папкой расширений.");
            notes.Add("Расширения хранятся в одной общей копии, в папку версии добавляется только ссылка. После изменений перезапустите Pilot этой версии.");
            Note = string.Join(Environment.NewLine, notes);
            IsVisible = true;
            Raise(nameof(HasUpdates));
            UpdateAllCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Error("Could not read the extensions", ex);
            Rows.Clear();
            Note = $"Не удалось прочитать папку Development: {ex.Message}";
            IsVisible = true;
        }
    }

    private async Task InstallAsync(ExtensionStatus status)
    {
        var (entry, target, services) = Context();
        var release = status.Latest ?? throw new InvalidOperationException("Нет данных о последнем релизе.");
        var takeOver = false;
        if (status.State == ExtensionState.Foreign)
        {
            var path = Path.Combine(target.Directory, status.Source.FolderName);
            if (ConfirmReplace?.Invoke(path) != true)
                return;
            takeOver = true;
        }

        await _runBusy(ct => services.Extensions.InstallAsync(target, status.Source, release, takeOver,
            new Progress<InstallProgress>(_showProgress), ct));
        _setStatus($"{status.Source.DisplayName} {release.Tag} установлено для {entry.Edition.DisplayName} {entry.Version}. Перезапустите Pilot этой версии.");
        await RefreshAsync(forceNetwork: false);
    }

    private async Task RemoveAsync(ExtensionStatus status)
    {
        var (entry, target, services) = Context();
        await _runBusy(ct => services.Extensions.RemoveAsync(target, status.Source, ct));
        _setStatus($"{status.Source.DisplayName} удалено для {entry.Edition.DisplayName} {entry.Version}.");
        await RefreshAsync(forceNetwork: false);
    }

    private async Task UpdateAllAsync()
    {
        var (entry, target, services) = Context();
        var updates = Rows.Select(r => r.Status).Where(s => s.State == ExtensionState.UpdateAvailable && s.Latest is not null).ToList();
        await _runBusy(async ct =>
        {
            foreach (var status in updates)
            {
                await services.Extensions.InstallAsync(target, status.Source, status.Latest!, takeOverForeign: false,
                    new Progress<InstallProgress>(_showProgress), ct);
            }
        });
        _setStatus($"Расширения обновлены для {entry.Edition.DisplayName} {entry.Version}. Перезапустите Pilot этой версии.");
        await RefreshAsync(forceNetwork: false);
    }

    private (VersionEntry Entry, DevelopmentTarget Target, AppServices Services) Context()
    {
        var entry = _current() ?? throw new InvalidOperationException("Версия не выбрана.");
        var services = _services();
        var target = services.Extensions.TargetFor(entry) ?? throw new InvalidOperationException("Эта версия не установлена на ПК.");
        return (entry, target, services);
    }
}
