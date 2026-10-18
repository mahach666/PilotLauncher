using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using PilotLauncher.Core.Launch;

namespace PilotLauncher.App;

/// <summary>
/// Per-user installation without administrator rights: the single-file executable goes to
/// %LOCALAPPDATA%\Programs\PilotLauncher, plus a Start menu shortcut and an "Apps &amp; features" entry.
/// Downloaded versions and settings are never touched.
/// </summary>
public static class SelfInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PilotLauncher";
    private const string ExeName = "PilotLauncher.exe";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PilotLauncher");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Pilot Launcher.lnk");

    /// <summary>A copy named <c>*-Setup.exe</c> installs itself when started without arguments.</summary>
    public static bool IsSetupName(string? exePath) =>
        Path.GetFileNameWithoutExtension(exePath)?.EndsWith("-Setup", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A published single-file exe has no assembly file next to it; a development build does.</summary>
    private static bool IsSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "PilotLauncher.dll"));

    public static int Run(bool install, bool quiet)
    {
        try
        {
            var message = install ? Install() : Uninstall();
            if (!quiet)
                MessageBox.Show(message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!quiet)
                MessageBox.Show(ex.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static string Install()
    {
        if (!IsSingleFile)
            throw new InvalidOperationException("Установка работает только из собранного одиночного файла PilotLauncher.exe, не из папки разработки.");

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(source), InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, InstalledExe, overwrite: true);

        ShortcutFile.Create(ShortcutPath, InstalledExe, "", InstalledExe + ",0", "Pilot Launcher");

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Pilot Launcher");
        key.SetValue("DisplayVersion", typeof(SelfInstaller).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        key.SetValue("Publisher", "mahach666");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return $"Pilot Launcher установлен в {InstallDir}. Ярлык есть в меню «Пуск».";
    }

    private static string Uninstall()
    {
        if (File.Exists(ShortcutPath))
            File.Delete(ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        // the running exe cannot delete itself: a short-lived cmd does it after we have exited
        var cleanup = $"/c ping 127.0.0.1 -n 3 >nul & rd /s /q \"{InstallDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cleanup) { CreateNoWindow = true, UseShellExecute = false });
        return "Pilot Launcher удалён. Скачанные версии и настройки не тронуты.";
    }
}
