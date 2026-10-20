using System.Text.RegularExpressions;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Extensions;

namespace PilotLauncher.Core.Sharing;

/// <summary>
/// Folders the user opens to the isolated accounts. An isolated version runs under its own Windows user and cannot see the files
/// of the main user; a file that has to be opened in Pilot is put into one of these folders.
/// </summary>
public static class SharedFolders
{
    public const string GroupComment = "PilotLauncher: isolated accounts that may use the shared folders";

    private static readonly Regex GroupPattern = new(@"^PilotLauncher-[0-9a-f]{4}$", RegexOptions.CultureInvariant);
    private static readonly Regex OwnerPattern = new(@"^[0-9a-f]{4}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The folder we offer: in the "Public documents" of Windows, where every interactive account may already change files, so
    /// nothing has to be granted and no administrator rights are needed to create it.
    /// </summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Pilot Launcher");

    /// <summary>One local group per Windows user of the PC, so two users of the launcher never open folders to each other's accounts.</summary>
    public static string GroupName(string ownerTag) => $"PilotLauncher-{ownerTag}";

    public static bool IsGroupName(string name) => GroupPattern.IsMatch(name);

    public static bool IsOwnerTag(string tag) => OwnerPattern.IsMatch(tag);

    /// <summary>True for an isolated account made by the launcher of this owner (<c>pl&lt;tag&gt;...</c>).</summary>
    public static bool BelongsTo(string userName, string ownerTag) =>
        IsOwnerTag(ownerTag) && AccountNaming.IsLauncherAccount(userName) && string.Equals(userName.Substring(2, 4), ownerTag, StringComparison.Ordinal);

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null when the folder may be opened to the isolated accounts, otherwise why not. The same check runs in the elevated copy,
    /// which does not trust what it is asked to open: giving change rights on a system folder would let an isolated account
    /// change the system.
    /// </summary>
    /// <param name="dataDir">The data folder of the launcher (settings, passwords); never shared.</param>
    public static string? Validate(string path, string? dataDir = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "Укажите полный путь к папке, например D:\\Обмен.";
        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Путь не подходит: {ex.Message}";
        }

        var root = Path.GetPathRoot(full)!;
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return "Сетевые папки не подходят: к ним нельзя надёжно выдать права на этом ПК.";
        if (!new DriveInfo(root).IsReady || new DriveInfo(root).DriveType != DriveType.Fixed)
            return "Выберите папку на локальном жёстком диске.";
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return "Нельзя открыть весь диск целиком: выберите отдельную папку.";
        if (!Directory.Exists(full))
            return $"Папки нет: {full}.";
        if (Junction.IsLink(full))
            return "Ссылки на папки (junction, символические) не подходят: выберите саму папку.";
        // a link may also stand higher in the path (C:\Mine\Link\System32): only the real path tells where the folder is
        var linked = ThroughLink(full);
        if (linked is not null)
            return linked;

        var usersDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var protectedFolder in ProtectedExact(usersDir))
        {
            if (string.Equals(full, protectedFolder, StringComparison.OrdinalIgnoreCase))
                return $"Эту папку открывать нельзя: {protectedFolder}. Выберите вложенную папку.";
        }

        // a profile folder of any user is protected as a whole: its subfolders may be shared one by one
        if (usersDir is not null && string.Equals(Path.GetDirectoryName(full), usersDir, StringComparison.OrdinalIgnoreCase))
            return "Папку профиля пользователя целиком открывать нельзя: выберите вложенную папку.";

        foreach (var tree in ProtectedTrees(dataDir))
        {
            if (IsInside(full, tree))
                return $"Папки внутри {tree} открывать нельзя: это системные файлы или данные лаунчера.";
        }

        return null;
    }

    /// <summary>Null when the folder is where its path says; otherwise why the path cannot be trusted.</summary>
    private static string? ThroughLink(string full)
    {
        try
        {
            using var handle = NativeFolder.Open(full, forChange: false);
            var real = NativeFolder.FinalPath(handle);
            return string.Equals(Normalize(real), full, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Путь проходит через ссылку или короткое имя: на самом деле это {real}. Укажите настоящий путь.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"Не удалось проверить папку: {ex.Message}";
        }
    }

    private static IEnumerable<string> ProtectedExact(string? usersDir)
    {
        yield return Normalize(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        yield return Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (usersDir is not null)
            yield return Normalize(usersDir);
    }

    private static IEnumerable<string> ProtectedTrees(string? dataDir)
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     // downloaded Pilot versions run from here: changing them would change what every version starts
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PilotLauncher"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotLauncher"),
                     dataDir ?? "",
                 })
        {
            if (folder.Length > 0)
                yield return Normalize(folder);
        }

        // the launcher data of every Windows user of the PC, not only of the one who runs this: the elevated copy may be started by
        // another administrator
        foreach (var data in OtherProfilesLauncherData())
            yield return data;
    }

    private static IEnumerable<string> OtherProfilesLauncherData()
    {
        var usersDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (usersDir is null || !Directory.Exists(usersDir))
            yield break;
        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(usersDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var profile in profiles)
            yield return Normalize(Path.Combine(profile, "AppData", "Local", "PilotLauncher"));
    }

    private static bool IsInside(string path, string tree) =>
        string.Equals(path, tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
}
