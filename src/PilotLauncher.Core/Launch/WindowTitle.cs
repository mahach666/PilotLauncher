using System.Runtime.InteropServices;
using System.Text;

namespace PilotLauncher.Core.Launch;

public sealed record WindowRef(IntPtr Handle, string Title);

public interface IWindowApi
{
    /// <summary>Visible top-level windows without an owner that belong to the process.</summary>
    IReadOnlyList<WindowRef> MainWindows(int processId);

    void SetTitle(IntPtr handle, string title);
}

public static class TitleDecorator
{
    /// <summary>The new title, or null when it must stay as it is (empty, or already carries the label).</summary>
    public static string? Decorate(string current, string label)
    {
        if (string.IsNullOrWhiteSpace(current))
            return null;
        var suffix = $" [{label}]";
        return current.EndsWith(suffix, StringComparison.Ordinal) ? null : current + suffix;
    }
}

/// <summary>Keeps "[edition version]" at the end of a client's window titles; Pilot rewrites its titles while it runs.</summary>
public static class TitleWatcher
{
    public static async Task WatchAsync(int processId, string label, IWindowApi windows, Func<bool> hasExited, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !hasExited())
        {
            foreach (var window in windows.MainWindows(processId))
            {
                var decorated = TitleDecorator.Decorate(window.Title, label);
                if (decorated is not null)
                    windows.SetTitle(window.Handle, decorated);
            }

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public sealed class Win32WindowApi : IWindowApi
{
    private const uint WmSetText = 0x000C;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint GwOwner = 4;

    public IReadOnlyList<WindowRef> MainWindows(int processId)
    {
        var found = new List<WindowRef>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == processId && IsWindowVisible(handle) && GetWindow(handle, GwOwner) == IntPtr.Zero)
            {
                var title = new StringBuilder(512);
                GetWindowText(handle, title, title.Capacity);
                found.Add(new WindowRef(handle, title.ToString()));
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void SetTitle(IntPtr handle, string title) =>
        // the window belongs to another user's process: a plain SetWindowText would not cross the process boundary
        SendMessageTimeout(handle, WmSetText, IntPtr.Zero, title, SmtoAbortIfHung, 1000, out _);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
}
