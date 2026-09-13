namespace Ff7ec.Server;

internal sealed record ProtoField(int Number, int WireType, byte[] Value)
{
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, ((ulong)Number << 3) | (uint)WireType);
        if (WireType == 2)
        {
            WriteVarint(stream, (ulong)Value.Length);
            stream.Write(Value);
        }
        else if (WireType == 0)
        {
            stream.Write(Value);
        }
        else
        {
            throw new NotSupportedException($"Unsupported protobuf wire type {WireType}.");
        }
        return stream.ToArray();
    }

    public static ProtoField LengthDelimited(int number, byte[] value) => new(number, 2, value);
    public static ProtoField Varint(int number, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, value);
        return new ProtoField(number, 0, stream.ToArray());
    }

    internal static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}

internal static class ProtobufWire
{
    public static List<ProtoField> Parse(ReadOnlySpan<byte> bytes)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var key = ReadVarint(bytes, ref offset);
            var number = checked((int)(key >> 3));
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0:
                {
                    var start = offset;
                    ReadVarint(bytes, ref offset);
                    fields.Add(new ProtoField(number, wireType, bytes[start..offset].ToArray()));
                    break;
                }
                case 2:
                {
                    var length = checked((int)ReadVarint(bytes, ref offset));
                    if (length < 0 || offset + length > bytes.Length)
                        throw new InvalidDataException("Invalid protobuf length-delimited field.");
                    fields.Add(new ProtoField(number, wireType, bytes.Slice(offset, length).ToArray()));
                    offset += length;
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported protobuf wire type {wireType}.");
            }
        }
        return fields;
    }

    public static byte[] Encode(IEnumerable<ProtoField> fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
            stream.Write(field.Encode());
        return stream.ToArray();
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length)
                throw new InvalidDataException("Truncated protobuf varint.");
            var b = bytes[offset++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    public static long ReadInt64(ProtoField field)
    {
        if (field.WireType != 0) throw new InvalidDataException("Expected protobuf varint.");
        var offset = 0;
        return unchecked((long)ReadVarint(field.Value, ref offset));
    }
}
