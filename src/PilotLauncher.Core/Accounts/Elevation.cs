using System.ComponentModel;
using System.Diagnostics;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Accounts;

public sealed class AdminOperationException : Exception
{
    public AdminOperationException(string message) : base(message) { }
}

/// <summary>Account operations that need administrator rights, run in an elevated copy of the launcher (UAC prompt).</summary>
public interface IAccountProvisioner
{
    /// <summary>Makes sure the account exists and accepts its stored password. Asks for elevation only when needed.</summary>
    Task EnsureAsync(string userName, string fullName, CancellationToken ct);

    Task DeleteAsync(string userName, bool deleteProfile, CancellationToken ct);
}

public sealed class ElevatedAccountProvisioner : IAccountProvisioner
{
    private readonly ILocalAccounts _probe;
    private readonly CredentialStore _credentials;
    private readonly string _credentialsFile;
    private readonly ILauncherLog _log;

    public ElevatedAccountProvisioner(ILocalAccounts probe, CredentialStore credentials, string credentialsFile, ILauncherLog log)
    {
        _probe = probe;
        _credentials = credentials;
        _credentialsFile = credentialsFile;
        _log = log;
    }

    public async Task EnsureAsync(string userName, string fullName, CancellationToken ct)
    {
        var password = _credentials.GetOrCreate(userName);
        if (_probe.Exists(userName) && _probe.PasswordWorks(userName, password))
            return;

        _log.Info($"Account {userName} needs to be created or repaired; asking for elevation.");
        await ElevatedRunner.RunAsync(
            ["ensure-user", "--user", userName, "--fullname", fullName, "--credentials", _credentialsFile], ct).ConfigureAwait(false);
        if (!_probe.PasswordWorks(userName, password))
            throw new AdminOperationException($"The account {userName} was prepared but does not accept its password.");
    }

    public async Task DeleteAsync(string userName, bool deleteProfile, CancellationToken ct)
    {
        if (_probe.Exists(userName))
        {
            await ElevatedRunner.RunAsync(
                ["delete-user", "--user", userName, "--profile", deleteProfile ? "yes" : "no"], ct).ConfigureAwait(false);
        }

        _credentials.Remove(userName);
    }
}

/// <summary>Starts this executable again with the "runas" verb and the <c>--admin</c> switch.</summary>
public static class ElevatedRunner
{
    private const int ErrorCancelled = 1223;

    public static async Task RunAsync(IReadOnlyList<string> adminArguments, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the launcher executable.");
        var resultFile = Path.Combine(Path.GetTempPath(), $"pilotlauncher-{Guid.NewGuid():N}.result");
        var arguments = new List<string> { "--admin" };
        arguments.AddRange(adminArguments);
        arguments.AddRange(["--result", resultFile]);

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', arguments.Select(Quote)),
        };

        try
        {
            using var process = Process.Start(info) ?? throw new AdminOperationException("The elevated process did not start.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var message = File.Exists(resultFile) ? File.ReadAllText(resultFile) : $"exit code {process.ExitCode}";
                throw new AdminOperationException(message);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("The administrator confirmation was declined.", ex);
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    internal static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '"', '\t']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
}

/// <summary>Body of <c>PilotLauncher.exe --admin ...</c>; reports failure through the exit code and the result file.</summary>
public static class AdminCommands
{
    public static int Run(string[] args, ILocalAccounts accounts, SystemClientAdmin? systemClient = null, ProfileDevelopmentAdmin? development = null)
    {
        var options = ParseOptions(args.Skip(1).ToArray());
        options.TryGetValue("--result", out var resultFile);
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "ensure-user":
                    var user = LauncherAccountName(options);
                    var store = new CredentialStore(Required(options, "--credentials"), new DpapiProtector());
                    var password = store.Get(user)
                                   ?? throw new AdminOperationException($"No stored password for {user}. Run the launcher as the user who created the account.");
                    accounts.Ensure(user, password, Required(options, "--fullname"), AccountNaming.Description);
                    break;
                case "delete-user":
                    var doomed = LauncherAccountName(options);
                    var withProfile = Required(options, "--profile") == "yes";
                    // links into the shared extension store go first, so deleting the profile cannot reach through them
                    if (withProfile)
                        development?.UnlinkAll(doomed);
                    accounts.Delete(doomed, withProfile);
                    break;
                case "link-development":
                    var linker = development ?? throw new AdminOperationException("Связь с папкой Development здесь недоступна.");
                    linker.Link(LauncherAccountName(options), Required(options, "--product"), Required(options, "--target"), Required(options, "--store"));
                    break;
                case "replace-client":
                    var client = systemClient ?? throw new AdminOperationException("Установка в систему здесь недоступна.");
                    var edition = SystemClientAdmin.TryParseEdition(Required(options, "--edition"), out var parsed)
                        ? parsed
                        : throw new AdminOperationException($"Неизвестная редакция '{options["--edition"]}'.");
                    var version = Version.TryParse(Required(options, "--version"), out var target) && target.Revision >= 0
                        ? target
                        : throw new AdminOperationException($"Неверная версия '{options["--version"]}'.");
                    client.Replace(edition, version, Required(options, "--setup"));
                    break;
                default:
                    throw new AdminOperationException($"Unknown admin command '{(args.Length > 0 ? args[0] : "")}'.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            // last stop of the elevated process: hand the reason to the caller that is waiting for us
            if (resultFile is not null)
                File.WriteAllText(resultFile, ex.Message);
            return 1;
        }
    }

    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
            map[args[i]] = args[i + 1];
        return map;
    }

    private static string LauncherAccountName(Dictionary<string, string> options)
    {
        var name = Required(options, "--user");
        return AccountNaming.IsLauncherAccount(name)
            ? name
            : throw new AdminOperationException($"'{name}' is not an account created by PilotLauncher; refusing to touch it.");
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new AdminOperationException($"Missing option {name}.");
}
