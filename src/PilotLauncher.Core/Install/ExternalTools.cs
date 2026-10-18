using System.Diagnostics;
using System.Text;

namespace PilotLauncher.Core.Install;

/// <summary>Windows built-ins used to unpack a setup: <c>expand.exe</c> for the cabinet, <c>msiexec /a</c> for the MSI.</summary>
public static class ExternalTools
{
    public static void ExtractCabinet(string cabinetPath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
        Run(expand, $"\"{cabinetPath}\" -F:* \"{destinationDir}\"", "expand.exe");
    }

    /// <summary>Windows Installer runs one operation per machine at a time; exit code 1618 means "someone else is installing".</summary>
    private const int ErrorInstallAlreadyRunning = 1618;

    /// <summary>Administrative image: copies the files to <paramref name="targetDir"/>, installs and registers nothing.</summary>
    public static void ExtractMsi(string msiPath, string targetDir, int busyRetries = 60, int busyDelayMs = 2000)
    {
        Directory.CreateDirectory(targetDir);
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        for (var attempt = 0; ; attempt++)
        {
            var exitCode = Run(msiexec, $"/a \"{msiPath}\" /qn TARGETDIR=\"{targetDir}\"", "msiexec /a", out var output);
            if (exitCode == 0)
                return;
            if (exitCode == ErrorInstallAlreadyRunning && attempt < busyRetries)
            {
                Thread.Sleep(busyDelayMs);
                continue;
            }

            throw new IOException($"msiexec /a failed with exit code {exitCode}. {output}".TrimEnd());
        }
    }

    private static void Run(string fileName, string arguments, string title)
    {
        var exitCode = Run(fileName, arguments, title, out var output);
        if (exitCode != 0)
            throw new IOException($"{title} failed with exit code {exitCode}. {output}".TrimEnd());
    }

    private static int Run(string fileName, string arguments, string title, out string output)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {title}.");
        var text = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (text) text.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (text) text.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        output = text.ToString();
        return process.ExitCode;
    }
}
