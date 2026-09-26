using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace McpServerTemplate.Testing;

/// <summary>
/// A certificate authority that exists for one test run, and a leaf certificate for each service
/// in the environment.
///
/// contract-005 · G-3 — every name the end-to-end environment serves over TLS is served with a
/// certificate this CA signed, and every client in it trusts this CA and nothing else. That is what
/// lets the server be run as shipped — validating certificates the ordinary way, with no override —
/// while a call to a host the environment does not serve fails TLS instead of reaching the internet.
///
/// Nothing of it is committed and nothing of it outlives the run. The CA's private key never leaves
/// this process: it signs the leaves and is discarded, so a leaked run directory can impersonate the
/// run's own names for a day and nothing else. Each leaf's key is written only to that leaf's own
/// directory, so a container is given its own key and not another's, and the trust directory holds
/// the CA certificate alone.
/// </summary>
public sealed class TestPki : IDisposable
{
    /// <summary>The file name of each leaf's certificate, PEM.</summary>
    public const string CertificateFile = "tls.crt";

    /// <summary>The file name of each leaf's private key, PKCS#8 PEM.</summary>
    public const string KeyFile = "tls.key";

    /// <summary>The certificate and its key in one PEM file, for servers that read a single path.</summary>
    public const string BundleFile = "tls.pem";

    /// <summary>The file name of the CA certificate inside <see cref="TrustDirectory"/>.</summary>
    public const string CaFile = "ca.pem";

    private readonly Dictionary<string, string[]> _leaves;

    private TestPki(string directory, X509Certificate2 ca, Dictionary<string, string[]> leaves)
    {
        Directory = directory;
        Ca = ca;
        _leaves = leaves;
    }

    /// <summary>The run's directory. It lies outside the repository and is deleted on dispose.</summary>
    public string Directory { get; }

    /// <summary>The CA certificate, without its key.</summary>
    public X509Certificate2 Ca { get; }

    /// <summary>A directory holding the CA certificate and nothing else — no key of any kind.</summary>
    public string TrustDirectory => Path.Combine(Directory, "trust");

    /// <summary>The CA certificate's path inside <see cref="TrustDirectory"/>.</summary>
    public string CaPath => Path.Combine(TrustDirectory, CaFile);

    /// <summary>The leaves by name, with the host names each one covers.</summary>
    public IReadOnlyDictionary<string, string[]> Leaves => _leaves;

    /// <summary>The directory holding one leaf's certificate, key and bundle.</summary>
    public string LeafDirectory(string leaf)
    {
        if (!_leaves.ContainsKey(leaf))
        {
            throw new ArgumentException($"The test PKI has no leaf named '{leaf}'.", nameof(leaf));
        }

        return Path.Combine(Directory, "leaves", leaf);
    }

    /// <summary>
    /// Generates the CA and one leaf per entry of <paramref name="leaves"/>, and writes them under
    /// <paramref name="directory"/>, which must not exist yet.
    /// </summary>
    /// <param name="directory">Where to write. Created here; on Unix readable by its owner only.</param>
    /// <param name="leaves">Each leaf's name and the DNS names its certificate covers.</param>
    /// <param name="lifetime">How long the certificates are valid; a run needs hours, not years.</param>
    public static TestPki Create(
        string directory,
        IReadOnlyDictionary<string, string[]> leaves,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(leaves);

        if (System.IO.Directory.Exists(directory))
        {
            throw new IOException($"The test PKI directory '{directory}' already exists; a run writes into a fresh one.");
        }

        CreatePrivateDirectory(directory);

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromDays(1));

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=McpServerTemplate E2E test CA, O=test only",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));

        using var caWithKey = caRequest.CreateSelfSigned(notBefore, notAfter);
        var ca = X509CertificateLoader.LoadCertificate(caWithKey.RawData);

        var trust = Path.Combine(directory, "trust");
        CreatePrivateDirectory(trust);
        WriteReadableFile(Path.Combine(trust, CaFile), ca.ExportCertificatePem() + "\n");

        var written = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, hostNames) in leaves)
        {
            if (hostNames.Length == 0)
            {
                throw new ArgumentException($"The leaf '{name}' names no host.", nameof(leaves));
            }

            using var leafKey = RSA.Create(2048);
            var request = new CertificateRequest($"CN={hostNames[0]}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var san = new SubjectAlternativeNameBuilder();
            foreach (var host in hostNames)
            {
                san.AddDnsName(host);
            }

            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caWithKey, true, false));

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;

            using var leaf = request.Create(caWithKey, notBefore, notAfter, serial);
            var certificatePem = leaf.ExportCertificatePem() + "\n";
            var keyPem = leafKey.ExportPkcs8PrivateKeyPem() + "\n";

            var leafDirectory = Path.Combine(directory, "leaves", name);
            CreatePrivateDirectory(Path.Combine(directory, "leaves"));
            CreatePrivateDirectory(leafDirectory);
            WriteReadableFile(Path.Combine(leafDirectory, CertificateFile), certificatePem);
            WriteReadableFile(Path.Combine(leafDirectory, KeyFile), keyPem);
            WriteReadableFile(Path.Combine(leafDirectory, BundleFile), certificatePem + keyPem);

            written[name] = [.. hostNames];
        }

        return new TestPki(directory, ca, written);
    }

    /// <summary>
    /// A chain policy that trusts this run's CA and nothing else. A test CA publishes no revocation
    /// list, so revocation is not checked; on Windows the check otherwise fails every chain.
    /// </summary>
    public X509ChainPolicy TrustOnlyThisCa()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(Ca);
        return policy;
    }

    /// <summary>
    /// Writes a file of the run — a realm with its client secrets, a proxy's configuration — into
    /// the run's directory, so it is deleted with the keys, and returns its path.
    /// </summary>
    public string WriteFile(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        if (Path.GetFileName(name) != name)
        {
            throw new ArgumentException($"'{name}' must be a file name, not a path.", nameof(name));
        }

        var path = Path.Combine(Directory, name);
        WriteReadableFile(path, content);
        return path;
    }

    /// <summary>Deletes the run's directory and every key in it.</summary>
    public void Dispose()
    {
        Ca.Dispose();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            // Containers are given single files through bind mounts, which the Docker daemon
            // resolves as root, so an owner-only directory keeps other local users away from the
            // keys without keeping the containers away from their own file. Mount a file from
            // here, never one of these directories: a container user could not enter it.
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A file every container user can read once it is mounted: Keycloak and the test issuer run as
    /// users of their own, not as the user that wrote the file. The mode is set, not left to the
    /// process umask, so a stricter umask cannot lock a container out of its own certificate. The
    /// owner-only directory around it is what keeps other local users away.
    /// </summary>
    private static void WriteReadableFile(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
