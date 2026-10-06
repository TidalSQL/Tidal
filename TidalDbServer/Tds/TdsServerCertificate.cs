namespace TidalDbServer.Tds
{
    using System;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;

    /// <summary>
    /// Provides the X.509 certificate the TDS endpoint presents during the TLS handshake. A 2048-bit
    /// self-signed certificate is generated once per process and cached. Because it is self-signed,
    /// clients must connect with <c>TrustServerCertificate=True</c> (SSMS: "Trust server certificate").
    /// The certificate is re-imported from a PFX blob so that, on Windows, the private key is available
    /// to SslStream (a freshly created self-signed cert otherwise exposes only an ephemeral key, which
    /// SslStream rejects when authenticating as server).
    /// </summary>
    internal static class TdsServerCertificate
    {
        private static readonly Lazy<X509Certificate2> Certificate = new(Create);

        public static X509Certificate2 Instance => Certificate.Value;

        private static X509Certificate2 Create()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=TidalDb",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Mark the certificate for the TLS Server Authentication EKU.
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));

            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));

            return new X509Certificate2(
                created.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.Exportable);
        }
    }
}
