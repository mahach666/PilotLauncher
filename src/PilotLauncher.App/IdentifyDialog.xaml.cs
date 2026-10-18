using System.Windows;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;

namespace PilotLauncher.App;

/// <summary>Asks for the edition and version of a setup file that does not tell them itself.</summary>
public partial class IdentifyDialog : Window
{
    private readonly string _path;

    public IdentifyDialog(string path)
    {
        InitializeComponent();
        _path = path;
        Question.Text = Path.GetFileName(path);
        EditionBox.ItemsSource = Editions.All;
        EditionBox.SelectedIndex = 0;
    }

    public SetupFile? Result { get; private set; }

    /// <summary>Pilot versions have four numbers (26.9.0.61368); <see cref="Version.TryParse(string?, out Version?)"/> alone also accepts "26.9".</summary>
    public static bool TryParseVersion(string text, out Version version) =>
        Version.TryParse(text, out version!) && version.Build >= 0 && version.Revision >= 0;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseVersion(VersionBox.Text.Trim(), out var version))
        {
            ErrorText.Text = "Введите версию из четырёх чисел через точку, например 26.9.0.61368.";
            return;
        }

        Result = new SetupFile(_path, (Edition)EditionBox.SelectedItem, version);
        DialogResult = true;
    }
}
