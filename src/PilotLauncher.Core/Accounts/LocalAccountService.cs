using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PilotLauncher.Core.Accounts;

public interface ILocalAccounts
{
    bool Exists(string userName);

    /// <summary>True when the account exists and accepts this password. Needs no administrator rights.</summary>
    bool PasswordWorks(string userName, string password);

    /// <summary>Creates the account or resets the password of an existing one. Administrator rights required.</summary>
    void Ensure(string userName, string password, string fullName, string description);

    /// <summary>Administrator rights required.</summary>
    void Delete(string userName, bool deleteProfile);
}

/// <summary>Local accounts through netapi32/userenv. Accounts are plain users, never administrators.</summary>
public sealed class LocalAccountService : ILocalAccounts, Sharing.ILocalGroups
{
    private const int NerrGroupNotFound = 2220;
    private const int ErrorNoSuchAlias = 1376;
    private const int NerrGroupExists = 2223;
    private const int ErrorAliasExists = 1379;
    private const int ErrorMemberInAlias = 1378;

    bool Sharing.ILocalGroups.Exists(string group)
    {
        var rc = NetLocalGroupGetInfo(null, group, 0, out var buffer);
        if (buffer != IntPtr.Zero)
            NetApiBufferFree(buffer);
        return rc switch
        {
            NerrSuccess => true,
            NerrGroupNotFound or ErrorNoSuchAlias => false,
            _ => throw new Win32Exception(rc, $"Cannot query group '{group}'."),
        };
    }

    void Sharing.ILocalGroups.Create(string group, string comment)
    {
        var info = new LocalGroupInfo1 { Name = group, Comment = comment };
        var rc = NetLocalGroupAdd(null, 1, ref info, out _);
        if (rc is not (NerrSuccess or NerrGroupExists or ErrorAliasExists))
            throw new Win32Exception(rc, $"Could not create the group '{group}' (NetApi error {rc}).");
    }

    void Sharing.ILocalGroups.AddMember(string group, string user)
    {
        var members = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{user}" };
        var rc = NetLocalGroupAddMembers(null, group, 3, ref members, 1);
        if (rc is not (NerrSuccess or ErrorMemberInAlias))
            throw new Win32Exception(rc, $"Could not add '{user}' to the group '{group}' (NetApi error {rc}).");
    }

    SecurityIdentifier Sharing.ILocalGroups.SidOf(string group) =>
        (SecurityIdentifier)new NTAccount(Environment.MachineName, group).Translate(typeof(SecurityIdentifier));

    private const int NerrSuccess = 0;
    private const int NerrUserNotFound = 2221;
    private const int UserPrivUser = 1;
    private const uint UfScript = 0x1;
    private const uint UfPasswdCantChange = 0x40;
    private const uint UfDontExpirePasswd = 0x10000;
    private const string UsersGroupSid = "S-1-5-32-545";
    private const string HiddenAccountsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public bool Exists(string userName)
    {
        var rc = NetUserGetInfo(null, userName, 0, out var buffer);
        if (buffer != IntPtr.Zero)
            NetApiBufferFree(buffer);
        return rc switch
        {
            NerrSuccess => true,
            NerrUserNotFound => false,
            _ => throw new Win32Exception(rc, $"Cannot query account '{userName}'."),
        };
    }

    public bool PasswordWorks(string userName, string password)
    {
        if (LogonUser(userName, ".", password, LogonInteractive, ProviderDefault, out var token))
        {
            CloseHandle(token);
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        // wrong password, unknown user, disabled/locked/expired account: the account is not usable as it is
        if (error is 1326 or 1327 or 1328 or 1329 or 1330 or 1331 or 1909 or 1793)
            return false;
        throw new Win32Exception(error, $"Cannot check the password of '{userName}'.");
    }

    public void Ensure(string userName, string password, string fullName, string description)
    {
        if (Exists(userName))
        {
            var passwordInfo = new UserInfo1003 { Password = password };
            Check(NetUserSetInfo(null, userName, 1003, ref passwordInfo, out _), $"set password of '{userName}'");
        }
        else
        {
            var info = new UserInfo1
            {
                Name = userName,
                Password = password,
                Priv = UserPrivUser,
                Comment = description,
                Flags = UfScript | UfPasswdCantChange | UfDontExpirePasswd,
            };
            Check(NetUserAdd(null, 1, ref info, out _), $"create account '{userName}'");
            var members = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{userName}" };
            Check(NetLocalGroupAddMembers(null, UsersGroupName(), 3, ref members, 1), $"add '{userName}' to Users");
        }

        var full = new UserInfo1011 { FullName = fullName };
        Check(NetUserSetInfo(null, userName, 1011, ref full, out _), $"set full name of '{userName}'");
        Hide(userName);
    }

    public void Delete(string userName, bool deleteProfile)
    {
        if (!Exists(userName))
            return;
        if (deleteProfile)
        {
            var sid = new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;
            // 2 = ERROR_FILE_NOT_FOUND: the account never logged on, there is no profile
            if (!DeleteProfile(sid, null, null) && Marshal.GetLastWin32Error() is var error and not 2)
                throw new Win32Exception(error, $"Не удалось удалить профиль пользователя {userName} (код Windows {error}). Закройте окна Pilot этой версии и повторите.");
        }

        Check(NetUserDel(null, userName), $"delete account '{userName}'");
        using var list = Registry.LocalMachine.OpenSubKey(HiddenAccountsKey, writable: true);
        list?.DeleteValue(userName, throwOnMissingValue: false);
    }

    private static void Hide(string userName)
    {
        using var list = Registry.LocalMachine.CreateSubKey(HiddenAccountsKey, writable: true);
        list.SetValue(userName, 0, RegistryValueKind.DWord);
    }

    private static string UsersGroupName()
    {
        var account = (NTAccount)new SecurityIdentifier(UsersGroupSid).Translate(typeof(NTAccount));
        return account.Value[(account.Value.IndexOf('\\') + 1)..];
    }

    private static void Check(int code, string action)
    {
        if (code != NerrSuccess)
            throw new Win32Exception(code, $"Could not {action} (NetApi error {code}).");
    }

    private const int LogonInteractive = 2;
    private const int ProviderDefault = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string Password;
        public uint PasswordAge;
        public int Priv;
        public string? HomeDir;
        public string? Comment;
        public uint Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003 { public string Password; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1011 { public string FullName; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3 { public string DomainAndName; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupInfo1
    {
        public string Name;
        public string Comment;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetInfo(string? server, string groupName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAdd(string? server, int level, ref LocalGroupInfo1 buffer, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, int level, ref UserInfo1 buffer, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "NetUserSetInfo")]
    private static extern int NetUserSetInfo(string? server, string userName, int level, ref UserInfo1003 buffer, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "NetUserSetInfo")]
    private static extern int NetUserSetInfo(string? server, string userName, int level, ref UserInfo1011 buffer, out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? server, string userName);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string groupName, int level, ref LocalGroupMembersInfo3 buffer, int totalEntries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteProfile(string sidString, string? profilePath, string? computerName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string userName, string? domain, string password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
