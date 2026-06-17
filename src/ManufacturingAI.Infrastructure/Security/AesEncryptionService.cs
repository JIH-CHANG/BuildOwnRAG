using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace ManufacturingAI.Infrastructure.Security;

public class AesEncryptionService : IEncryptionService
{
    // Tags ciphertext produced by EncryptSecret so DecryptSecret can tell it apart
    // from legacy plaintext API keys stored before encryption was added.
    private const string SecretPrefix = "enc:v1:";

    private readonly byte[] _key;
    private readonly byte[] _iv;

    public AesEncryptionService(IConfiguration config)
    {
        var keyBase64 = config["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured.");
        var ivBase64 = config["Encryption:IV"]
            ?? throw new InvalidOperationException("Encryption:IV is not configured.");

        _key = Convert.FromBase64String(keyBase64);
        _iv = Convert.FromBase64String(ivBase64);

        if (_key.Length != 32) throw new InvalidOperationException("Encryption:Key must be 256-bit (32 bytes).");
        if (_iv.Length != 16) throw new InvalidOperationException("Encryption:IV must be 128-bit (16 bytes).");
    }

    public string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(cipherBytes);
    }

    public string Decrypt(string cipherText)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = Convert.FromBase64String(cipherText);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public string EncryptSecret(string? plainText)
        => string.IsNullOrEmpty(plainText) ? string.Empty : SecretPrefix + Encrypt(plainText);

    public string DecryptSecret(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return string.Empty;
        return storedValue.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? Decrypt(storedValue[SecretPrefix.Length..])
            : storedValue; // legacy plaintext written before encryption was added
    }
}
