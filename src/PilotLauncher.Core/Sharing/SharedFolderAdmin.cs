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
    /// <summary>
    /// Gives <paramref name="who"/> change rights on the folder. The folder is opened once; <paramref name="check"/> gets the real
    /// path of exactly that object (no link left in it) and may refuse it, and the rights are written through the same handle, so
    /// nothing can be swapped for another folder between the check and the change.
    /// </summary>
    /// <exception cref="AdminOperationException">The path goes through a link, or <paramref name="check"/> refused the folder.</exception>
    public static void Grant(string directory, SecurityIdentifier who, Func<string, string?>? check = null)
    {
        using var handle = NativeFolder.Open(directory, forChange: true);
        var real = NativeFolder.FinalPath(handle);
        if (!SharedFolders.SamePath(real, directory))
            throw new AdminOperationException($"{directory}: путь проходит через ссылку, на самом деле это {real}; отказ.");
        if (check?.Invoke(real) is { } problem)
            throw new AdminOperationException($"{real}: {problem}");
        NativeFolder.ChangeAccess(handle, security => security.AddAccessRule(Rule(who)));
    }

    /// <summary>Takes back what <see cref="Grant"/> gave. It only removes entries of <paramref name="who"/>, so a link is harmless here.</summary>
    public static void Revoke(string directory, SecurityIdentifier who)
    {
        using var handle = NativeFolder.Open(directory, forChange: true);
        NativeFolder.ChangeAccess(handle, security => security.RemoveAccessRuleAll(Rule(who)));
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
    private readonly string _dataDir;

    /// <param name="dataDir">
    /// The data folder of this launcher, worked out by the elevated copy itself: what has to stay closed is not taken from the
    /// arguments of whoever started it.
    /// </param>
    public SharedFolderAdmin(ILocalAccounts accounts, ILocalGroups groups, string dataDir)
    {
        _accounts = accounts;
        _groups = groups;
        _dataDir = dataDir;
    }

    /// <summary>
    /// Opens the folders in <paramref name="add"/> and closes the ones in <paramref name="remove"/>. Everything is checked before
    /// anything is changed, so a refused folder leaves the others untouched.
    /// </summary>
    public void Apply(string ownerTag, IReadOnlyList<string> users, IReadOnlyList<string> add, IReadOnlyList<string> remove)
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
            if (SharedFolders.Validate(folder, _dataDir) is { } problem)
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
            // checked once more on the opened folder itself: the first check was on a path, which can change by the time of the grant
            foreach (var folder in add)
                FolderAcl.Grant(SharedFolders.Normalize(folder), sid, real => SharedFolders.Validate(real, _dataDir));
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
