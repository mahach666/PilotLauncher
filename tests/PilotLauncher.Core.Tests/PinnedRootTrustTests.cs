using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PilotLauncher.Core.Net;
using PilotLauncher.Core.Notes;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Tests;

[TestClass]
public class PinnedRootTrustTests
{
    private const string Host = "help.pilotems.com";
    private static readonly string[] Hosts = [Host];

    /// <summary>A throw-away root, an intermediate and a server certificate, built in memory.</summary>
    private sealed record TestChain(X509Certificate2 Root, X509Certificate2 Sub, X509Certificate2 Leaf);

    private static TestChain MakeChain(string dns = Host, int leafValidFromDays = -1, int leafValidToDays = 60, string eku = "1.3.6.1.5.5.7.3.1")
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-120), now.AddDays(365));

        using var subKey = RSA.Create(2048);
        var subRequest = new CertificateRequest("CN=Test Sub", subKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        subRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        subRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var subPublic = subRequest.Create(root, now.AddDays(-90), now.AddDays(300), Serial());
        using var sub = subPublic.CopyWithPrivateKey(subKey);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={dns}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dns);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var leaf = leafRequest.Create(sub, now.AddDays(leafValidFromDays), now.AddDays(leafValidToDays), Serial());

        return new TestChain(X509CertificateLoader.LoadCertificate(root.RawData), X509CertificateLoader.LoadCertificate(sub.RawData), leaf);
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        return serial;
    }

    private static PinnedRootTrust PinTo(TestChain chain) => new(chain.Root, Hosts);

    [TestMethod]
    public void AcceptsTheServedChainWhenItLeadsToThePinnedRoot()
    {
        var chain = MakeChain();

        Assert.IsTrue(PinTo(chain).Validate(Host, chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void AcceptsTheHostNameInAnyCase()
    {
        var chain = MakeChain();

        Assert.IsTrue(PinTo(chain).Validate("HELP.Pilotems.COM", chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void RefusesOtherHostsEvenWithAGoodChain()
    {
        var chain = MakeChain("example.com");

        Assert.IsFalse(PinTo(chain).Validate("example.com", chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(PinTo(chain).Validate(null, chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void RefusesWhenTheNameDoesNotMatchTheCertificate()
    {
        var chain = MakeChain("other.example.com");

        Assert.IsFalse(PinTo(chain).Validate(Host, chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(PinTo(MakeChain()).Validate(Host, MakeChain().Leaf, [], SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [TestMethod]
    public void RefusesAnyProblemOtherThanTheChain()
    {
        var chain = MakeChain();
        var pinned = PinTo(chain);

        Assert.IsFalse(pinned.Validate(Host, chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.IsFalse(pinned.Validate(Host, chain.Leaf, [chain.Sub], SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.IsFalse(pinned.Validate(Host, chain.Leaf, [chain.Sub], SslPolicyErrors.None));
    }

    [TestMethod]
    public void RefusesAChainFromAnotherRoot()
    {
        var pinned = PinTo(MakeChain());
        var impostor = MakeChain(); // same names, different keys

        Assert.IsFalse(pinned.Validate(Host, impostor.Leaf, [impostor.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void RefusesAnExpiredOrNotYetValidCertificate()
    {
        var expired = MakeChain(leafValidFromDays: -60, leafValidToDays: -1);
        var future = MakeChain(leafValidFromDays: 5, leafValidToDays: 60);

        Assert.IsFalse(PinTo(expired).Validate(Host, expired.Leaf, [expired.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(PinTo(future).Validate(Host, future.Leaf, [future.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void RefusesACertificateThatIsNotForServerAuthentication()
    {
        var clientOnly = MakeChain(eku: "1.3.6.1.5.5.7.3.2");

        Assert.IsFalse(PinTo(clientOnly).Validate(Host, clientOnly.Leaf, [clientOnly.Sub], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void RefusesWhenTheIntermediateIsMissing()
    {
        var chain = MakeChain();

        Assert.IsFalse(PinTo(chain).Validate(Host, chain.Leaf, [], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void CallbackKeepsWhatWindowsAcceptsAndOnlyWidensForOurHostsWhenEnabled()
    {
        var chain = MakeChain();
        var served = new X509Chain();
        served.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        served.ChainPolicy.ExtraStore.Add(chain.Sub);
        served.ChainPolicy.ExtraStore.Add(chain.Root);
        served.Build(chain.Leaf); // fails on the untrusted root, but fills ChainElements like the TLS stack does
        var enabled = true;
        var callback = PinTo(chain).CreateCallback(() => enabled, _ => Host);

        Assert.IsTrue(callback(this, chain.Leaf, served, SslPolicyErrors.None), "a chain Windows trusts stays trusted");
        Assert.IsTrue(callback(this, chain.Leaf, served, SslPolicyErrors.RemoteCertificateChainErrors));
        enabled = false;
        Assert.IsFalse(callback(this, chain.Leaf, served, SslPolicyErrors.RemoteCertificateChainErrors), "off means off");
        Assert.IsTrue(callback(this, chain.Leaf, served, SslPolicyErrors.None), "off does not break ordinary sites");
        enabled = true;
        Assert.IsFalse(PinTo(chain).CreateCallback(() => true, _ => "example.com")(this, chain.Leaf, served, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(callback(this, null, served, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(callback(this, chain.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void TheEmbeddedRussianRootIsTheOfficialOne()
    {
        var pinned = PinnedRootTrust.LoadRussianRoot();

        Assert.AreEqual(PinnedRootTrust.RussianRootSha256, pinned.Sha256);
        Assert.AreEqual("CN=Russian Trusted Root CA, O=The Ministry of Digital Development and Communications, C=RU", pinned.Subject);
        CollectionAssert.AreEqual(new[] { "help.pilotems.com" }, pinned.Hosts.ToArray());
        Assert.IsTrue(pinned.NotAfter > new DateTime(2030, 1, 1), "the root must be valid for years to come");
    }

    [TestMethod]
    public void CertificateProblemIsToldApartFromOtherNetworkErrors()
    {
        Assert.IsTrue(ReleaseNotesCatalog.IsCertificateProblem(new HttpRequestException("tls", new AuthenticationException("bad chain"))));
        Assert.IsFalse(ReleaseNotesCatalog.IsCertificateProblem(new HttpRequestException("no network")));
    }

    [TestMethod]
    public async Task NotesResultFlagsACertificateProblemWhenTheOldCopyIsShown()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "release-notes.html");
            File.Copy(TestSupport.Fixture("release-notes.sample.html"), cache);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-3)); // stale, so a refresh is tried
            var handler = new FakeFileHandler([]) { Override = _ => throw new HttpRequestException("tls", new AuthenticationException("UntrustedRoot")) };

            var result = await new ReleaseNotesCatalog(new HttpClient(handler), cache).LoadAsync(CancellationToken.None);

            Assert.IsTrue(result.CertificateProblem);
            Assert.IsNotNull(result.Error);
            Assert.IsTrue(result.Releases.Count > 0, "the saved copy is still shown");
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }

    [TestMethod]
    public void TrustIsOffByDefaultAndKeptInTheSettings()
    {
        var dir = TestSupport.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.IsFalse(LauncherSettings.Load(path).TrustRussianRoot);

            new LauncherSettings { TrustRussianRoot = true }.Save(path);

            Assert.IsTrue(LauncherSettings.Load(path).TrustRussianRoot);
        }
        finally
        {
            TestSupport.DeleteQuietly(dir);
        }
    }
}
