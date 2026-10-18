using System.Text;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class AccountTests
{
    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().ToArray();
        public byte[] Unprotect(byte[] data) => data.Reverse().ToArray();
    }

    private sealed class FakeAccounts : ILocalAccounts
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, string> Existing { get; } = new();

        public bool Exists(string userName) => Existing.ContainsKey(userName);
        public bool PasswordWorks(string userName, string password) => Existing.TryGetValue(userName, out var p) && p == password;

        public void Ensure(string userName, string password, string fullName, string description)
        {
            Calls.Add($"ensure {userName} {fullName}");
            Existing[userName] = password;
        }

        public void Delete(string userName, bool deleteProfile)
        {
            Calls.Add($"delete {userName} profile={deleteProfile}");
            Existing.Remove(userName);
        }
    }

    [TestMethod]
    public void AccountNamesFitWindowsLimitForEveryEditionAndRealisticVersions()
    {
        var tag = AccountNaming.OwnerTag("S-1-5-21-1-2-3-1001");
        foreach (var edition in Editions.All)
        {
            foreach (var version in new[] { "15.0.11.14613", "26.13.0.62219", "99.99.0.999999" })
            {
                var name = AccountNaming.UserName(tag, edition, Version.Parse(version));
                Assert.IsTrue(name.Length <= AccountNaming.MaxLength, name);
                Assert.IsTrue(AccountNaming.IsLauncherAccount(name), name);
            }
        }
    }

    [TestMethod]
    public void AccountNamesDifferByOwnerEditionAndVersion()
    {
        var ice = Editions.Get(EditionId.IceEnterprise);
        var bim = Editions.Get(EditionId.Bim);
        var v = new Version(26, 13, 0, 62219);
        var a = AccountNaming.OwnerTag("S-1-5-21-1");
        var b = AccountNaming.OwnerTag("S-1-5-21-2");

        Assert.AreNotEqual(a, b);
        Assert.AreEqual(AccountNaming.UserName(a, ice, v), AccountNaming.UserName(a, ice, v));
        Assert.AreNotEqual(AccountNaming.UserName(a, ice, v), AccountNaming.UserName(b, ice, v));
        Assert.AreNotEqual(AccountNaming.UserName(a, ice, v), AccountNaming.UserName(a, bim, v));
        Assert.AreNotEqual(AccountNaming.UserName(a, ice, v), AccountNaming.UserName(a, ice, new Version(26, 9, 0, 61368)));
    }

    [TestMethod]
    public void AccountNameTooLongIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            AccountNaming.UserName("abcd", Editions.Get(EditionId.Ice), new Version(100, 100, 0, 99999999)));

    [TestMethod]
    [DataRow("Administrator")]
    [DataRow("HP")]
    [DataRow("plab12E26x13")]
    [DataRow("plAB12E26x13x62219")]
    [DataRow("pl_ab12_E_26")]
    [DataRow("plab12X26x13x62219")]
    public void OnlyLauncherAccountsAreRecognised(string name) => Assert.IsFalse(AccountNaming.IsLauncherAccount(name));

    [TestMethod]
    public void GeneratedPasswordsAreLongMixedAndDifferent()
    {
        var all = Enumerable.Range(0, 50).Select(_ => PasswordGenerator.Create()).ToList();

        Assert.AreEqual(50, all.Distinct().Count());
        foreach (var p in all)
        {
            Assert.AreEqual(32, p.Length);
            Assert.IsTrue(p.Any(char.IsLower) && p.Any(char.IsUpper) && p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c)), p);
        }
    }

    [TestMethod]
    public void CredentialsPersistProtectedAndCanBeRemoved()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "credentials.json");
            var store = new CredentialStore(path, new ReversingProtector());

            Assert.IsNull(store.Get("plab12E26x13x62219"));
            var password = store.GetOrCreate("plab12E26x13x62219");
            Assert.AreEqual(password, store.GetOrCreate("plab12E26x13x62219"));
            Assert.AreEqual(password, new CredentialStore(path, new ReversingProtector()).Get("plab12E26x13x62219"));
            Assert.IsFalse(File.ReadAllText(path).Contains(password), "the password must not be stored in clear text");

            store.Remove("plab12E26x13x62219");
            Assert.IsNull(store.Get("plab12E26x13x62219"));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void DpapiRoundTrips()
    {
        var protector = new DpapiProtector();
        var data = Encoding.UTF8.GetBytes("secret-value");

        var sealedData = protector.Protect(data);

        CollectionAssert.AreNotEqual(data, sealedData);
        CollectionAssert.AreEqual(data, protector.Unprotect(sealedData));
    }

    [TestMethod]
    public void AdminCommandCreatesAccountWithTheStoredPassword()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var credentials = Path.Combine(dir, "credentials.json");
            var password = new CredentialStore(credentials, new DpapiProtector()).GetOrCreate("plab12E26x13x62219");
            var accounts = new FakeAccounts();
            var result = Path.Combine(dir, "result.txt");

            var code = AdminCommands.Run(
                ["ensure-user", "--user", "plab12E26x13x62219", "--fullname", "Pilot ICE Enterprise 26.13.0.62219", "--credentials", credentials, "--result", result],
                accounts);

            Assert.AreEqual(0, code);
            CollectionAssert.AreEqual(new[] { "ensure plab12E26x13x62219 Pilot ICE Enterprise 26.13.0.62219" }, accounts.Calls);
            Assert.AreEqual(password, accounts.Existing["plab12E26x13x62219"]);
            Assert.IsFalse(File.Exists(result));
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminCommandRefusesAccountsItDidNotCreate()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var accounts = new FakeAccounts();
            var result = Path.Combine(dir, "result.txt");

            var code = AdminCommands.Run(["delete-user", "--user", "Administrator", "--profile", "yes", "--result", result], accounts);

            Assert.AreEqual(1, code);
            Assert.AreEqual(0, accounts.Calls.Count);
            StringAssert.Contains(File.ReadAllText(result), "Administrator");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminCommandReportsMissingPassword()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var result = Path.Combine(dir, "result.txt");
            var code = AdminCommands.Run(
                ["ensure-user", "--user", "plab12E26x13x62219", "--fullname", "x", "--credentials", Path.Combine(dir, "none.json"), "--result", result],
                new FakeAccounts());

            Assert.AreEqual(1, code);
            StringAssert.Contains(File.ReadAllText(result), "No stored password");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void AdminCommandRejectsUnknownCommand()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var result = Path.Combine(dir, "result.txt");
            Assert.AreEqual(1, AdminCommands.Run(["format-disk", "--result", result], new FakeAccounts()));
            StringAssert.Contains(File.ReadAllText(result), "Unknown admin command");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public async Task ProvisionerSkipsElevationWhenTheAccountAlreadyWorks()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var credentialsFile = Path.Combine(dir, "credentials.json");
            var store = new CredentialStore(credentialsFile, new ReversingProtector());
            var accounts = new FakeAccounts();
            accounts.Existing["plab12E26x13x62219"] = store.GetOrCreate("plab12E26x13x62219");
            var provisioner = new ElevatedAccountProvisioner(accounts, store, credentialsFile, Core.Logging.NullLog.Instance);

            // would throw if it tried to start an elevated process: there is no UI in a test
            await provisioner.EnsureAsync("plab12E26x13x62219", "x", CancellationToken.None);

            Assert.AreEqual(0, accounts.Calls.Count);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void RealAccountServiceTellsExistingFromMissingAccounts()
    {
        // read-only checks: nothing is created or changed
        var service = new LocalAccountService();

        Assert.IsTrue(service.Exists(Environment.UserName));
        Assert.IsFalse(service.Exists("pl-no-such-account-" + Guid.NewGuid().ToString("N")[..8]));
        Assert.IsFalse(service.PasswordWorks("pl-no-such-account-" + Guid.NewGuid().ToString("N")[..8], "x"));
    }

    [TestMethod]
    public void QuotingKeepsArgumentsWithSpacesTogether()
    {
        Assert.AreEqual("plain", ElevatedRunner.Quote("plain"));
        Assert.AreEqual("\"Pilot ICE 26.13\"", ElevatedRunner.Quote("Pilot ICE 26.13"));
        Assert.AreEqual("\"a\\\"b\"", ElevatedRunner.Quote("a\"b"));
        Assert.AreEqual("\"\"", ElevatedRunner.Quote(""));
    }
}
