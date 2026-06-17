namespace ManufacturingAI.Infrastructure.Security;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);

    /// <summary>
    /// Encrypts a stored secret (e.g. a tenant API key), tagging the ciphertext so
    /// <see cref="DecryptSecret"/> can distinguish it from legacy plaintext values
    /// written before encryption was introduced. Empty input stays empty so callers
    /// can keep treating "" as "not configured".
    /// </summary>
    string EncryptSecret(string? plainText);

    /// <summary>
    /// Decrypts a value produced by <see cref="EncryptSecret"/>. Untagged values are
    /// returned unchanged (legacy plaintext rows that predate encryption).
    /// </summary>
    string DecryptSecret(string? storedValue);
}
