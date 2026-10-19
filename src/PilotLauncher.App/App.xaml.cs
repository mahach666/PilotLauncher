using System.Diagnostics;
using System.Windows;
using PilotLauncher.Core.Update;
using Forms = System.Windows.Forms;

namespace PilotLauncher.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;

    public static AppServices Services { get; set; } = null!;

    /// <summary>True once the user chose Exit (or nothing is running any more): closing the window really closes it.</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Services?.Log.Error("Unhandled exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "Pilot Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Services = AppServices.Create();
        if (Environment.ProcessPath is { } exe)
            _ = LauncherUpdater.CleanUpAsync(exe, Services.Log);
        try
        {
            SelfInstaller.RecordInstalledVersion();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Services.Log.Error("Could not record the launcher version in Apps & features", ex);
        }

        _viewModel = new MainViewModel(() => Services);
        _viewModel.RunningChanged += OnRunningChanged;
        _viewModel.RestartAfterUpdate = path =>
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
            Quit();
        };
        _window = new MainWindow(_viewModel);
        _window.Closed += (_, _) => Quit();
        _window.IsVisibleChanged += (_, _) => UpdateTray();
        CreateTray();
        _window.Show();
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/launcher.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Pilot Launcher",
            Visible = false,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Показать", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("Выход", null, (_, _) => Quit());
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        _window!.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>The tray icon shows while the window is hidden and clients still run.</summary>
    private void UpdateTray()
    {
        if (_tray is not null && !IsExiting)
            _tray.Visible = _viewModel!.Running.Count > 0 && !_window!.IsVisible;
    }

    private void OnRunningChanged()
    {
        UpdateTray();
        // the window was hidden because clients were running; nothing left to label, so the launcher can go
        if (_viewModel!.Running.Count == 0 && !_window!.IsVisible)
            Quit();
    }

    private void Quit()
    {
        if (IsExiting)
            return;
        IsExiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        Shutdown();
    }
}
