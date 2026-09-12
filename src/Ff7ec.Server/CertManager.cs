using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ff7ec.Server;

/// <summary>
/// Generates and persists a private, local "FF7EC Offline CA" root certificate plus a
/// single leaf certificate (SAN = every hostname the server answers for) signed by it.
/// The root's public half is what the launcher installs into the OS trust store; the
/// game only ever talks to the leaf. Nothing here depends on mitmproxy - this is a
/// self-contained CA dedicated to the offline server, so it works long after any
/// capture tooling is gone.
/// </summary>
public static class CertManager
{
    // Local-only PFX password. The .pfx files live in a folder the user controls; this
    // exists only to satisfy the X509 export/import API surface, not as a real secret.
    private const string PfxPassword = "ff7ec-offline";

    public static (X509Certificate2 Leaf, X509Certificate2 RootPublic) EnsureCertificates(
        string certDirectory, IReadOnlyList<string> hostNames)
    {
        Directory.CreateDirectory(certDirectory);

        var rootPfxPath = Path.Combine(certDirectory, "ff7ec-offline-ca.pfx");
        var rootCerPath = Path.Combine(certDirectory, "ff7ec-offline-ca.cer");
        var leafPfxPath = Path.Combine(certDirectory, "ff7ec-offline-leaf.pfx");

        X509Certificate2 root = File.Exists(rootPfxPath)
            ? LoadPfx(rootPfxPath)
            : CreateRoot(rootPfxPath, rootCerPath);

        X509Certificate2 leaf = File.Exists(leafPfxPath) && LeafCoversAllHosts(leafPfxPath, hostNames)
            ? LoadPfx(leafPfxPath)
            : CreateLeaf(root, leafPfxPath, hostNames);

        var rootPublicOnly = new X509Certificate2(root.Export(X509ContentType.Cert));
        return (leaf, rootPublicOnly);
    }

    private static bool LeafCoversAllHosts(string leafPfxPath, IReadOnlyList<string> hostNames)
    {
        using var cert = LoadPfx(leafPfxPath);
        var sanExtension = cert.Extensions["2.5.29.17"]; // Subject Alternative Name
        if (sanExtension is null) return false;
        var sanText = sanExtension.Format(false);
        return hostNames.All(h => sanText.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static X509Certificate2 LoadPfx(string path) =>
        new X509Certificate2(path, PfxPassword, X509KeyStorageFlags.Exportable);

    private static X509Certificate2 CreateRoot(string pfxPath, string cerPath)
    {
        // Personal, non-commercial preservation tool - a private root CA that only this
        // machine will ever trust, purely so Kestrel can present a leaf cert the game's
        // own TLS stack accepts for the tracked hostnames.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=FF7EC Offline Server CA",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(20);
        using var root = req.CreateSelfSigned(notBefore, notAfter);

        File.WriteAllBytes(pfxPath, root.Export(X509ContentType.Pfx, PfxPassword));
        File.WriteAllBytes(cerPath, root.Export(X509ContentType.Cert));

        return LoadPfx(pfxPath);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 root, string leafPfxPath, IReadOnlyList<string> hostNames)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={hostNames[0]}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var host in hostNames)
            sanBuilder.AddDnsName(host);
        req.CertificateExtensions.Add(sanBuilder.Build());

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // TLS Server Authentication
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);

        using var leafPublic = req.Create(root, notBefore, notAfter, serial);
        using var leafWithKey = leafPublic.CopyWithPrivateKey(rsa);

        File.WriteAllBytes(leafPfxPath, leafWithKey.Export(X509ContentType.Pfx, PfxPassword));
        return LoadPfx(leafPfxPath);
    }
}
