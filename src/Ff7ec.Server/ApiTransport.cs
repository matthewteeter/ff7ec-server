using System.Security.Cryptography;
using K4os.Compression.LZ4.Streams;

namespace Ff7ec.Server;

internal static class ApiTransport
{
    private static readonly byte[] ClientKey = Convert.FromBase64String("Gs69+UiZDGBrjzj0uGq/m6mFs66bBUAP5ykHOROesZ4=");
    private static readonly byte[] ServerKey = Convert.FromBase64String("CMMnsenXvr7izAFborJvCZHwFrG40sykNgUSqgJ99+A=");

    public static byte[] DecodeRequest(byte[] body) => Decode(body, ClientKey);
    public static byte[] DecodeResponse(byte[] body) => Decode(body, ServerKey);

    public static byte[] EncodeResponse(byte[] plain, IHeaderDictionary headers)
    {
        using var compressedStream = new MemoryStream();
        using (var encoder = LZ4Stream.Encode(compressedStream))
            encoder.Write(plain);
        var compressed = compressedStream.ToArray();
        headers["X-Content-Hash"] = Convert.ToBase64String(SHA256.HashData(compressed)).Replace('+', '-').Replace('/', '_');

        using var aes = Aes.Create();
        aes.Key = ServerKey;
        aes.GenerateIV();
        using var output = new MemoryStream();
        output.Write(aes.IV);
        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            crypto.Write(compressed);
            crypto.FlushFinalBlock();
        }
        return output.ToArray();
    }

    private static byte[] Decode(byte[] body, byte[] key)
    {
        if (body.Length < 32) throw new InvalidDataException("Encrypted payload is too short.");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = body[..16];
        using var input = new MemoryStream(body, 16, body.Length - 16);
        using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var decoder = LZ4Stream.Decode(crypto);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}
