using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OfficeChat.Server.Security;

/// <summary>
/// Сертификат HTTPS. Сервер создаёт его сам при первом запуске (самоподписанный, на 20 лет) и хранит
/// в папке данных. Клиенты запоминают его отпечаток при первом подключении и дальше доверяют только ему.
/// </summary>
public static class ServerCertificate
{
    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=OfficeChat Server, O=OfficeChat", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Dns.GetHostName());
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var pfx = created.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, pfx);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Загружаем заново из pfx: на Windows ключ созданного в памяти сертификата TLS-стек использовать не умеет.
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Отпечаток SHA-256 — по нему клиент узнаёт «свой» сервер.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
}
