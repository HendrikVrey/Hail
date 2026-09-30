namespace Hail.Core.Ports;

/// <summary>
/// Encrypts a plugin's secrets for the current Windows account (DPAPI, the Sling token store's
/// pattern), so the file they are kept in is useless on another machine or to another user.
/// </summary>
public interface ISecretProtector
{
    /// <param name="purpose">Mixed into the encryption, so one secret cannot stand in for another.</param>
    byte[] Protect(byte[] plain, byte[] purpose);

    /// <summary>
    /// The plain bytes. Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// when they cannot be recovered: another account wrote them, or the purpose differs.
    /// </summary>
    byte[] Unprotect(byte[] protectedData, byte[] purpose);
}
