using System.Windows;
using PilotLauncher.Core.Library;

namespace PilotLauncher.App;

public partial class DeleteDialog : Window
{
    public DeleteDialog(VersionEntry entry)
    {
        InitializeComponent();
        Question.Text = $"Удалить копию {entry.Edition.DisplayName} {entry.Version}?";
    }

    public bool DeleteData => DeleteDataBox.IsChecked == true;

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
