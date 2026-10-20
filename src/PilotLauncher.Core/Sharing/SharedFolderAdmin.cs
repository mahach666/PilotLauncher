using System.Security.AccessControl;
using System.Security.Principal;
using PilotLauncher.Core.Accounts;

namespace PilotLauncher.Core.Sharing;

/// <summary>Local groups through netapi32. Administrator rights required.</summary>
public interface ILocalGroups
{
    bool Exists(string group);

    void Create(string group, string comment);

    /// <summary>Adds the user; a user that is already a member is not an error.</summary>
    void AddMember(string group, string user);

    SecurityIdentifier SidOf(string group);
}

/// <summary>Change rights of one account or group on a folder and everything in it.</summary>
public static class FolderAcl
{
    public static void Grant(string directory, SecurityIdentifier who)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(Rule(who));
        info.SetAccessControl(security);
    }

    public static void Revoke(string directory, SecurityIdentifier who)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleAll(Rule(who));
        info.SetAccessControl(security);
    }

    public static bool Has(string directory, SecurityIdentifier who) =>
        new DirectoryInfo(directory).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(who) && r.FileSystemRights.HasFlag(FileSystemRights.Modify));

    private static FileSystemAccessRule Rule(SecurityIdentifier who) =>
        new(who, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
}

/// <summary>
/// The elevated half of "shared folders" (<c>--admin apply-shares</c>). The isolated accounts of one launcher owner are members of
/// one local group, and the group gets change rights on every shared folder, so an account made later reaches the folders at once.
/// It works only on folders that pass <see cref="SharedFolders.Validate"/> and only on accounts of that owner.
/// </summary>
public sealed class SharedFolderAdmin
{
    private readonly ILocalAccounts _accounts;
    private readonly ILocalGroups _groups;

    public SharedFolderAdmin(ILocalAccounts accounts, ILocalGroups groups)
    {
        _accounts = accounts;
        _groups = groups;
    }

    /// <summary>
    /// Opens the folders in <paramref name="add"/> and closes the ones in <paramref name="remove"/>. Everything is checked before
    /// anything is changed, so a refused folder leaves the others untouched.
    /// </summary>
    public void Apply(string ownerTag, IReadOnlyList<string> users, IReadOnlyList<string> add, IReadOnlyList<string> remove, string dataDir)
    {
        if (!SharedFolders.IsOwnerTag(ownerTag))
            throw new AdminOperationException($"Неверная метка владельца '{ownerTag}'.");
        foreach (var user in users)
        {
            if (!SharedFolders.BelongsTo(user, ownerTag))
                throw new AdminOperationException($"'{user}' не учётная запись лаунчера этого пользователя; отказ.");
        }

        foreach (var folder in add)
        {
            if (SharedFolders.Validate(folder, dataDir) is { } problem)
                throw new AdminOperationException($"{folder}: {problem}");
        }

        foreach (var folder in remove)
        {
            if (!Path.IsPathFullyQualified(folder))
                throw new AdminOperationException($"Нужен полный путь к папке: {folder}.");
        }

        var group = SharedFolders.GroupName(ownerTag);
        if (add.Count > 0)
        {
            if (!_groups.Exists(group))
                _groups.Create(group, SharedFolders.GroupComment);
            foreach (var user in users.Where(_accounts.Exists))
                _groups.AddMember(group, user);
            var sid = _groups.SidOf(group);
            foreach (var folder in add)
                FolderAcl.Grant(SharedFolders.Normalize(folder), sid);
        }

        if (remove.Count > 0 && _groups.Exists(group))
        {
            var sid = _groups.SidOf(group);
            foreach (var folder in remove.Select(SharedFolders.Normalize).Where(Directory.Exists))
                FolderAcl.Revoke(folder, sid);
        }
    }

    /// <summary>A new or repaired account joins the group of its owner when that group exists (something has been shared).</summary>
    public void JoinIfGroupExists(string user)
    {
        if (!AccountNaming.IsLauncherAccount(user))
            return;
        var group = SharedFolders.GroupName(user.Substring(2, 4));
        if (_groups.Exists(group))
            _groups.AddMember(group, user);
    }
}
