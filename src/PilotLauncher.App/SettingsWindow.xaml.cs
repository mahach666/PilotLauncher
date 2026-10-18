using System.Windows;
using Forms = System.Windows.Forms;

namespace PilotLauncher.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(string storeRoot, string? setupFolder)
    {
        InitializeComponent();
        PathBox.Text = storeRoot;
        SetupBox.Text = setupFolder ?? "";
    }

    public string StoreRoot { get; private set; } = "";

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
