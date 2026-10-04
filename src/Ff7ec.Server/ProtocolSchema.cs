using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace Ff7ec.Server;

// Load only field mappings; DLL overrides are read as metadata, never executed.
internal sealed class ProtocolSchema
{
    private static readonly Dictionary<string, string> ScalarTypes = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean", ["int32"] = "System.Int32", ["int64"] = "System.Int64",
        ["uint32"] = "System.UInt32", ["uint64"] = "System.UInt64", ["string"] = "System.String",
        ["bytes"] = "Google.Protobuf.ByteString", ["enum"] = "enum",
    };
    private readonly Dictionary<string, Message> _messages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enums = new(StringComparer.Ordinal);

    public ProtocolSchema(string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            ReadSchemaFile(path);
        else
            ReadAssembly(path);
        GetMessage("ApiResponse");
        GetMessage("ApiRequest");
        GetMessage("Tables");
    }

    private void ReadAssembly(string assemblyPath)
    {
        using var input = File.OpenRead(assemblyPath);
        using var pe = new PEReader(input);
        var reader = pe.GetMetadataReader();
        var provider = new TypeNames();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = provider.GetTypeFromDefinition(reader, handle, 0);
            if (!type.BaseType.IsNil && TypeName(reader, type.BaseType, provider) == "System.Enum")
                _enums.Add(name);

            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var fieldName = reader.GetString(field.Name);
                if (!fieldName.EndsWith("FieldNumber", StringComparison.Ordinal)) continue;
                var constantHandle = field.GetDefaultValue();
                if (constantHandle.IsNil)
                    throw new InvalidDataException($"Missing protobuf field constant: {name}.{fieldName}.");
                var constant = reader.GetConstant(constantHandle);
                if (constant.TypeCode != ConstantTypeCode.Int32)
                    throw new InvalidDataException($"Invalid protobuf field constant: {name}.{fieldName}.");
                var number = reader.GetBlobReader(constant.Value).ReadInt32();
                if (number <= 0 || number > 536870911)
                    throw new InvalidDataException($"Invalid protobuf field number: {name}.{fieldName}.");
                numbers.Add(fieldName[..^"FieldNumber".Length], number);
            }
            if (numbers.Count == 0) continue;

            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var propertyName = reader.GetString(property.Name);
                if (!numbers.TryGetValue(propertyName, out var number)) continue;
                var propertyType = property.DecodeSignature(provider, null).ReturnType;
                const string repeated = "Google.Protobuf.Collections.RepeatedField`1<";
                bool isRepeated = propertyType.StartsWith(repeated, StringComparison.Ordinal) && propertyType.EndsWith('>');
                fields.Add(propertyName, new Field(number,
                    isRepeated ? propertyType[repeated.Length..^1] : propertyType, isRepeated));
            }
            if (fields.Count != numbers.Count)
                throw new InvalidDataException($"Incomplete protobuf properties for {name}.");
            _messages.Add(name, new Message(name, fields));
        }
    }

    private void ReadSchemaFile(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var document = JsonDocument.Parse(input);
            var root = Properties(document.RootElement, "protocol schema");
            if (!root.Keys.ToHashSet().SetEquals(["formatVersion", "clientBuild", "messages"]))
                throw new InvalidDataException("Protocol schema requires only formatVersion, clientBuild and messages.");
            if (root["formatVersion"].GetInt32() != 1)
                throw new InvalidDataException("Unsupported protocol schema formatVersion.");
            ValidateBuild(root["clientBuild"].GetString());
            foreach (var (name, value) in Properties(root["messages"], "messages"))
            {
                if (string.IsNullOrWhiteSpace(name) || ScalarTypes.ContainsKey(name))
                    throw new InvalidDataException($"Invalid protocol message name: {name}.");
                var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
                var numbers = new HashSet<int>();
                foreach (var (property, tuple) in Properties(value, name))
                {
                    if (string.IsNullOrWhiteSpace(property) || tuple.ValueKind != JsonValueKind.Array ||
                        tuple.GetArrayLength() is < 2 or > 3)
                        throw new InvalidDataException($"Invalid protocol field mapping: {name}.{property}.");
                    int number = tuple[0].GetInt32();
                    string type = tuple[1].GetString()
                        ?? throw new InvalidDataException($"Missing protocol field type: {name}.{property}.");
                    if (number <= 0 || number > 536870911 || number is >= 19000 and <= 19999 || !numbers.Add(number))
                        throw new InvalidDataException($"Invalid or duplicate protocol field number: {name}.{property}.");
                    bool repeated = tuple.GetArrayLength() == 3 && tuple[2].GetBoolean();
                    fields.Add(property, new Field(number, ScalarTypes.GetValueOrDefault(type) ?? type, repeated));
                }
                _messages.Add(name, new Message(name, fields));
            }
            _enums.Add("enum");
            foreach (var message in _messages.Values)
                foreach (var (property, field) in message.Fields)
                    if (!ScalarTypes.Values.Contains(field.Type) && !_messages.ContainsKey(field.Type))
                        throw new InvalidDataException($"Unresolved protocol type {field.Type} at {message.Name}.{property}.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed protocol-schema JSON.", ex);
        }
    }

    // Export only the account graph. Request payloads and the minimal session/title/
    // write acknowledgements are opaque messages: only their envelope tags are used.
    public void WriteAccountSchema(string path, string clientBuild, IEnumerable<string> listEndpoints)
    {
        ValidateBuild(clientBuild);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protocol schema output must be a .json file.");
        var lists = listEndpoints.ToArray();
        var endpoints = lists.Concat(["PostAuthSession", "PostPvtUserTitle"]).ToArray();
        var messages = new SortedDictionary<string, Message>(StringComparer.Ordinal);
        var requests = endpoints.ToDictionary(name => name, name =>
            GetField("ApiRequest", name) with { Type = "Google.Protobuf.ByteString" }, StringComparer.Ordinal);
        var responses = new Dictionary<string, Field>(StringComparer.Ordinal);
        foreach (string name in lists.Append("Common"))
        {
            var field = GetField("ApiResponse", name);
            responses.Add(name, field);
            Visit(GetMessage(field.Type));
        }
        foreach (string name in new[] { "PostAuthSession", "PostPvtUserTitle", "PostPvtStorePurchaseRestartSteam" })
            responses.Add(name, GetField("ApiResponse", name) with { Type = "Google.Protobuf.ByteString" });
        messages.Add("ApiRequest", new Message("ApiRequest", requests));
        messages.Add("ApiResponse", new Message("ApiResponse", responses));
        Visit(GetMessage("PostPvtGiftListRequest"));

        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                writer.WriteLine("{");
                writer.WriteLine("  \"formatVersion\": 1,");
                writer.WriteLine($"  \"clientBuild\": {JsonSerializer.Serialize(clientBuild)},");
                writer.WriteLine("  \"messages\": {");
                int remainingMessages = messages.Count;
                foreach (var (name, message) in messages)
                {
                    writer.WriteLine($"    {JsonSerializer.Serialize(name)}: {{");
                    int remainingFields = message.Fields.Count;
                    foreach (var (property, field) in message.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        string type = ExportType(field.Type);
                        object[] tuple = field.Repeated ? [field.Number, type, true] : [field.Number, type];
                        writer.WriteLine($"      {JsonSerializer.Serialize(property)}: {JsonSerializer.Serialize(tuple)}{(--remainingFields > 0 ? "," : "")}");
                    }
                    writer.WriteLine($"    }}{(--remainingMessages > 0 ? "," : "")}");
                }
                writer.WriteLine("  }");
                writer.WriteLine("}");
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        void Visit(Message message)
        {
            string name = ShortName(message.Name);
            if (messages.TryGetValue(name, out var existing))
            {
                if (existing.Name != message.Name)
                    throw new InvalidDataException($"Ambiguous protocol message name: {name}.");
                return;
            }
            messages.Add(name, message);
            foreach (var field in message.Fields.Values)
            {
                if (_messages.TryGetValue(field.Type, out var nested)) Visit(nested);
                else ExportType(field.Type);
            }
        }
    }

    private string ExportType(string type)
    {
        if (_enums.Contains(type)) return "enum";
        foreach (var (alias, scalar) in ScalarTypes)
            if (scalar == type) return alias;
        if (_messages.ContainsKey(type)) return ShortName(type);
        throw new InvalidDataException($"Cannot export unsupported protocol field type: {type}.");
    }

    private static string ShortName(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static void ValidateBuild(string? build)
    {
        if (!long.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
            throw new InvalidDataException("Protocol schema clientBuild must be a positive build ID.");
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement value, string location)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{location} must be an object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value))
                throw new InvalidDataException($"Duplicate protocol schema property: {location}.{property.Name}.");
        return result;
    }

    public Field GetField(string message, string property) =>
        GetMessage(message).Fields.TryGetValue(property, out var field)
            ? field
            : throw new InvalidDataException($"Protocol schema has no {message}.{property}.");

    public byte[] Encode(string message, JsonElement value) => Encode(GetMessage(message), value, message);

    private byte[] Encode(Message message, JsonElement value, string location)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{location} must be an object.");
        using var output = new MemoryStream();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var path = $"{location}.{property.Name}";
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"Duplicate JSON field: {path}.");
            if (!message.Fields.TryGetValue(property.Name, out var field))
                throw new InvalidDataException($"JSON field is absent from the current protocol schema: {path}.");
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (field.Repeated)
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"{path} must be an array or null.");
                int index = 0;
                foreach (var item in property.Value.EnumerateArray())
                    output.Write(EncodeValue(field, item, $"{path}[{index++}]").Encode());
            }
            else
            {
                output.Write(EncodeValue(field, property.Value, path).Encode());
            }
        }
        return output.ToArray();
    }

    private ProtoField EncodeValue(Field field, JsonElement value, string path)
    {
        try
        {
            if (_messages.TryGetValue(field.Type, out var nested))
                return ProtoField.LengthDelimited(field.Number, Encode(nested, value, path));
            if (_enums.Contains(field.Type))
                return ProtoField.Varint(field.Number, unchecked((ulong)checked((int)ReadSigned(value))));
            return field.Type switch
            {
                "System.Boolean" => ProtoField.Varint(field.Number, value.GetBoolean() ? 1UL : 0UL),
                "System.Int32" => ProtoField.Varint(field.Number, unchecked((ulong)checked((int)ReadSigned(value)))),
                "System.Int64" => ProtoField.Varint(field.Number, unchecked((ulong)ReadSigned(value))),
                "System.UInt32" => ProtoField.Varint(field.Number, checked((uint)ReadUnsigned(value))),
                "System.UInt64" => ProtoField.Varint(field.Number, ReadUnsigned(value)),
                "System.String" => ProtoField.LengthDelimited(field.Number, Encoding.UTF8.GetBytes(value.GetString()
                    ?? throw new InvalidDataException($"{path} cannot be null."))),
                "Google.Protobuf.ByteString" => ProtoField.LengthDelimited(field.Number, value.GetBytesFromBase64()),
                // The account schema uses integer/enum/bool/string/message fields. Fail
                // closed for other types whose wire encoding cannot be inferred here.
                _ => throw new InvalidDataException($"Unsupported protobuf property type {field.Type} at {path}."),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Invalid JSON value at {path} ({field.Type}).", ex);
        }
    }

    private static long ReadSigned(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
        : value.GetInt64();

    private static ulong ReadUnsigned(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? ulong.Parse(value.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture)
        : value.GetUInt64();

    private Message GetMessage(string name)
    {
        if (_messages.TryGetValue(name, out var exact)) return exact;
        var matches = _messages.Values.Where(message => message.Name.EndsWith("." + name, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidDataException($"Protocol schema must contain exactly one protobuf message named {name}.");
    }

    private static string TypeName(MetadataReader reader, EntityHandle handle, TypeNames provider) => handle.Kind switch
    {
        HandleKind.TypeDefinition => provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        _ => string.Empty,
    };

    internal sealed record Field(int Number, string Type, bool Repeated);
    private sealed record Message(string Name, Dictionary<string, Field> Fields);

    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            return parent.IsNil
                ? reader.GetString(type.Namespace) + "." + reader.GetString(type.Name)
                : GetTypeFromDefinition(reader, parent, rawTypeKind) + "." + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function";
    }
}
