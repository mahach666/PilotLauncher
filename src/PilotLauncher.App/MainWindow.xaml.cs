using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;
using Forms = System.Windows.Forms;

namespace PilotLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ConfirmDelete = ConfirmDelete;
        viewModel.ConfirmTrustRussianRoot = ConfirmTrustRussianRoot;
        viewModel.Extensions.ConfirmReplace = ConfirmReplaceExtensionFolder;
        viewModel.PickFiles = PickFiles;
        viewModel.PickFolder = PickFolder;
        viewModel.AskSetup = AskSetup;
        viewModel.ConfirmInstallToSystem = ConfirmInstallToSystem;
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
        var dialog = new SettingsWindow(settings.StoreRoot, settings.SetupFolder, settings.Prerelease) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var modeChanged = dialog.Prerelease != settings.Prerelease;
            settings.StoreRoot = dialog.StoreRoot;
            settings.SetupFolder = dialog.SetupFolder;
            settings.Prerelease = dialog.Prerelease;
            settings.Save(App.Services.Paths.SettingsFile);
            App.Services = AppServices.Create();
            if (modeChanged)
                await _viewModel.PrereleaseChangedAsync();
            else
                await _viewModel.ReloadAsync(refreshCatalog: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!App.IsExiting && CloseRequestedWhileRunning)
        {
            // keep the process: it puts the version label on the windows of the running clients
            e.Cancel = true;
            Hide();
        }
    }
}
