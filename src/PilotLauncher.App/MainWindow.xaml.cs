using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Sharing;
using PilotLauncher.Core.Storage;
using Forms = System.Windows.Forms;

namespace PilotLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        var settings = App.Services.Settings;
        var area = SystemParameters.WorkArea;
        (Width, Height) = WindowSizing.Choose(settings.WindowWidth, settings.WindowHeight, area.Width, area.Height, MinWidth, MinHeight);
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ConfirmDelete = ConfirmDelete;
        viewModel.ConfirmTrustRussianRoot = ConfirmTrustRussianRoot;
        viewModel.Extensions.ConfirmReplace = ConfirmReplaceExtensionFolder;
        viewModel.PickFiles = PickFiles;
        viewModel.PickFolder = PickFolder;
        viewModel.AskSetup = AskSetup;
        viewModel.ConfirmInstallToSystem = ConfirmInstallToSystem;
        viewModel.ConfirmUninstallFromSystem = ConfirmUninstallFromSystem;
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>Raised when the window is closed by the user while clients are still running: the app keeps running in the tray.</summary>
    public bool CloseRequestedWhileRunning => _viewModel.Running.Count > 0;

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("Start failed", ex);
            MessageBox.Show(this, ex.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool? ConfirmDelete(VersionEntry entry)
    {
        var dialog = new DeleteDialog(entry) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.DeleteData : null;
    }

    private bool ConfirmReplaceExtensionFolder(string path) =>
        MessageBox.Show(this,
            $"Папка уже есть, и создана не лаунчером:\n{path}\n\nЛаунчер не удаляет её, а переместит рядом с общим хранилищем расширений (папка _replaced) и поставит на её место ссылку на общую копию.\n\nПродолжить?",
            "Заменить папку расширения", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool ConfirmTrustRussianRoot(Core.Net.PinnedRootTrust root) =>
        MessageBox.Show(this,
            $"Лаунчер будет доверять корневому сертификату Минцифры России, но только при обращении к {string.Join(", ", root.Hosts)} (заметки о выпуске).\n\n" +
            "Windows, браузеры и другие программы не меняются: сертификат в систему не устанавливается. Отключить доверие можно ссылкой в разделе «Что нового».\n\n" +
            $"Сертификат: {root.Subject}\nSHA-256: {root.Sha256}\nДействует до: {root.NotAfter:dd.MM.yyyy}\n\nПродолжить?",
            "Доверие сертификату Минцифры", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Установщики Pilot",
            Filter = "Установщики Pilot (*.zip;*.exe)|*.zip;*.exe|Все файлы|*.*",
            Multiselect = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    private string? PickFolder(string? start)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Папка для файла установщика (например, флешка)",
            ShowNewFolderButton = true,
            SelectedPath = start ?? "",
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private SetupFile? AskSetup(string path)
    {
        var dialog = new IdentifyDialog(path) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("FileMenu");
        menu.DataContext = _viewModel;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.IsIdle && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await _viewModel.AddFilesAsync(files);
    }

    private bool ConfirmUninstallFromSystem(VersionEntry entry) =>
        MessageBox.Show(this,
            $"Удалить {entry.Edition.DisplayName} {entry.Version} из системы?\n\n" +
            "Версия будет удалена настоящим деинсталлятором Pilot для всех пользователей этого ПК. Windows запросит подтверждение (права администратора). " +
            "Закройте Pilot этой редакции заранее.\n\n" +
            (entry.IsInStore ? "Копия этой версии, скачанная лаунчером, останется и по-прежнему запускается в отдельном профиле." : "Версия пропадёт из списка, если её нет в центре загрузок."),
            "Удаление из системы", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool ConfirmInstallToSystem(VersionEntry entry, SystemInstallPlan plan) =>
        new InstallToSystemDialog(entry, plan) { Owner = this }.ShowDialog() == true;

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("ActionsMenu");
        menu.DataContext = _viewModel;
        menu.PlacementTarget = (UIElement)sender;
        // above the button, right edges aligned: the button sits at the window's right edge
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height), PopupPrimaryAxis.None)];
        menu.IsOpen = true;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.Settings;
        var oldShared = settings.SharedFolders.ToList();
        var dialog = new SettingsWindow(settings.StoreRoot, settings.SetupFolder, settings.Prerelease, settings.CheckUpdates, oldShared, _viewModel.CheckForUpdateNowAsync) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var modeChanged = dialog.Prerelease != settings.Prerelease;
            var checkTurnedOn = dialog.CheckUpdates && !settings.CheckUpdates;

            // the folders are opened (or closed) first: if Windows is not allowed to do it, the list stays as it was
            var added = dialog.SharedFolders.Where(p => !oldShared.Any(o => SharedFolders.SamePath(o, p))).ToList();
            var removed = oldShared.Where(o => !dialog.SharedFolders.Any(p => SharedFolders.SamePath(o, p))).ToList();
            string? sharedProblem = null;
            if (added.Count > 0 || removed.Count > 0)
            {
                try
                {
                    await App.Services.SharedFolders.ApplyAsync(added, removed, CancellationToken.None);
                    settings.SharedFolders = dialog.SharedFolders.ToList();
                }
                catch (OperationCanceledException)
                {
                    sharedProblem = "Общие папки не изменены: вы отклонили запрос Windows.";
                }
                catch (Exception ex) when (ex is AdminOperationException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    App.Services.Log.Error("Shared folders were not changed", ex);
                    sharedProblem = $"Общие папки не изменены: {ex.Message}";
                }
            }

            settings.StoreRoot = dialog.StoreRoot;
            settings.SetupFolder = dialog.SetupFolder;
            settings.Prerelease = dialog.Prerelease;
            settings.CheckUpdates = dialog.CheckUpdates;
            settings.Save(App.Services.Paths.SettingsFile);
            App.Services = AppServices.Create();
            if (sharedProblem is not null)
                MessageBox.Show(this, sharedProblem, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (modeChanged)
                await _viewModel.PrereleaseChangedAsync();
            else
                await _viewModel.ReloadAsync(refreshCatalog: false);
            if (checkTurnedOn)
                await _viewModel.CheckForUpdateAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Remembers the size for the next start. A maximized or minimized window keeps the size it returns to.</summary>
    private void SaveWindowSize()
    {
        var settings = App.Services.Settings;
        var size = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
        settings.WindowWidth = size.Width;
        settings.WindowHeight = size.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        try
        {
            settings.Save(App.Services.Paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // losing the size must not stop the window from closing
            App.Services.Log.Error("Could not save the window size", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveWindowSize();
        if (!App.IsExiting && CloseRequestedWhileRunning)
        {
            // keep the process: it puts the version label on the windows of the running clients
            e.Cancel = true;
            Hide();
        }
    }
}
