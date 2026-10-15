using System.Security.Cryptography;
using System.Text;

namespace OfficeChat.Server.Security;

/// <summary>
/// Ключ устройства: выдаётся при входе, дальше устройство передаёт его вместо пароля.
/// На сервере хранится только SHA-256 ключа — по утёкшей базе войти нельзя.
/// </summary>
public static class Tokens
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
