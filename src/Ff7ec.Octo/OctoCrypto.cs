using System.Security.Cryptography;
using System.Text;

namespace Ff7ec.Octo;

public static class OctoCrypto
{
    private const byte SecureFileAesWithMd5 = 1;
    private const string DatabaseAppKey = "132mfd8uvkum78h6";
    private const string DatabaseIvSeed = "LvAUtf+tnz";

    public static byte[] DecryptSecureFile(ReadOnlySpan<byte> file)
    {
        if (file.Length < 17)
            throw new InvalidDataException("Unsupported or truncated Octo manifest SecureFile.");
        if (file[0] == 0) return file[1..].ToArray();
        if (file[0] != SecureFileAesWithMd5)
            throw new NotSupportedException($"Unsupported Octo SecureFile mode 0x{file[0]:x2}.");
        using Aes aes = CreateAes();
        byte[] decrypted = aes.DecryptCbc(file[1..], aes.IV, PaddingMode.PKCS7);
        if (decrypted.Length < 16) throw new InvalidDataException("Octo manifest payload is truncated.");
        ReadOnlySpan<byte> payload = decrypted.AsSpan(16);
        if (!MD5.HashData(payload).AsSpan().SequenceEqual(decrypted.AsSpan(0, 16)))
            throw new InvalidDataException("Octo manifest MD5 integrity check failed.");
        return payload.ToArray();
    }

    public static byte[] EncryptSecureFile(ReadOnlySpan<byte> payload)
    {
        byte[] tagged = new byte[16 + payload.Length];
        MD5.HashData(payload).CopyTo(tagged, 0);
        payload.CopyTo(tagged.AsSpan(16));
        using Aes aes = CreateAes();
        byte[] cipher = aes.EncryptCbc(tagged, aes.IV, PaddingMode.PKCS7);
        byte[] output = new byte[1 + cipher.Length];
        output[0] = SecureFileAesWithMd5;
        cipher.CopyTo(output, 1);
        return output;
    }

    private static Aes CreateAes()
    {
        Aes aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(DatabaseAppKey));
        aes.IV = MD5.HashData(Encoding.UTF8.GetBytes(DatabaseIvSeed));
        return aes;
    }
}
