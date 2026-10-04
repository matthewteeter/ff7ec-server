using Ff7ec.Server;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Ff7ec.ProtocolSchema <Command.Domain.dll> <output.json> <client-build-id>");
    return 2;
}

try
{
    AccountExportStore.WriteProtocolSchema(args[0], args[1], args[2]);
    Console.WriteLine($"Wrote account protocol mappings ({new FileInfo(args[1]).Length:N0} bytes).");
    return 0;
}
catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
{
    Console.Error.WriteLine("Could not generate protocol schema: " + ex.Message);
    return 1;
}
