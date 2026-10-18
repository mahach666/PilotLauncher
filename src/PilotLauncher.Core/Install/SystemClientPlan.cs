namespace PilotLauncher.Core.Install;

public enum SystemClientAction
{
    /// <summary>Nothing of this edition is installed: just run the setup.</summary>
    Install,

    /// <summary>The target is newer: the setup replaces the installed client itself (RemoveExistingProducts).</summary>
    Upgrade,

    /// <summary>The target is older: the package refuses downgrades, so the installed client goes first.</summary>
    UninstallThenInstall,

    /// <summary>The target is what is installed already: nothing to replace.</summary>
    AlreadyInstalled,
}

/// <param name="Installed">The system client of the edition at the moment of planning; null when there is none.</param>
public sealed record SystemInstallPlan(SystemClientAction Action, Version? Installed);

public static class SystemClientPlan
{
    public static SystemClientAction Choose(Version? installed, Version target) =>
        installed is null ? SystemClientAction.Install :
        target > installed ? SystemClientAction.Upgrade :
        target < installed ? SystemClientAction.UninstallThenInstall :
        SystemClientAction.AlreadyInstalled;
}
