using System.Security.Cryptography;
using Hail.Core.Ports;

namespace Hail.Windows.Security;

/// <summary>
/// DPAPI under the current user: what it encrypts only this Windows account on this machine
/// can decrypt, which is what makes <c>plugin-settings.json</c> safe to leave where it is.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain, byte[] purpose) =>
        ProtectedData.Protect(plain, purpose, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData, byte[] purpose) =>
        ProtectedData.Unprotect(protectedData, purpose, DataProtectionScope.CurrentUser);
}
