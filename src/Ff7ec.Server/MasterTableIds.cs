using K4os.Compression.LZ4.Streams;
using MessagePack;

namespace Ff7ec.Server;

internal static class MasterTableIds
{
    public static HashSet<long> Read(byte[] encrypted)
    {
        using var source = new MemoryStream(MasterDataCrypto.Decrypt(encrypted));
        using var decoder = LZ4Stream.Decode(source);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        var reader = new MessagePackReader(output.ToArray().AsMemory());
        int records = reader.ReadArrayHeader();
        var ids = new HashSet<long>();
        for (int row = 0; row < records; row++)
        {
            int fields = reader.ReadArrayHeader();
            if (fields == 0) throw new InvalidDataException("An installed masterdata record has no ID.");
            long id = reader.ReadInt64();
            if (!ids.Add(id)) throw new InvalidDataException($"Duplicate installed masterdata ID: {id}.");
            for (int field = 1; field < fields; field++) reader.Skip();
        }
        if (!reader.End) throw new InvalidDataException("An installed masterdata table has trailing records.");
        return ids;
    }
}
