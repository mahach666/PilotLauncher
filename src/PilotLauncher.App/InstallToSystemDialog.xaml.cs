using System.Windows;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;

namespace PilotLauncher.App;

public partial class InstallToSystemDialog : Window
{
    public InstallToSystemDialog(VersionEntry entry, SystemInstallPlan plan)
    {
        InitializeComponent();
        Question.Text = $"Установить {entry.Edition.DisplayName} {entry.Version} в систему?";
        // the myAdmin package installs no virtual disk driver (its MSI has no such actions)
        var driver = entry.Edition.Id == EditionId.MyAdmin ? "" : " Будет перезапущен драйвер виртуального диска.";
        Details.Text = "Установка идёт настоящим установщиком Pilot и может занять несколько минут." + driver +
                       " Windows запросит подтверждение (права администратора). Закройте Pilot этой редакции заранее.";
        Action.Text = plan.Action switch
        {
            SystemClientAction.Upgrade =>
                $"Установленная версия {plan.Installed} будет заменена на {entry.Version} для всех пользователей этого ПК.",
            SystemClientAction.UninstallThenInstall =>
                $"Установленная версия {plan.Installed} новее выбранной, а установщик не ставит старую версию поверх новой. " +
                $"Поэтому сначала будет удалена {plan.Installed}, затем установлена {entry.Version}, для всех пользователей этого ПК.",
            _ => $"Версия {entry.Version} будет установлена для всех пользователей этого ПК.",
        };
    }

    private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
