using System.Security.Cryptography;

namespace Ff7ec.Server;

internal static class MasterDataCrypto
{
    // Application-wide content encryption, not account or session credentials.
    private static readonly byte[] Key = Convert.FromBase64String("ZtV6ceJZqRqChLynCi0GBnl6llNbRoSZoT2QabU+SJA=");

    public static byte[] Decrypt(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || bytes.Length % 16 != 0)
            throw new InvalidDataException("Installed masterdata has invalid AES framing.");
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.DecryptCbc(bytes[16..], bytes[..16], PaddingMode.PKCS7);
    }
}
