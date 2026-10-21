using System.Windows;
using PilotLauncher.Core.Sharing;
using Forms = System.Windows.Forms;

namespace PilotLauncher.App;

public partial class SettingsWindow : Window
{
    private readonly Func<Task<UpdateCheckResult>> _checkNow;

    /// <param name="checkNow">Asks GitHub for a newer launcher right now (the "Check now" button).</param>
    public SettingsWindow(string storeRoot, string? setupFolder, bool prerelease, bool checkUpdates, IEnumerable<string> sharedFolders,
        Func<Task<UpdateCheckResult>> checkNow)
    {
        InitializeComponent();
        _checkNow = checkNow;
        VersionText.Text = $"Pilot Launcher {SelfInstaller.CurrentVersion.ToString(3)}";
        // the window grows with its content but never beyond the screen: the rest is scrolled
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;
        PathBox.Text = storeRoot;
        SetupBox.Text = setupFolder ?? "";
        PrereleaseBox.IsChecked = prerelease;
        CheckUpdatesBox.IsChecked = checkUpdates;
        foreach (var folder in sharedFolders)
            SharedList.Items.Add(folder);
        UpdateSharedHint();
    }

    public string StoreRoot { get; private set; } = "";

    /// <summary>The shared folders as the user left the list.</summary>
    public IReadOnlyList<string> SharedFolders => SharedList.Items.Cast<string>().ToList();

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckResult.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        CheckResult.Text = "Проверяю...";
        try
        {
            var result = await _checkNow();
            CheckResult.Text = result.Message;
            if (result.State == UpdateCheckState.Failed)
                CheckResult.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("Update check failed", ex);
            CheckResult.Text = $"Не удалось проверить: {ex.Message}";
            CheckResult.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private void UpdateSharedHint()
    {
        var hasDefault = SharedList.Items.Cast<string>().Any(p => SharedFolderService.IsDefault(p));
        CreateDefaultButton.IsEnabled = !hasDefault;
        SharedHint.Text = hasDefault
            ? "Папка по умолчанию создана в «Общих документах» Windows: права ей выдавать не нужно. Для остальных папок Windows один раз запросит подтверждение (права администратора)."
            : $"Папка по умолчанию: {SharedFolderService.DefaultFolder}. Для любой другой папки Windows один раз запросит подтверждение (права администратора).";
    }

    private void SharedList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RemoveSharedButton.IsEnabled = OpenSharedButton.IsEnabled = SharedList.SelectedItem is not null;
    }

    private void AddShared_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { ShowNewFolderButton = true, Description = "Папка, которую нужно открыть изолированным версиям Pilot" };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        if (Core.Sharing.SharedFolders.Validate(dialog.SelectedPath) is { } problem)
        {
            ErrorText.Text = problem;
            return;
        }

        ErrorText.Text = "";
        AddToList(Core.Sharing.SharedFolders.Normalize(dialog.SelectedPath));
    }

    private void CreateDefaultShared_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AddToList(App.Services.SharedFolders.CreateDefault());
            ErrorText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Не удалось создать папку: {ex.Message}";
        }
    }

    private void AddToList(string folder)
    {
        if (!SharedList.Items.Cast<string>().Any(p => Core.Sharing.SharedFolders.SamePath(p, folder)))
            SharedList.Items.Add(folder);
        UpdateSharedHint();
    }

    private void RemoveShared_Click(object sender, RoutedEventArgs e)
    {
        if (SharedList.SelectedItem is string folder)
            SharedList.Items.Remove(folder);
        UpdateSharedHint();
    }

    private void OpenShared_Click(object sender, RoutedEventArgs e)
    {
        if (SharedList.SelectedItem is not string folder)
            return;
        if (!Directory.Exists(folder))
        {
            ErrorText.Text = $"Папки больше нет: {folder}.";
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public bool Prerelease => PrereleaseBox.IsChecked == true;

    public bool CheckUpdates => CheckUpdatesBox.IsChecked == true;

    /// <summary>Null when the field is empty: no installer folder.</summary>
    public string? SetupFolder { get; private set; }

    /// <summary>The folder is only read, so network and removable drives are fine; an empty path means "none".</summary>
    public static string? ValidateSetupFolder(string path) =>
        path.Length == 0 || Path.IsPathFullyQualified(path) ? null : "Укажите полный путь, например E:\\Установщики, или оставьте поле пустым.";

    /// <summary>Null when the path is usable; otherwise the reason it is not.</summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "Укажите полный путь, например D:\\Pilot.";
        var root = Path.GetPathRoot(path)!;
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return "Сетевые папки не подходят: отдельные пользователи Windows не смогут запускать оттуда клиент.";
        if (!new DriveInfo(root).IsReady || new DriveInfo(root).DriveType != DriveType.Fixed)
            return "Выберите папку на локальном жёстком диске.";
        return null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = PathBox.Text, ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            PathBox.Text = dialog.SelectedPath;
    }

    private void BrowseSetup_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = SetupBox.Text, ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            SetupBox.Text = dialog.SelectedPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        var problem = Validate(path);
        if (problem is not null)
        {
            ErrorText.Text = problem;
            return;
        }

        var setup = SetupBox.Text.Trim();
        var setupProblem = ValidateSetupFolder(setup);
        if (setupProblem is not null)
        {
            ErrorText.Text = setupProblem;
            return;
        }

        StoreRoot = Path.GetFullPath(path);
        SetupFolder = setup.Length == 0 ? null : Path.GetFullPath(setup);
        DialogResult = true;
    }
}
