using System.Security.Principal;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Extensions;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Sharing;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class SharedFolderTests
{
    private const string Owner = "ab12";
    private const string Mine = "plab12E26x13x62219";
    private const string MineToo = "plab12M26x13x62219";
    private const string Foreign = "plcd34E26x13x62219"; // an account of another Windows user of the PC

    /// <summary>BUILTIN\Users: a real group, so the access-control tests change real access lists of temporary folders.</summary>
    private static readonly SecurityIdentifier GroupSid = new(WellKnownSidType.BuiltinUsersSid, null);

    private sealed class FakeAccounts : ILocalAccounts
    {
        public HashSet<string> Existing { get; } = [];
        public bool Exists(string userName) => Existing.Contains(userName);
        public bool PasswordWorks(string userName, string password) => true;
        public void Ensure(string userName, string password, string fullName, string description) => Existing.Add(userName);
        public void Delete(string userName, bool deleteProfile) => Existing.Remove(userName);
    }

    private sealed class FakeGroups : ILocalGroups
    {
        public Dictionary<string, List<string>> Groups { get; } = new();
        public List<string> Calls { get; } = [];

        public bool Exists(string group) => Groups.ContainsKey(group);

        public void Create(string group, string comment)
        {
            Calls.Add($"create {group}");
            Groups[group] = [];
        }

        public void AddMember(string group, string user)
        {
            Calls.Add($"add {group} {user}");
            if (!Groups[group].Contains(user))
                Groups[group].Add(user);
        }

        public SecurityIdentifier SidOf(string group) => GroupSid;
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = TestSupport.NewTempDir();
        public FakeAccounts Accounts { get; } = new();
        public FakeGroups Groups { get; } = new();
        public SharedFolderAdmin Admin { get; }
        public string DataDir => Path.Combine(Dir, "data");

        public Fixture()
        {
            Directory.CreateDirectory(DataDir);
            Admin = new SharedFolderAdmin(Accounts, Groups, DataDir);
            Accounts.Existing.Add(Mine);
            Accounts.Existing.Add(MineToo);
        }

        public string Folder(string name)
        {
            var path = Path.Combine(Dir, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => TestSupport.DeleteQuietly(Dir);
    }

    /// <summary>Entries written for the group on the folder itself (what rights inherited from a parent give is not looked at).</summary>
    private static int ExplicitRules(string folder) =>
        new DirectoryInfo(folder).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>().Count(r => r.IdentityReference.Equals(GroupSid));

    // ---- which folders may be opened

    [TestMethod]
    public void AnOrdinaryFolderIsAllowedAlsoDeepInsideAProfile()
    {
        using var fx = new Fixture();

        Assert.IsNull(SharedFolders.Validate(fx.Folder("Exchange"), fx.DataDir));
        Assert.IsNull(SharedFolders.Validate(fx.Folder("Exchange") + @"\", fx.DataDir), "a trailing slash does not matter");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow(@"Exchange")]
    [DataRow(@"..\Exchange")]
    [DataRow(@"\\server\share\Exchange")]
    public void NoPathOrARelativeOrNetworkPathIsRefused(string path) => Assert.IsNotNull(SharedFolders.Validate(path));

    [TestMethod]
    public void AFolderThatDoesNotExistOrIsAFileIsRefused()
    {
        using var fx = new Fixture();
        var file = Path.Combine(fx.Dir, "a.txt");
        File.WriteAllText(file, "x");

        StringAssert.Contains(SharedFolders.Validate(Path.Combine(fx.Dir, "nope"))!, "Папки нет");
        Assert.IsNotNull(SharedFolders.Validate(file));
    }

    [TestMethod]
    public void AWholeDriveIsRefused()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        StringAssert.Contains(SharedFolders.Validate(root)!, "весь диск");
    }

    [TestMethod]
    public void SystemFoldersAndTheirSubfoldersAreRefused()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.IsNotNull(SharedFolders.Validate(windows));
        Assert.IsNotNull(SharedFolders.Validate(Path.Combine(windows, "System32")));
        Assert.IsNotNull(SharedFolders.Validate(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.IsNotNull(SharedFolders.Validate(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)), "ProgramData itself");
    }

    [TestMethod]
    public void ProfileFoldersAndTheUsersFolderAreRefusedButTheirSubfoldersAreNot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var users = Path.GetDirectoryName(profile)!;

        Assert.IsNotNull(SharedFolders.Validate(profile), "own profile as a whole");
        Assert.IsNotNull(SharedFolders.Validate(users), "the folder of all profiles");
        Assert.IsNotNull(SharedFolders.Validate(Path.Combine(users, "Public")), "any profile folder, also the public one");
        Assert.IsNull(SharedFolders.Validate(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)), "Public\\Documents is an ordinary subfolder");
    }

    [TestMethod]
    public void TheLauncherDataAndTheVersionStoreAreRefused()
    {
        using var fx = new Fixture();
        var inside = Path.Combine(fx.DataDir, "logs");
        Directory.CreateDirectory(inside);

        Assert.IsNotNull(SharedFolders.Validate(fx.DataDir, fx.DataDir));
        Assert.IsNotNull(SharedFolders.Validate(inside, fx.DataDir), "a folder inside the data folder");
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PilotLauncher", "versions");
        if (Directory.Exists(store))
            Assert.IsNotNull(SharedFolders.Validate(store), "the downloaded versions run from here");
    }

    [TestMethod]
    public void ALinkToAFolderIsRefusedBecauseTheRightsWouldLandSomewhereElse()
    {
        using var fx = new Fixture();
        var target = fx.Folder("Real");
        var link = Path.Combine(fx.Dir, "Link");
        Junction.Create(link, target);

        StringAssert.Contains(SharedFolders.Validate(link)!, "Ссылки");
    }

    [TestMethod]
    public void TheDefaultFolderIsInThePublicDocumentsWhereEveryInteractiveAccountMayWrite()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Pilot Launcher");

        Assert.AreEqual(expected, SharedFolders.DefaultFolder);
        Assert.IsTrue(SharedFolderService.IsDefault(expected.ToUpperInvariant() + @"\"), "compared without case and trailing slash");
    }

    // ---- names

    [TestMethod]
    public void TheGroupBelongsToOneOwnerAndOnlyHisAccountsBelongToIt()
    {
        Assert.AreEqual("PilotLauncher-ab12", SharedFolders.GroupName(Owner));
        Assert.IsTrue(SharedFolders.IsGroupName("PilotLauncher-ab12"));
        Assert.IsFalse(SharedFolders.IsGroupName("Administrators"));
        Assert.IsFalse(SharedFolders.IsGroupName("PilotLauncher-ab1"));

        Assert.IsTrue(SharedFolders.BelongsTo(Mine, Owner));
        Assert.IsTrue(SharedFolders.BelongsTo(MineToo, Owner));
        Assert.IsFalse(SharedFolders.BelongsTo(Foreign, Owner), "an account of another Windows user");
        Assert.IsFalse(SharedFolders.BelongsTo("Administrator", Owner));
        Assert.IsFalse(SharedFolders.BelongsTo(Mine, "zzzz"));
    }

    // ---- access lists

    [TestMethod]
    public void GrantingAndRevokingChangesTheAccessListOfTheFolder()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");

        FolderAcl.Grant(folder, GroupSid);
        Assert.IsTrue(FolderAcl.Has(folder, GroupSid));

        Assert.AreEqual(1, ExplicitRules(folder));

        FolderAcl.Revoke(folder, GroupSid);
        Assert.AreEqual(0, ExplicitRules(folder), "the group may still hold rights inherited from the parent; only explicit entries are counted");
    }

    // ---- the elevated half

    [TestMethod]
    public void OpeningAFolderMakesTheGroupAddsTheExistingAccountsAndGrantsTheGroup()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");

        fx.Admin.Apply(Owner, [Mine, MineToo, "plab12B26x1x1"], [folder], []); // the last one has no account yet

        CollectionAssert.AreEqual(new[] { "create PilotLauncher-ab12", $"add PilotLauncher-ab12 {Mine}", $"add PilotLauncher-ab12 {MineToo}" }, fx.Groups.Calls);
        Assert.IsTrue(FolderAcl.Has(folder, GroupSid));
    }

    [TestMethod]
    public void AnExistingGroupIsNotCreatedAgain()
    {
        using var fx = new Fixture();
        fx.Groups.Groups["PilotLauncher-ab12"] = [];

        fx.Admin.Apply(Owner, [Mine], [fx.Folder("A")], []);

        Assert.IsFalse(fx.Groups.Calls.Any(c => c.StartsWith("create")));
    }

    [TestMethod]
    public void AnAccountOfAnotherOwnerOrAnUnknownOwnerIsRefusedAndNothingChanges()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");

        Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Apply(Owner, [Mine, Foreign], [folder], []));
        Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Apply("ZZZZ", [], [folder], []));
        Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Apply(Owner, ["Administrator"], [folder], []));

        Assert.AreEqual(0, fx.Groups.Calls.Count);
        Assert.AreEqual(0, ExplicitRules(folder), "the folder was not changed");
    }

    [TestMethod]
    public void OneRefusedFolderLeavesTheOthersAndTheGroupUntouched()
    {
        using var fx = new Fixture();
        var good = fx.Folder("Good");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var ex = Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Apply(Owner, [Mine], [good, windows], []));

        StringAssert.Contains(ex.Message, windows);
        Assert.AreEqual(0, fx.Groups.Calls.Count, "the group was not even created");
        Assert.AreEqual(0, ExplicitRules(good), "the good folder was not changed either");
    }

    [TestMethod]
    public void ClosingAFolderTakesTheRightsBackAndAMissingFolderIsNotAnError()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");
        fx.Admin.Apply(Owner, [Mine], [folder], []);
        Assert.IsTrue(FolderAcl.Has(folder, GroupSid));

        fx.Admin.Apply(Owner, [], [], [folder, Path.Combine(fx.Dir, "gone")]);

        Assert.AreEqual(0, ExplicitRules(folder));
    }

    [TestMethod]
    public void ClosingWithoutAnyGroupDoesNothingAndDoesNotMakeOne()
    {
        using var fx = new Fixture();

        fx.Admin.Apply(Owner, [], [], [fx.Folder("Exchange")]);

        Assert.AreEqual(0, fx.Groups.Calls.Count);
    }

    [TestMethod]
    public void ANewAccountJoinsTheGroupOnlyWhenSomethingHasBeenShared()
    {
        using var fx = new Fixture();

        fx.Admin.JoinIfGroupExists(Mine);
        Assert.AreEqual(0, fx.Groups.Calls.Count, "nothing is shared: no group, nothing to join");

        fx.Groups.Groups["PilotLauncher-ab12"] = [];
        fx.Admin.JoinIfGroupExists(Mine);
        fx.Admin.JoinIfGroupExists("Administrator");
        fx.Admin.JoinIfGroupExists(Foreign); // belongs to the group of another owner, which does not exist

        CollectionAssert.AreEqual(new[] { $"add PilotLauncher-ab12 {Mine}" }, fx.Groups.Calls);
    }

    [TestMethod]
    public void TheAdminCommandOpensFoldersAndRefusesWithoutAHandlerOrWithForeignAccounts()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");
        var result = Path.Combine(fx.Dir, "result.txt");
        int Run(SharedFolderAdmin? admin, params string[] args) => AdminCommands.Run([.. args, "--result", result], fx.Accounts, null, null, admin);

        Assert.AreEqual(1, Run(null, "apply-shares", "--owner", Owner, "--add", folder), "no handler");
        Assert.AreEqual(1, Run(fx.Admin, "apply-shares", "--owner", Owner, "--users", $"{Mine},{Foreign}", "--add", folder));
        Assert.AreEqual(0, fx.Groups.Calls.Count);

        Assert.AreEqual(0, Run(fx.Admin, "apply-shares", "--owner", Owner, "--users", $"{Mine},{MineToo}", "--add", folder));
        Assert.IsTrue(FolderAcl.Has(folder, GroupSid));
        Assert.AreEqual(1, Run(fx.Admin, "apply-shares"), "the owner is required");
        Assert.AreEqual(1, Run(fx.Admin, "apply-shares", "--owner", Owner, "--add", Path.Combine(fx.Dir, "nope")), "a folder that does not exist");
    }

    // ---- links on the way: a link higher in the path must not carry a protected folder past the rules

    [TestMethod]
    public void AFolderBehindALinkHigherInThePathIsRefused()
    {
        using var fx = new Fixture();
        var real = fx.Folder("Real");
        Directory.CreateDirectory(Path.Combine(real, "Sub"));
        var link = Path.Combine(fx.Dir, "Link");
        Junction.Create(link, real);
        try
        {
            StringAssert.Contains(SharedFolders.Validate(Path.Combine(link, "Sub"), fx.DataDir)!, "ссылк");
            Assert.IsNull(SharedFolders.Validate(Path.Combine(real, "Sub"), fx.DataDir), "the real path of the same folder is fine");
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [TestMethod]
    public void ALinkCannotCarryTheLauncherDataPastTheProtectedFolderRule()
    {
        using var fx = new Fixture();
        var logs = Path.Combine(fx.DataDir, "logs");
        Directory.CreateDirectory(logs);
        var link = Path.Combine(fx.Dir, "Innocent");
        Junction.Create(link, fx.DataDir);
        try
        {
            // lexically "Innocent\logs" is nowhere near the data folder; in fact it is a folder inside it
            Assert.IsNotNull(SharedFolders.Validate(Path.Combine(link, "logs"), fx.DataDir));
            var ex = Assert.ThrowsExactly<AdminOperationException>(() => fx.Admin.Apply(Owner, [Mine], [Path.Combine(link, "logs")], []));
            StringAssert.Contains(ex.Message, "ссылк");
            Assert.AreEqual(0, fx.Groups.Calls.Count, "nothing was created or granted");
            Assert.AreEqual(0, ExplicitRules(logs));
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [TestMethod]
    public void TheGrantItselfRefusesAPathThroughALinkEvenWhenTheEarlierCheckWasSkipped()
    {
        using var fx = new Fixture();
        var real = fx.Folder("Real");
        var sub = Path.Combine(real, "Sub");
        Directory.CreateDirectory(sub);
        var link = Path.Combine(fx.Dir, "Link");
        Junction.Create(link, real);
        try
        {
            Assert.ThrowsExactly<AdminOperationException>(() => FolderAcl.Grant(Path.Combine(link, "Sub"), GroupSid));

            Assert.AreEqual(0, ExplicitRules(sub));
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [TestMethod]
    public void TheCheckOfTheGrantGetsTheRealPathOfTheOpenedFolderAndCanStopTheChange()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");
        string? seen = null;

        var ex = Assert.ThrowsExactly<AdminOperationException>(() => FolderAcl.Grant(folder, GroupSid, real => { seen = real; return "не годится"; }));

        StringAssert.Contains(ex.Message, "не годится");
        Assert.IsTrue(SharedFolders.SamePath(folder, seen!));
        Assert.AreEqual(0, ExplicitRules(folder));
    }

    [TestMethod]
    public void TheGrantByHandleReachesWhatIsAlreadyInTheFolderAndIsNotAddedTwice()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "x");
        int InheritedOnFile() => new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: false, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>().Count(r => r.IdentityReference.Equals(GroupSid));
        var before = InheritedOnFile();

        FolderAcl.Grant(folder, GroupSid);
        FolderAcl.Grant(folder, GroupSid);

        Assert.AreEqual(1, ExplicitRules(folder), "one entry, not two");
        Assert.IsTrue(InheritedOnFile() > before, "the file that was already there got the right from its folder");
    }

    [TestMethod]
    public void TheGrantKeepsWhetherTheFolderInheritsRightsFromItsParent()
    {
        using var fx = new Fixture();
        var open = fx.Folder("Open");
        var closed = fx.Folder("Closed");
        var info = new DirectoryInfo(closed);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        info.SetAccessControl(security);

        FolderAcl.Grant(open, GroupSid);
        FolderAcl.Grant(closed, GroupSid);

        Assert.IsFalse(new DirectoryInfo(open).GetAccessControl().AreAccessRulesProtected);
        Assert.IsTrue(new DirectoryInfo(closed).GetAccessControl().AreAccessRulesProtected, "a folder that blocked inheritance still blocks it");
    }

    [TestMethod]
    public void RevokingByHandleLeavesTheOtherRightsOfTheFolder()
    {
        using var fx = new Fixture();
        var folder = fx.Folder("Exchange");
        var before = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Count;
        FolderAcl.Grant(folder, GroupSid);

        FolderAcl.Revoke(folder, GroupSid);

        Assert.AreEqual(0, ExplicitRules(folder));
        Assert.AreEqual(before, new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Count, "the rights of the owner and of the others are as they were");
    }

    /// <summary>A parent with several kinds of inheritable entries, as the temporary folders of a build machine have.</summary>
    private static string FolderWithRichInheritedRights(Fixture fx)
    {
        var parent = fx.Folder("Parent");
        var parentInfo = new DirectoryInfo(parent);
        var security = parentInfo.GetAccessControl();
        const System.Security.AccessControl.InheritanceFlags both = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), System.Security.AccessControl.FileSystemRights.ReadAndExecute, both,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), System.Security.AccessControl.FileSystemRights.FullControl, both,
            System.Security.AccessControl.PropagationFlags.InheritOnly, System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), System.Security.AccessControl.FileSystemRights.Write,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        parentInfo.SetAccessControl(security);
        var child = Path.Combine(parent, "Exchange");
        Directory.CreateDirectory(child);
        return child;
    }

    [TestMethod]
    public void GrantingAndRevokingLeavesEveryRightInheritedFromTheParentExactlyAsItWas()
    {
        using var fx = new Fixture();
        var folder = FolderWithRichInheritedRights(fx);
        string Rules() => new DirectoryInfo(folder).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        var before = Rules();

        FolderAcl.Grant(folder, GroupSid);
        FolderAcl.Grant(folder, GroupSid);
        FolderAcl.Revoke(folder, GroupSid);

        Assert.AreEqual(before, Rules(), "inherited entries are neither copied, nor lost, nor reordered");
    }

    [TestMethod]
    public void AccountsCreatedByTheAdminCommandJoinTheGroup()
    {
        using var fx = new Fixture();
        fx.Groups.Groups["PilotLauncher-ab12"] = [];
        var credentials = Path.Combine(fx.Dir, "credentials.json");
        new CredentialStore(credentials, new DpapiProtector()).GetOrCreate("plab12E26x14x62538");
        var result = Path.Combine(fx.Dir, "result.txt");

        var code = AdminCommands.Run(
            ["ensure-user", "--user", "plab12E26x14x62538", "--fullname", "Pilot ICE Enterprise 26.14", "--credentials", credentials, "--result", result],
            fx.Accounts, null, null, fx.Admin);

        Assert.AreEqual(0, code);
        CollectionAssert.AreEqual(new[] { "add PilotLauncher-ab12 plab12E26x14x62538" }, fx.Groups.Calls);
    }

    // ---- the launcher's half

    private sealed class ServiceFixture : IDisposable
    {
        public string Dir { get; } = TestSupport.NewTempDir();
        public List<IReadOnlyList<string>> Elevated { get; } = [];
        public SharedFolderService Service { get; }
        public string DataDir => Path.Combine(Dir, "data");

        public ServiceFixture(params string[] accounts)
        {
            var credentials = new CredentialStore(Path.Combine(Dir, "credentials.json"), new PlainProtector());
            foreach (var account in accounts)
                credentials.GetOrCreate(account);
            Directory.CreateDirectory(DataDir);
            Service = new SharedFolderService(Owner, credentials, DataDir, (args, _) => { Elevated.Add(args); return Task.CompletedTask; }, NullLog.Instance);
        }

        public string Folder(string name)
        {
            var path = Path.Combine(Dir, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => TestSupport.DeleteQuietly(Dir);
    }

    [TestMethod]
    public async Task TheDefaultFolderAloneNeedsNoAdministratorPrompt()
    {
        using var fx = new ServiceFixture(Mine);

        Assert.IsFalse(SharedFolderService.NeedsElevation([SharedFolders.DefaultFolder], []));
        await fx.Service.ApplyAsync([SharedFolders.DefaultFolder], [SharedFolders.DefaultFolder.ToLowerInvariant()], default);

        Assert.AreEqual(0, fx.Elevated.Count);
    }

    [TestMethod]
    public async Task AnotherFolderAsksTheElevatedCopyOnceWithEverythingItNeeds()
    {
        using var fx = new ServiceFixture(Mine, MineToo, Foreign);
        var added = fx.Folder("A");
        var removed = Path.Combine(fx.Dir, "B"); // does not have to exist any more

        Assert.IsTrue(SharedFolderService.NeedsElevation([added], []));
        await fx.Service.ApplyAsync([SharedFolders.DefaultFolder, added], [removed], default);

        var call = fx.Elevated.Single();
        CollectionAssert.AreEqual(
            new[] { "apply-shares", "--owner", Owner, "--users", $"{Mine},{MineToo}", "--add", added, "--remove", removed },
            call.ToArray(), "only this owner's accounts, the default folder is not in the list");
    }

    [TestMethod]
    public async Task ARefusedFolderIsRefusedBeforeWindowsIsAskedAnything()
    {
        using var fx = new ServiceFixture(Mine);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fx.Service.ApplyAsync([fx.Folder("A"), windows], [], default));

        StringAssert.Contains(ex.Message, windows);
        Assert.AreEqual(0, fx.Elevated.Count);
    }

    [TestMethod]
    public async Task WithoutAccountsThereAreNoUsersInTheCommand()
    {
        using var fx = new ServiceFixture();

        await fx.Service.ApplyAsync([fx.Folder("A")], [], default);

        CollectionAssert.DoesNotContain(fx.Elevated.Single().ToList(), "--users");
    }

    [TestMethod]
    public void TheSharedFoldersSurviveSavingAndOlderSettingsFilesHaveNone()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "StoreRoot": "D:\\Pilot", "CheckUpdates": false }""");
            var old = LauncherSettings.Load(path);
            Assert.AreEqual(0, old.SharedFolders.Count);

            old.SharedFolders.Add(@"D:\Обмен");
            old.Save(path);

            CollectionAssert.AreEqual(new[] { @"D:\Обмен" }, LauncherSettings.Load(path).SharedFolders);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void TheCredentialStoreListsTheAccountsItKnows()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var store = new CredentialStore(Path.Combine(dir, "c.json"), new PlainProtector());
            Assert.AreEqual(0, store.Names().Count);

            store.GetOrCreate(Mine);
            store.GetOrCreate(MineToo);
            store.Remove(Mine);

            CollectionAssert.AreEqual(new[] { MineToo }, store.Names().ToArray());
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }
}
