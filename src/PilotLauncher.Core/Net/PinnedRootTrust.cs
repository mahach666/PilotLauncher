using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PilotLauncher.Core.Net;

/// <summary>
/// Trust in one specific root certificate, limited to named hosts, and only inside the launcher: nothing is installed in
/// Windows. Used for help.pilotems.com, which is served with a certificate of the Russian Trusted Root CA that Windows
/// does not trust by default.
/// </summary>
public sealed class PinnedRootTrust
{
    /// <summary>
    /// SHA-256 of the root certificate from https://gu-st.ru/content/lending/russian_trusted_root_ca_pem.crt. The file in
    /// the repository is checked against it when it is loaded, so a damaged or swapped file stops the program.
    /// </summary>
    public const string RussianRootSha256 = "D26D2D0231B7C39F92CC738512BA54103519E4405D68B5BD703E9788CA8ECF31";

    public static readonly IReadOnlyList<string> RussianRootHosts = ["help.pilotems.com"];

    private const string ResourceName = "PilotLauncher.Core.Net.russian_trusted_root_ca.crt";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private readonly X509Certificate2 _root;
    private readonly HashSet<string> _hosts;

    public PinnedRootTrust(X509Certificate2 root, IEnumerable<string> hosts)
    {
        _root = root;
        _hosts = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
    }

    public string Subject => _root.Subject;
    public string Sha256 => Convert.ToHexString(_root.GetCertHash(HashAlgorithmName.SHA256));
    public string Sha1 => _root.Thumbprint;
    public DateTime NotAfter => _root.NotAfter;
    public IReadOnlyCollection<string> Hosts => _hosts;

    public static PinnedRootTrust LoadRussianRoot()
    {
        using var stream = typeof(PinnedRootTrust).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidDataException($"The embedded certificate {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        var root = X509Certificate2.CreateFromPem(reader.ReadToEnd());
        var actual = Convert.ToHexString(root.GetCertHash(HashAlgorithmName.SHA256));
        if (!string.Equals(actual, RussianRootSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The embedded root certificate has SHA-256 {actual}, expected {RussianRootSha256}.");
        return new PinnedRootTrust(root, RussianRootHosts);
    }

    /// <summary>
    /// True only when Windows rejected nothing but the chain (not the name, not a missing certificate), the host is one of
    /// ours, and the served chain leads to the pinned root with a valid, unexpired server certificate.
    /// </summary>
    /// <remarks>shortcut: revocation is not checked, the Russian CA's CRL/OCSP servers are often unreachable; add it if that changes.</remarks>
    public bool Validate(string? host, X509Certificate2 leaf, IEnumerable<X509Certificate2> servedChain, SslPolicyErrors errors)
    {
        if (host is null || !_hosts.Contains(host))
            return false;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors)
            return false;
        if (!leaf.MatchesHostname(host, allowWildcards: true, allowCommonName: false))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        foreach (var certificate in servedChain)
            chain.ChainPolicy.ExtraStore.Add(certificate);

        return chain.Build(leaf) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(_root.RawData);
    }

    /// <summary>
    /// For <c>SocketsHttpHandler.SslOptions</c>. Everything Windows accepts stays accepted; the pinned root only widens
    /// that for our hosts, and only while <paramref name="isEnabled"/> says the user allowed it.
    /// </summary>
    public RemoteCertificateValidationCallback CreateCallback(Func<bool> isEnabled) =>
        CreateCallback(isEnabled, sender => (sender as SslStream)?.TargetHostName);

    internal RemoteCertificateValidationCallback CreateCallback(Func<bool> isEnabled, Func<object, string?> hostOf) => (sender, certificate, chain, errors) =>
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (!isEnabled() || certificate is null || chain is null)
            return false;
        var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Validate(hostOf(sender), leaf, chain.ChainElements.Select(e => e.Certificate), errors);
    };
}
