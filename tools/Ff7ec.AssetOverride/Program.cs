using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ff7ec.Octo;
using static Ff7ec.Octo.OctoCrypto;

return AssetOverrideProgram.Run(args);

internal static class AssetOverrideProgram
{
    private const byte SecureFileAesWithMd5 = 1;
    private const byte AssetMaskSeed = 0xAB;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Usage();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0].ToLowerInvariant();
            string configPath = GetArg(args, "--config") ?? throw new ArgumentException("Missing --config <path>.");
            OverrideConfig config = LoadConfig(configPath);
            string stateDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, config.StateDirectory));

            return command switch
            {
                "apply" => Apply(config, stateDirectory),
                "restore" => Restore(stateDirectory),
                "status" => Status(config, stateDirectory),
                _ => throw new ArgumentException($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    private static int Apply(OverrideConfig config, string stateDirectory)
    {
        EnsureGameClosed();
        ValidateConfig(config);

        string manifestPath = FindManifest(config.GameDirectory);
        byte[] source = File.ReadAllBytes(config.SourcePath);
        EnsureUnityBundle(source, config.SourcePath);
        string sourceSha256 = Hex(SHA256.HashData(source));
        if (!sourceSha256.Equals(config.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Replacement SHA-256 mismatch. Expected {config.SourceSha256}, got {sourceSha256}.");

        Directory.CreateDirectory(stateDirectory);
        string statePath = Path.Combine(stateDirectory, "state.json");
        OverrideState? previous = File.Exists(statePath)
            ? JsonSerializer.Deserialize<OverrideState>(File.ReadAllText(statePath), JsonOptions())
            : null;
        if (previous?.Applied == true)
        {
            VerifyAppliedFiles(previous, sourceSha256, verifyActiveManifest: false);
            Console.WriteLine($"Asset override is already applied: {config.AssetName}");
            return 0;
        }

        byte[] manifestFile = File.ReadAllBytes(manifestPath);
        string currentManifestSha256 = Hex(SHA256.HashData(manifestFile));
        if (previous is not null &&
            !currentManifestSha256.Equals(previous.OriginalManifestSha256, StringComparison.OrdinalIgnoreCase) &&
            !currentManifestSha256.Equals(previous.PatchedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            VerifyAppliedFiles(previous, sourceSha256, verifyActiveManifest: false);
            previous.Applied = true;
            previous.AppliedAtUtc = DateTimeOffset.UtcNow;
            WriteJsonAtomic(statePath, previous);
            Console.WriteLine($"Applied server-only override: {config.AssetName}");
            Console.WriteLine("  The game-managed manifest changed, so its on-disk copy was left untouched.");
            return 0;
        }

        byte[] payload = DecryptSecureFile(manifestFile);
        ItemMetadata current = ReadItem(payload, config.AssetName);

        if (previous is not null &&
            currentManifestSha256.Equals(previous.PatchedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            RequireReplacementMetadata(current, previous);
            VerifyAppliedFiles(previous, sourceSha256, verifyActiveManifest: true);
            if (!previous.Applied)
            {
                previous.Applied = true;
                previous.AppliedAtUtc ??= DateTimeOffset.UtcNow;
                WriteJsonAtomic(statePath, previous);
            }
            Console.WriteLine($"Asset override is already applied: {config.AssetName}");
            return 0;
        }

        RequireOriginalMetadata(current, config);
        string originalBlobPath = FindBlob(config.GameDirectory, config.OriginalMd5);
        VerifyFile(originalBlobPath, config.OriginalMd5, config.OriginalSize);
        string bucketMetaPath = Path.Combine(Path.GetDirectoryName(originalBlobPath)!, ".meta");
        byte[] expectedBucketMeta = Convert.FromHexString(config.OriginalBucketMetaHex);
        if (!File.Exists(bucketMetaPath)) WriteBytesAtomic(bucketMetaPath, expectedBucketMeta);
        else if (!File.ReadAllBytes(bucketMetaPath).AsSpan().SequenceEqual(expectedBucketMeta))
            throw new InvalidDataException($"Octo bucket metadata differs from the registered original: {bucketMetaPath}");

        byte marker = File.ReadAllBytes(originalBlobPath)[0];
        byte[] wrapped = EncryptAsset(source, config.AssetName, marker);
        string replacementMd5 = Hex(MD5.HashData(wrapped));
        string replacementBlobPath = Path.Combine(Path.GetDirectoryName(originalBlobPath)!, replacementMd5);
        string servedBlobPath = Path.Combine(stateDirectory, "replacement.blob");
        string patchedManifestPayloadPath = Path.Combine(stateDirectory, "octo-list-override.bin");
        if (!DecryptAsset(wrapped, config.AssetName).AsSpan().SequenceEqual(source))
            throw new InvalidDataException("Replacement failed the Octo encryption round-trip check.");

        byte[] patchedPayload = PatchItem(payload, config.AssetName, source.Length, config.ReplacementCrc, replacementMd5);
        byte[] patchedManifest = EncryptSecureFile(patchedPayload);
        string originalManifestSha256 = currentManifestSha256;
        string patchedManifestSha256 = Hex(SHA256.HashData(patchedManifest));

        string manifestBackup = Path.Combine(stateDirectory, "octocacheevai.original");
        string blobBackup = Path.Combine(stateDirectory, config.OriginalMd5 + ".original");
        string bucketMetaBackup = Path.Combine(stateDirectory, "bucket.meta.original");
        bool canReuseBackups = previous is not null &&
            originalManifestSha256.Equals(previous.OriginalManifestSha256, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(manifestBackup) && File.Exists(blobBackup) && File.Exists(bucketMetaBackup);
        if (canReuseBackups)
        {
            EnsureHash(manifestBackup, originalManifestSha256, "Stored manifest backup");
            EnsureHash(blobBackup, config.OriginalMd5, "Stored asset backup", MD5.Create());
            EnsureHash(bucketMetaBackup, Hex(SHA256.HashData(expectedBucketMeta)), "Stored bucket metadata backup");
        }
        else
        {
            File.Copy(manifestPath, manifestBackup, true);
            File.Copy(originalBlobPath, blobBackup, true);
            File.Copy(bucketMetaPath, bucketMetaBackup, true);
        }

        var state = new OverrideState
        {
            Applied = false,
            AssetName = config.AssetName,
            ObjectName = config.ObjectName,
            GameDirectory = config.GameDirectory,
            SourcePath = config.SourcePath,
            SourceSha256 = sourceSha256,
            ManifestPath = manifestPath,
            ManifestBackupPath = manifestBackup,
            OriginalManifestSha256 = originalManifestSha256,
            PatchedManifestSha256 = patchedManifestSha256,
            PatchedManifestPayloadSha256 = Hex(SHA256.HashData(patchedPayload)),
            OriginalBlobPath = originalBlobPath,
            OriginalBlobBackupPath = blobBackup,
            BucketMetaPath = bucketMetaPath,
            BucketMetaBackupPath = bucketMetaBackup,
            BucketMetaSha256 = Hex(SHA256.HashData(expectedBucketMeta)),
            ReplacementBlobPath = replacementBlobPath,
            ServedBlobPath = servedBlobPath,
            PatchedManifestPayloadPath = patchedManifestPayloadPath,
            ReplacementMd5 = replacementMd5,
            ReplacementSize = source.Length,
            ReplacementCrc = config.ReplacementCrc
        };
        WriteJsonAtomic(statePath, state);

        try
        {
            WriteBytesAtomic(servedBlobPath, wrapped);
            WriteBytesAtomic(patchedManifestPayloadPath, patchedPayload);
            WriteBytesAtomic(replacementBlobPath, wrapped);
            if (!replacementBlobPath.Equals(originalBlobPath, StringComparison.OrdinalIgnoreCase) && File.Exists(originalBlobPath))
                File.Delete(originalBlobPath);
            WriteBytesAtomic(manifestPath, patchedManifest);
            state.Applied = true;
            state.AppliedAtUtc = DateTimeOffset.UtcNow;
            WriteJsonAtomic(statePath, state);
        }
        catch
        {
            WriteBytesAtomic(bucketMetaPath, File.ReadAllBytes(bucketMetaBackup));
            WriteBytesAtomic(originalBlobPath, File.ReadAllBytes(blobBackup));
            WriteBytesAtomic(manifestPath, File.ReadAllBytes(manifestBackup));
            if (!replacementBlobPath.Equals(originalBlobPath, StringComparison.OrdinalIgnoreCase) && File.Exists(replacementBlobPath))
                File.Delete(replacementBlobPath);
            throw;
        }

        Console.WriteLine($"Applied override: {config.AssetName}");
        Console.WriteLine($"  Source       : {config.SourcePath}");
        Console.WriteLine($"  Cached blob  : {replacementBlobPath}");
        Console.WriteLine($"  MD5 / size   : {replacementMd5} / {source.Length:N0}");
        Console.WriteLine($"  Unity CRC    : {config.ReplacementCrc} (0x{config.ReplacementCrc:x8})");
        return 0;
    }

    private static int Restore(string stateDirectory)
    {
        EnsureGameClosed();
        string statePath = Path.Combine(stateDirectory, "state.json");
        if (!File.Exists(statePath))
        {
            Console.WriteLine("No asset override state exists; nothing to restore.");
            return 0;
        }

        OverrideState state = JsonSerializer.Deserialize<OverrideState>(File.ReadAllText(statePath), JsonOptions())
            ?? throw new InvalidDataException($"Invalid override state: {statePath}");
        EnsureHash(state.ManifestBackupPath, state.OriginalManifestSha256, "Manifest backup");
        EnsureHash(state.OriginalBlobBackupPath, Path.GetFileName(state.OriginalBlobPath), "Asset backup", MD5.Create());
        EnsureHash(state.BucketMetaBackupPath, state.BucketMetaSha256, "Bucket metadata backup");

        string activeManifestHash = Hex(SHA256.HashData(File.ReadAllBytes(state.ManifestPath)));
        bool manifestIsOriginal = activeManifestHash.Equals(state.OriginalManifestSha256, StringComparison.OrdinalIgnoreCase);
        bool manifestIsPatched = activeManifestHash.Equals(state.PatchedManifestSha256, StringComparison.OrdinalIgnoreCase);
        if (!manifestIsOriginal && !manifestIsPatched)
        {
            state.Applied = false;
            state.RestoredAtUtc = DateTimeOffset.UtcNow;
            WriteJsonAtomic(statePath, state);
            Console.WriteLine($"Disabled server-only override: {state.AssetName}");
            Console.WriteLine("  The game-managed manifest was left untouched.");
            return 0;
        }

        bool originalBlobValid = File.Exists(state.OriginalBlobPath);
        if (originalBlobValid)
        {
            try { EnsureHash(state.OriginalBlobPath, Path.GetFileName(state.OriginalBlobPath), "Original cache blob", MD5.Create()); }
            catch (InvalidDataException) { originalBlobValid = false; }
        }
        WriteBytesAtomic(state.BucketMetaPath, File.ReadAllBytes(state.BucketMetaBackupPath));
        if (!originalBlobValid)
            WriteBytesAtomic(state.OriginalBlobPath, File.ReadAllBytes(state.OriginalBlobBackupPath));

        if (!manifestIsOriginal)
            WriteBytesAtomic(state.ManifestPath, File.ReadAllBytes(state.ManifestBackupPath));
        if (!state.ReplacementBlobPath.Equals(state.OriginalBlobPath, StringComparison.OrdinalIgnoreCase) && File.Exists(state.ReplacementBlobPath))
            File.Delete(state.ReplacementBlobPath);

        state.Applied = false;
        state.RestoredAtUtc = DateTimeOffset.UtcNow;
        WriteJsonAtomic(statePath, state);
        Console.WriteLine(manifestIsOriginal
            ? $"Asset override is already restored: {state.AssetName}"
            : $"Restored original asset and manifest: {state.AssetName}");
        return 0;
    }

    private static int Status(OverrideConfig config, string stateDirectory)
    {
        string statePath = Path.Combine(stateDirectory, "state.json");
        if (!File.Exists(statePath))
        {
            Console.WriteLine($"Not applied: {config.AssetName}");
            return 0;
        }

        OverrideState state = JsonSerializer.Deserialize<OverrideState>(File.ReadAllText(statePath), JsonOptions())
            ?? throw new InvalidDataException($"Invalid override state: {statePath}");
        string stateName = state.Applied ? "APPLIED" : "RESTORED";
        Console.WriteLine($"{stateName}: {state.AssetName}");
        Console.WriteLine($"  Manifest: {state.ManifestPath}");
        Console.WriteLine($"  Blob    : {state.ReplacementBlobPath}");
        return 0;
    }

    private static OverrideConfig LoadConfig(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<OverrideConfig>(File.ReadAllText(fullPath), JsonOptions())
            ?? throw new InvalidDataException($"Invalid override configuration: {fullPath}");
        config.GameDirectory = Path.GetFullPath(config.GameDirectory);
        config.SourcePath = Path.GetFullPath(config.SourcePath);
        return config;
    }

    private static void ValidateConfig(OverrideConfig config)
    {
        if (!Directory.Exists(config.GameDirectory)) throw new DirectoryNotFoundException($"Game directory not found: {config.GameDirectory}");
        if (!File.Exists(config.SourcePath)) throw new FileNotFoundException("Replacement bundle not found.", config.SourcePath);
        if (string.IsNullOrWhiteSpace(config.AssetName)) throw new InvalidDataException("AssetName is required.");
        if (string.IsNullOrWhiteSpace(config.ObjectName)) throw new InvalidDataException("ObjectName is required.");
        if (config.OriginalMd5.Length != 32) throw new InvalidDataException("OriginalMd5 must contain 32 hexadecimal characters.");
        if (config.SourceSha256.Length != 64) throw new InvalidDataException("SourceSha256 must contain 64 hexadecimal characters.");
        if (config.OriginalBucketMetaHex.Length != 16) throw new InvalidDataException("OriginalBucketMetaHex must contain 16 hexadecimal characters.");
    }

    private static void RequireOriginalMetadata(ItemMetadata item, OverrideConfig config)
    {
        if (!item.Md5.Equals(config.OriginalMd5, StringComparison.OrdinalIgnoreCase) ||
            item.Size != config.OriginalSize || item.Crc != config.OriginalCrc)
            throw new InvalidDataException($"Manifest metadata differs from the registered original for {config.AssetName}. " +
                $"Found md5={item.Md5}, size={item.Size}, crc={item.Crc}.");
    }

    private static void RequireReplacementMetadata(ItemMetadata item, OverrideState state)
    {
        if (!item.Md5.Equals(state.ReplacementMd5, StringComparison.OrdinalIgnoreCase) ||
            item.Size != state.ReplacementSize || item.Crc != state.ReplacementCrc)
            throw new InvalidDataException($"Patched manifest metadata is inconsistent for {state.AssetName}.");
    }

    private static void VerifyAppliedFiles(OverrideState state, string sourceSha256, bool verifyActiveManifest)
    {
        if (!sourceSha256.Equals(state.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered source bundle changed after the override was applied. Restore before applying it again.");
        if (verifyActiveManifest)
        {
            EnsureHash(state.ManifestPath, state.PatchedManifestSha256, "Active Octo manifest");
            EnsureHash(state.ReplacementBlobPath, state.ReplacementMd5, "Replacement cache blob", MD5.Create());
            EnsureHash(state.BucketMetaPath, state.BucketMetaSha256, "Octo bucket metadata");
        }
        EnsureHash(state.ServedBlobPath, state.ReplacementMd5, "Served replacement blob", MD5.Create());
        EnsureHash(state.PatchedManifestPayloadPath, state.PatchedManifestPayloadSha256, "Served manifest override");
    }

    private static string FindManifest(string gameDirectory)
    {
        string root = Path.Combine(gameDirectory, "octo", "pdb");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Octo manifest directory not found: {root}");
        return Directory.EnumerateFiles(root, "octocacheevai", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => new FileInfo(p).Length)
            .FirstOrDefault() ?? throw new FileNotFoundException("No Octo manifest was found.", root);
    }

    private static string FindBlob(string gameDirectory, string md5)
    {
        string root = Path.Combine(gameDirectory, "octo", "v1");
        string[] matches = Directory.EnumerateFiles(root, md5, SearchOption.AllDirectories).Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"Cached original blob {md5} was not found under {root}."),
            _ => throw new InvalidDataException($"Multiple cached blobs named {md5} were found under {root}.")
        };
    }

    private static void EnsureGameClosed()
    {
        if (Process.GetProcessesByName("FF7EC").Length > 0)
            throw new InvalidOperationException("FF7EC is running. Close the game before applying or restoring asset overrides.");
    }

    private static void EnsureUnityBundle(byte[] bytes, string path)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 7).SequenceEqual("UnityFS"u8))
            throw new InvalidDataException($"Replacement is not a plaintext UnityFS bundle: {path}");
    }

    private static void VerifyFile(string path, string md5, int size)
    {
        var file = new FileInfo(path);
        if (file.Length != size) throw new InvalidDataException($"Original cache blob size mismatch: {file.Length} != {size}.");
        EnsureHash(path, md5, "Original cache blob", MD5.Create());
    }

    private static void EnsureHash(string path, string expected, string label, HashAlgorithm? algorithm = null)
    {
        using HashAlgorithm hash = algorithm ?? SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        string actual = Hex(hash.ComputeHash(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} hash mismatch. Expected {expected}, got {actual}: {path}");
    }

    private static byte[] EncryptAsset(ReadOnlySpan<byte> plain, string name, byte marker)
    {
        byte[] mask = BuildMask(name);
        byte[] output = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++) output[i] = (byte)(plain[i] ^ mask[i % mask.Length]);
        output[0] = marker;
        return output;
    }

    private static byte[] DecryptAsset(ReadOnlySpan<byte> stored, string name)
    {
        byte[] mask = BuildMask(name);
        byte[] output = new byte[stored.Length];
        for (int i = 0; i < stored.Length; i++) output[i] = (byte)(stored[i] ^ mask[i % mask.Length]);
        output[0] = (byte)'U';
        return output;
    }

    private static byte[] BuildMask(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        byte[] mask = new byte[bytes.Length * 2];
        int front = 0, back = mask.Length - 1;
        foreach (byte value in bytes)
        {
            mask[front] = value;
            front += 2;
            mask[back] = value;
            back -= 2;
        }
        byte roll = AssetMaskSeed;
        foreach (byte value in mask) roll = (byte)((((roll & 1) << 7) | (roll >> 1)) ^ value);
        for (int i = 0; i < mask.Length; i++) mask[i] ^= roll;
        return mask;
    }

    private static byte[] DecryptSecureFile(ReadOnlySpan<byte> file)
    {
        if (file.Length < 17 || file[0] != SecureFileAesWithMd5)
            throw new InvalidDataException("Unsupported or truncated Octo manifest SecureFile.");
        return OctoCrypto.DecryptSecureFile(file);
    }

    private static ItemMetadata ReadItem(ReadOnlySpan<byte> database, string assetName)
    {
        int offset = 0;
        while (offset < database.Length)
        {
            ulong key = ReadVarint(database, ref offset);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 2 && wire == 2)
            {
                ReadOnlySpan<byte> item = ReadLengthDelimited(database, ref offset);
                ItemMetadata metadata = ReadItemMetadata(item);
                if (metadata.Name.Equals(assetName, StringComparison.Ordinal)) return metadata;
            }
            else SkipField(database, ref offset, wire);
        }
        throw new InvalidDataException($"Asset '{assetName}' was not found in the Octo manifest.");
    }

    private static ItemMetadata ReadItemMetadata(ReadOnlySpan<byte> item)
    {
        string name = "", md5 = "";
        int size = 0;
        uint crc = 0;
        int offset = 0;
        while (offset < item.Length)
        {
            ulong key = ReadVarint(item, ref offset);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (field)
            {
                case 3 when wire == 2: name = Encoding.UTF8.GetString(ReadLengthDelimited(item, ref offset)); break;
                case 4 when wire == 0: size = checked((int)ReadVarint(item, ref offset)); break;
                case 5 when wire == 0: crc = checked((uint)ReadVarint(item, ref offset)); break;
                case 10 when wire == 2: md5 = Encoding.ASCII.GetString(ReadLengthDelimited(item, ref offset)); break;
                default: SkipField(item, ref offset, wire); break;
            }
        }
        return new ItemMetadata(name, size, crc, md5);
    }

    private static byte[] PatchItem(ReadOnlySpan<byte> database, string assetName, int size, uint crc, string md5)
    {
        using var output = new MemoryStream(database.Length + 128);
        int offset = 0, patched = 0;
        while (offset < database.Length)
        {
            int fieldStart = offset;
            ulong key = ReadVarint(database, ref offset);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 2 && wire == 2)
            {
                ReadOnlySpan<byte> item = ReadLengthDelimited(database, ref offset);
                ItemMetadata metadata = ReadItemMetadata(item);
                if (metadata.Name.Equals(assetName, StringComparison.Ordinal))
                {
                    byte[] patchedItem = PatchItemFields(item, size, crc, md5);
                    WriteVarint(output, key);
                    WriteVarint(output, checked((ulong)patchedItem.Length));
                    output.Write(patchedItem);
                    patched++;
                    continue;
                }
            }
            else SkipField(database, ref offset, wire);
            output.Write(database[fieldStart..offset]);
        }
        if (patched != 1) throw new InvalidDataException($"Expected one manifest entry for '{assetName}', patched {patched}.");
        return output.ToArray();
    }

    private static byte[] PatchItemFields(ReadOnlySpan<byte> item, int size, uint crc, string md5)
    {
        using var output = new MemoryStream(item.Length + 32);
        int offset = 0;
        var replaced = new HashSet<int>();
        while (offset < item.Length)
        {
            int fieldStart = offset;
            ulong key = ReadVarint(item, ref offset);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 4 && wire == 0)
            {
                ReadVarint(item, ref offset);
                WriteVarint(output, key); WriteVarint(output, checked((ulong)size)); replaced.Add(field);
            }
            else if (field == 5 && wire == 0)
            {
                ReadVarint(item, ref offset);
                WriteVarint(output, key); WriteVarint(output, crc); replaced.Add(field);
            }
            else if (field == 10 && wire == 2)
            {
                ReadLengthDelimited(item, ref offset);
                byte[] value = Encoding.ASCII.GetBytes(md5);
                WriteVarint(output, key); WriteVarint(output, checked((ulong)value.Length)); output.Write(value); replaced.Add(field);
            }
            else
            {
                SkipField(item, ref offset, wire);
                output.Write(item[fieldStart..offset]);
            }
        }
        if (!replaced.SetEquals([4, 5, 10])) throw new InvalidDataException("Target manifest item is missing size, CRC, or MD5 metadata.");
        return output.ToArray();
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated protobuf varint.");
            byte current = bytes[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int length = checked((int)ReadVarint(bytes, ref offset));
        if (length < 0 || offset + length > bytes.Length) throw new InvalidDataException("Truncated protobuf field.");
        ReadOnlySpan<byte> value = bytes.Slice(offset, length);
        offset += length;
        return value;
    }

    private static void SkipField(ReadOnlySpan<byte> bytes, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(bytes, ref offset); break;
            case 1: offset = checked(offset + 8); break;
            case 2: ReadLengthDelimited(bytes, ref offset); break;
            case 5: offset = checked(offset + 4); break;
            default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
        }
        if (offset > bytes.Length) throw new InvalidDataException("Truncated protobuf field.");
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".ff7ec-override.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteJsonAtomic(string path, OverrideState state) =>
        WriteBytesAtomic(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions())));

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string? GetArg(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Usage() => Console.WriteLine("Usage: Ff7ec.AssetOverride <apply|restore|status> --config <asset-overrides.json>");

    private sealed class OverrideConfig
    {
        public string GameDirectory { get; set; } = "";
        public string StateDirectory { get; set; } = "..\\data\\asset-overrides\\tifa-019";
        public string AssetName { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string OriginalMd5 { get; set; } = "";
        public int OriginalSize { get; set; }
        public uint OriginalCrc { get; set; }
        public string OriginalBucketMetaHex { get; set; } = "";
        public uint ReplacementCrc { get; set; }
    }

    private sealed class OverrideState
    {
        public bool Applied { get; set; }
        public string AssetName { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string GameDirectory { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public string ManifestBackupPath { get; set; } = "";
        public string OriginalManifestSha256 { get; set; } = "";
        public string PatchedManifestSha256 { get; set; } = "";
        public string PatchedManifestPayloadSha256 { get; set; } = "";
        public string OriginalBlobPath { get; set; } = "";
        public string OriginalBlobBackupPath { get; set; } = "";
        public string BucketMetaPath { get; set; } = "";
        public string BucketMetaBackupPath { get; set; } = "";
        public string BucketMetaSha256 { get; set; } = "";
        public string ReplacementBlobPath { get; set; } = "";
        public string ServedBlobPath { get; set; } = "";
        public string PatchedManifestPayloadPath { get; set; } = "";
        public string ReplacementMd5 { get; set; } = "";
        public int ReplacementSize { get; set; }
        public uint ReplacementCrc { get; set; }
        public DateTimeOffset? AppliedAtUtc { get; set; }
        public DateTimeOffset? RestoredAtUtc { get; set; }
    }

    private readonly record struct ItemMetadata(string Name, int Size, uint Crc, string Md5);
}
