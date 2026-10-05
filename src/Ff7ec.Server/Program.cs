using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Http.Extensions;
using Ff7ec.Server;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration.GetSection("Ff7ec");
int listenPort = config.GetValue("ListenPort", 443);
string capturesDir = Path.GetFullPath(config["CapturesDirectory"] ?? throw new InvalidOperationException("Ff7ec:CapturesDirectory not configured"), builder.Environment.ContentRootPath);
string certDir = Path.GetFullPath(config["CertDirectory"] ?? throw new InvalidOperationException("Ff7ec:CertDirectory not configured"), builder.Environment.ContentRootPath);
string gapsDir = Path.GetFullPath(config["GapsDirectory"] ?? throw new InvalidOperationException("Ff7ec:GapsDirectory not configured"), builder.Environment.ContentRootPath);
string dataDir = Path.GetFullPath(config["DataDirectory"] ?? throw new InvalidOperationException("Ff7ec:DataDirectory not configured"), builder.Environment.ContentRootPath);
var standaloneConfig = config.GetSection("Standalone");
bool standalone = standaloneConfig.GetValue("Enabled", false);
if (standalone) dataDir = Path.Combine(dataDir, "standalone");
var assetOverrideConfig = config.GetSection("AssetOverride");
string assetOverrideStateDirectory = Path.GetFullPath(
    assetOverrideConfig["StateDirectory"] ?? Path.Combine(dataDir, "asset-overrides"), builder.Environment.ContentRootPath);
string[] assetOverrideStateFiles = assetOverrideConfig.GetSection("StateFiles").Get<string[]>()
    ?? (assetOverrideConfig["StateFile"] is string stateFile ? [stateFile] : []);
assetOverrideStateFiles = assetOverrideStateFiles.Select(path => Path.GetFullPath(path, builder.Environment.ContentRootPath)).ToArray();
string assetManifestHost = assetOverrideConfig["ManifestHost"] ?? throw new InvalidOperationException("Ff7ec:AssetOverride:ManifestHost not configured");
string assetManifestPath = assetOverrideConfig["ManifestPath"] ?? throw new InvalidOperationException("Ff7ec:AssetOverride:ManifestPath not configured");
string assetDataHost = assetOverrideConfig["AssetHost"] ?? throw new InvalidOperationException("Ff7ec:AssetOverride:AssetHost not configured");
string[] hostNames = config.GetSection("Hostnames").Get<string[]>()
    ?? throw new InvalidOperationException("Ff7ec:Hostnames not configured");
var accountExportConfig = config.GetSection("AccountExport");
string? accountJsonPath = accountExportConfig["JsonPath"];
string? protocolAssemblyPath = accountExportConfig["ProtocolAssemblyPath"];
if (standalone && string.IsNullOrWhiteSpace(accountJsonPath))
    throw new InvalidOperationException("Standalone mode requires AccountExport:JsonPath.");
if (string.IsNullOrWhiteSpace(accountJsonPath) && !string.IsNullOrWhiteSpace(protocolAssemblyPath))
    throw new InvalidOperationException("AccountExport:ProtocolAssemblyPath requires JsonPath.");
if (!string.IsNullOrWhiteSpace(accountJsonPath))
{
    string jsonPath = Path.GetFullPath(accountJsonPath, builder.Environment.ContentRootPath);
    string protocolPath = !string.IsNullOrWhiteSpace(protocolAssemblyPath)
        ? protocolAssemblyPath
        : accountExportConfig["ProtocolSchemaPath"] ?? AccountExportStore.DefaultProtocolSchemaPath;
    if (string.IsNullOrWhiteSpace(protocolPath))
        throw new InvalidOperationException("AccountExport:ProtocolSchemaPath must identify a protocol-schema file.");
    protocolPath = Path.GetFullPath(protocolPath, builder.Environment.ContentRootPath);
    builder.Services.AddSingleton(sp => new AccountExportStore(
        sp.GetRequiredService<ILogger<AccountExportStore>>(),
        jsonPath,
        protocolPath,
        accountExportConfig["ApiHost"] ?? hostNames[0],
        accountExportConfig.GetValue<long?>("FrozenServerTime")));
}
if (standalone)
{
    string gameDirectory = standaloneConfig["GameDirectory"]
        ?? throw new InvalidOperationException("Standalone:GameDirectory is required.");
    if (string.IsNullOrWhiteSpace(gameDirectory))
        throw new InvalidOperationException("Standalone:GameDirectory is required.");
    gameDirectory = Path.GetFullPath(gameDirectory, builder.Environment.ContentRootPath);
    string masterHost = hostNames.Single(host => host.StartsWith("client-masterdata-", StringComparison.Ordinal));
    string webviewHost = hostNames.Single(host => host.StartsWith("webview-", StringComparison.Ordinal));
    builder.Services.AddSingleton(sp => new LocalGameContentStore(
        sp.GetRequiredService<ILogger<LocalGameContentStore>>(), gameDirectory, masterHost, assetManifestHost, assetDataHost,
        sp.GetRequiredService<AccountExportStore>().RequiredMasterIds));
    builder.Services.AddSingleton<StandaloneResponseHeaders>();
    builder.Services.AddSingleton(sp => new StandaloneApi(sp.GetRequiredService<AccountExportStore>(),
        sp.GetRequiredService<LocalGameContentStore>(), accountExportConfig["ApiHost"] ?? hostNames[0], webviewHost));
}

var (leafCert, rootCaCert) = CertManager.EnsureCertificates(certDir, hostNames);
Console.WriteLine($"[FF7EC] Using leaf cert '{leafCert.Subject}' (thumbprint {leafCert.Thumbprint})");
Console.WriteLine($"[FF7EC] Root CA thumbprint {rootCaCert.Thumbprint} - install {Path.Combine(certDir, "ff7ec-offline-ca.cer")} into Trusted Root if not already done.");

builder.WebHost.ConfigureKestrel(options =>
{
    void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions)
    {
        listenOptions.UseHttps(httpsOptions =>
        {
            httpsOptions.ServerCertificateSelector = (_, _) => leafCert;
        });
    }
    if (standalone) options.Listen(IPAddress.Loopback, listenPort, ConfigureHttps);
    else options.ListenAnyIP(listenPort, ConfigureHttps);
});

builder.Services.AddSingleton(sp => new ReplayStore(sp.GetRequiredService<ILogger<ReplayStore>>(), capturesDir, !standalone));
builder.Services.AddSingleton(sp => new GapLogger(sp.GetRequiredService<ILogger<GapLogger>>(), gapsDir));
builder.Services.AddSingleton(sp => new PartySettingsStore(sp.GetRequiredService<ILogger<PartySettingsStore>>(), dataDir, standalone));
builder.Services.AddSingleton(sp => new StoryStateStore(sp.GetRequiredService<ILogger<StoryStateStore>>(), dataDir, standalone));
builder.Services.AddSingleton(sp => new LocalAssetOverrideStore(
    sp.GetRequiredService<ILogger<LocalAssetOverrideStore>>(),
    assetOverrideStateDirectory,
    assetManifestHost,
    assetManifestPath,
    assetDataHost,
    assetOverrideStateFiles));
builder.Services.AddSingleton(sp => new PartyStateMerger(
    sp.GetRequiredService<PartySettingsStore>(),
    sp.GetRequiredService<StoryStateStore>(),
    sp.GetRequiredService<ILogger<PartyStateMerger>>(), standalone));

var app = builder.Build();

// Force-load the store at startup (rather than on first request) so load errors and the
// captured-response count show up immediately in the console.
var store = app.Services.GetRequiredService<ReplayStore>();
var partySettingsStore = app.Services.GetRequiredService<PartySettingsStore>();
var storyStateStore = app.Services.GetRequiredService<StoryStateStore>();
var assetOverrideStore = app.Services.GetRequiredService<LocalAssetOverrideStore>();
var partyStateMerger = app.Services.GetRequiredService<PartyStateMerger>();
var accountExportStore = app.Services.GetService<AccountExportStore>();
var localContent = app.Services.GetService<LocalGameContentStore>();
var standaloneHeaders = app.Services.GetService<StandaloneResponseHeaders>();
var standaloneApi = app.Services.GetService<StandaloneApi>();
if (standalone) app.Logger.LogInformation("STANDALONE single-player mode: loopback only, no replay or asset overrides; settings use {Directory}", dataDir);
app.Logger.LogInformation("FF7EC offline server ready - {Count} captured responses loaded, listening on :{Port} for {Hosts}",
    store.Count, listenPort, string.Join(", ", hostNames));

// Response headers that either Kestrel manages itself (framing) or are CloudFront/AWS
// infrastructure noise that is misleading once served from a local offline box. Every
// other captured header - including all the app's custom X-* ones - is replayed verbatim.
var suppressedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Connection", "Transfer-Encoding", "Content-Length",
    "Via", "X-Cache", "X-Amz-Cf-Pop", "X-Amz-Cf-Id", "Date",
};

var writableSettingsEndpoints = new HashSet<string>(StringComparer.Ordinal)
{
    "/api/pvt/party/multi/set/upsert",
    "/api/pvt/party/solo/set/upsert",
    "/api/pvt/user/home/background/setting",
};
var emptyWriteEndpoints = new HashSet<string>(StringComparer.Ordinal)
{
    "/api/pvt/dungeon/story/end",
    "/api/pvt/dungeon/story/start",
    "/api/pvt/event/solo/battle/end",
    "/api/pvt/event/solo/battle/start",
    "/api/pvt/character/story/battle/end",
    "/api/pvt/character/story/battle/start",
    "/api/pvt/character/story/result",
    "/api/pvt/story/battle/end",
    "/api/pvt/story/battle/start",
    "/api/pvt/story/result",
    "/api/pvt/story/select/drama",
};
var storyStateEndpoints = new HashSet<string>(StringComparer.Ordinal)
{
    "/api/pvt/story/select/drama",
    "/api/pvt/story/result",
};
var standaloneWriteEndpoints = new HashSet<string>(StringComparer.Ordinal)
{
    "/api/pvt/party/solo/set/upsert",
    "/api/pvt/user/home/background/setting",
    "/api/pvt/story/select/drama",
    "/api/pvt/story/result",
    "/api/pvt/character/story/result",
    "/api/pvt/dungeon/story/start",
    "/api/pvt/dungeon/story/end",
};

app.Run(async context =>
{
    var request = context.Request;
    var host = request.Headers.Host.ToString();
    var pathAndQuery = request.GetEncodedPathAndQuery();

    using var bodyStream = new MemoryStream();
    await request.Body.CopyToAsync(bodyStream);
    var bodyBytes = bodyStream.ToArray();

    if (!standalone && HttpMethods.IsGet(request.Method) && assetOverrideStore.TryGetAsset(host, pathAndQuery, out var overrideAsset))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = overrideAsset.Length;
        app.Logger.LogInformation("ASSET OVERRIDE {Host}{Path} -> 200 ({Bytes} bytes)", host, pathAndQuery, overrideAsset.Length);
        await context.Response.Body.WriteAsync(overrideAsset);
        return;
    }

    var requestPath = request.Path.Value ?? string.Empty;
    bool isExportAccountRequest = accountExportStore?.IsAccountRequest(request) == true;
    if (isExportAccountRequest && !accountExportStore!.MatchesUser(request))
    {
        app.Logger.LogWarning("ACCOUNT EXPORT rejected a request for another or missing user: {Path}", requestPath);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("This server is configured for a different exported account.\n");
        return;
    }
    if (standalone && !isExportAccountRequest)
    {
        if (HttpMethods.IsGet(request.Method))
        {
            try
            {
                if (localContent!.TryGet(request.Host.Host, requestPath, out var contentBody, out var contentType))
                {
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength = contentBody.Length;
                    app.Logger.LogInformation("LOCAL CONTENT {Host}{Path} -> 200 ({Bytes} bytes)", host, requestPath, contentBody.Length);
                    await context.Response.Body.WriteAsync(contentBody);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException)
            {
                app.Logger.LogError(ex, "LOCAL CONTENT unavailable at {Host}{Path}", host, requestPath);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Required installed game content is missing or invalid.\n");
                return;
            }
        }
    }
    if (standaloneApi?.Handles(request) == true)
    {
        if (!standaloneApi.HasCorrectMethod(request))
        {
            app.Logger.LogWarning("STANDALONE rejected method {Method} at {Path}", request.Method, requestPath);
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = requestPath == "/api/check" ? HttpMethods.Get : HttpMethods.Post;
            return;
        }
        byte[] generated;
        try
        {
            standaloneHeaders!.Apply(request, context.Response.Headers);
            generated = standaloneApi.CreateResponse(request, bodyBytes, context.Response.Headers);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or InvalidOperationException or NotSupportedException)
        {
            app.Logger.LogWarning(ex, "STANDALONE rejected malformed request at {Path}", requestPath);
            context.Response.Headers.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid standalone API request.\n");
            return;
        }
        context.Response.ContentLength = generated.Length;
        app.Logger.LogInformation("STANDALONE {Path} -> 200 ({Bytes} generated bytes)", requestPath, generated.Length);
        await context.Response.Body.WriteAsync(generated);
        return;
    }

    if (isExportAccountRequest && accountExportStore!.Handles(requestPath) && !HttpMethods.IsPost(request.Method))
    {
        app.Logger.LogWarning("ACCOUNT EXPORT rejected method {Method} at {Path}", request.Method, requestPath);
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = HttpMethods.Post;
        return;
    }

    if (isExportAccountRequest && accountExportStore!.Handles(requestPath))
    {
        CapturedResponse? template = null;
        if (!standalone && !accountExportStore.TryGetHeaderTemplate(request, store, out template))
        {
            app.Logger.LogError("ACCOUNT EXPORT has no secure header template for {Host}{Path}", host, requestPath);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("No secure response-header template is available for this account.\n");
            return;
        }
        byte[] generated;
        try
        {
            if (standalone) standaloneHeaders!.Apply(request, context.Response.Headers);
            else
                foreach (var header in template!.ResponseHeaders)
                    if (!suppressedHeaders.Contains(header.Name))
                        context.Response.Headers[header.Name] = header.Value;
            generated = accountExportStore.CreateResponse(request, bodyBytes, context.Response.Headers);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or InvalidOperationException or NotSupportedException)
        {
            app.Logger.LogWarning(ex, "ACCOUNT EXPORT rejected malformed request at {Path}", requestPath);
            context.Response.Headers.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid exported-account API request.\n");
            return;
        }
        try
        {
            generated = partyStateMerger.MergeReplayResponse(request, generated, context.Response.Headers) ?? generated;
        }
        catch (InvalidDataException ex)
        {
            app.Logger.LogError(ex, "STANDALONE could not apply saved settings at {Path}", requestPath);
            context.Response.Headers.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Could not apply saved standalone settings; existing data was not discarded.\n");
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = generated.Length;
        app.Logger.LogInformation("ACCOUNT EXPORT {Path} -> 200 ({Bytes} generated bytes)", requestPath, generated.Length);
        await context.Response.Body.WriteAsync(generated);
        return;
    }

    var isSettingsWrite = writableSettingsEndpoints.Contains(requestPath);
    var isEmptyWrite = emptyWriteEndpoints.Contains(requestPath);
    if (standalone && (isSettingsWrite || isEmptyWrite) &&
        (!isExportAccountRequest || !standaloneWriteEndpoints.Contains(requestPath)))
    {
        await app.Services.GetRequiredService<GapLogger>().LogAsync(request, bodyBytes);
        app.Logger.LogWarning("STANDALONE does not support this progression or multiplayer write: {Path}", requestPath);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        await context.Response.WriteAsync("This operation is not supported by standalone single-player mode.\n");
        return;
    }
    if (standalone && (isSettingsWrite || isEmptyWrite) && !HttpMethods.IsPost(request.Method))
    {
        app.Logger.LogWarning("STANDALONE rejected write method {Method} at {Path}", request.Method, requestPath);
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = HttpMethods.Post;
        return;
    }
    if (HttpMethods.IsPost(request.Method) && (isSettingsWrite || isEmptyWrite))
    {
        var userId = request.Query["user_id"].ToString();
        var contentHash = request.Headers["x-content-hash"].ToString();
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(contentHash) ||
            bodyBytes.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "Writable requests require nonempty user_id, x-content-hash, and request body.\n");
            return;
        }

        if (isSettingsWrite && !standalone)
        {
            await partySettingsStore.AppendAsync(
                host,
                requestPath,
                pathAndQuery,
                userId,
                contentHash,
                request.ContentType,
                bodyBytes,
                context.RequestAborted);
        }

        if (storyStateEndpoints.Contains(requestPath) && !standalone)
        {
            await storyStateStore.AppendAsync(
                host,
                requestPath,
                pathAndQuery,
                userId,
                contentHash,
                bodyBytes,
                context.RequestAborted);
        }

        // Reuse the captured response headers and CommonResponse as a secure success
        // envelope, replacing its endpoint-specific response and optional user update.
        var responseTemplatePath =
            $"/api/pvt/store/purchase/restart/steam?user_id={Uri.EscapeDataString(userId)}";
        CapturedResponse? responseTemplate = null;
        bool hasTemplate = standalone || (isExportAccountRequest
            ? accountExportStore!.TryGetHeaderTemplate(request, store, out responseTemplate)
            : store.TryGet(host, HttpMethods.Post, responseTemplatePath, out responseTemplate));
        if (!hasTemplate)
        {
            app.Logger.LogError(
                "WRITE response template is missing: {Method} {Host}{Path}",
                HttpMethods.Post, host, responseTemplatePath);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(
                "No encrypted success-response template is available.\n");
            return;
        }

        context.Response.StatusCode = standalone ? StatusCodes.Status200OK : responseTemplate!.StatusCode;
        if (standalone)
        {
            try { standaloneHeaders!.Apply(request, context.Response.Headers); }
            catch (InvalidDataException ex)
            {
                app.Logger.LogWarning(ex, "STANDALONE rejected write language at {Path}", requestPath);
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Unsupported standalone language.\n");
                return;
            }
        }
        else
            foreach (var header in responseTemplate!.ResponseHeaders)
            {
                if (suppressedHeaders.Contains(header.Name)) continue;
                context.Response.Headers[header.Name] = header.Value;
            }

        var templateBody = isExportAccountRequest
            ? accountExportStore!.CreateWriteTemplate(context.Response.Headers)
            : responseTemplate!.Body;
        var responseBody = isSettingsWrite
            ? partyStateMerger.CreateWriteResponse(
                requestPath, userId, bodyBytes, templateBody, context.Response.Headers)
            : partyStateMerger.CreateEmptyWriteResponse(
                requestPath, userId, bodyBytes, templateBody, context.Response.Headers);
        if (responseBody is null && isSettingsWrite && !isExportAccountRequest)
        {
            responseBody = responseTemplate!.Body;
            app.Logger.LogWarning(
                "SETTINGS WRITE ACK could not include a client cache update; using {Template}",
                Path.GetFileName(responseTemplate.SourceFile));
        }
        else if (responseBody is null)
        {
            context.Response.Headers.Clear();
            context.Response.StatusCode = standalone ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Could not generate a secure write response.\n");
            return;
        }
        if (standalone)
        {
            if (isSettingsWrite)
                await partySettingsStore.AppendAsync(host, requestPath, pathAndQuery, userId, contentHash,
                    request.ContentType, bodyBytes, context.RequestAborted);
            if (storyStateEndpoints.Contains(requestPath))
                await storyStateStore.AppendAsync(host, requestPath, pathAndQuery, userId, contentHash,
                    bodyBytes, context.RequestAborted);
        }

        context.Response.ContentLength = responseBody.Length;
        app.Logger.LogInformation(
            "WRITE ACK {Host}{Path} -> {Status} ({Bytes} bytes){Update}",
            host,
            request.Path,
            context.Response.StatusCode,
            responseBody.Length,
            isSettingsWrite ? " with client cache update" : string.Empty);
        await context.Response.Body.WriteAsync(responseBody);
        return;
    }
    if (standalone)
    {
        await app.Services.GetRequiredService<GapLogger>().LogAsync(request, bodyBytes);
        app.Logger.LogWarning("STANDALONE unsupported endpoint {Method} {Host}{Path}; replay is disabled",
            request.Method, host, requestPath);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        await context.Response.WriteAsync("Standalone mode has no local handler for this request; replay is disabled.\n");
        return;
    }

    if (store.TryGet(host, request.Method, pathAndQuery, out var captured))
    {
        context.Response.StatusCode = captured.StatusCode;
        foreach (var header in captured.ResponseHeaders)
        {
            if (suppressedHeaders.Contains(header.Name)) continue;
            context.Response.Headers[header.Name] = header.Value;
        }
        // Replay the captured response first, then rewrite its encrypted protobuf
        // envelope with the latest state received through the write endpoint.
        var responseBody = captured.Body;
        if (isExportAccountRequest && captured.StatusCode == StatusCodes.Status200OK)
        {
            try
            {
                responseBody = accountExportStore!.RemoveCapturedAccountState(responseBody, context.Response.Headers) ?? responseBody;
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException or NotSupportedException)
            {
                app.Logger.LogError(ex, "ACCOUNT EXPORT could not remove captured account state at {Path}", requestPath);
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("Could not isolate exported account state from the replay response.\n");
                return;
            }
            context.Response.ContentLength = responseBody.Length;
        }
        if (HttpMethods.IsGet(request.Method) && assetOverrideStore.TryGetManifest(host, pathAndQuery, out var overrideManifest))
        {
            responseBody = overrideManifest;
            context.Response.ContentLength = responseBody.Length;
            app.Logger.LogInformation("ASSET OVERRIDE manifest applied to {Host}{Path}", host, pathAndQuery);
        }

        var mergedBody = partyStateMerger.MergeReplayResponse(
            request, responseBody, context.Response.Headers);
        if (mergedBody is not null)
        {
            responseBody = mergedBody;
            context.Response.ContentLength = responseBody.Length;
            app.Logger.LogInformation(
                "REPLAY request state overlay applied to {Method} {Host}{Path} ({Bytes} bytes)",
                request.Method, host, pathAndQuery, responseBody.Length);
        }

        app.Logger.LogInformation("REPLAY {Method} {Host}{Path} -> {Status} ({Bytes} bytes) [{Source}]",
            request.Method, host, pathAndQuery, captured.StatusCode, responseBody.Length, Path.GetFileName(captured.SourceFile));
        await context.Response.Body.WriteAsync(responseBody);
        return;
    }

    var gapLogger = app.Services.GetRequiredService<GapLogger>();
    await gapLogger.LogAsync(request, bodyBytes);
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync("FF7EC offline server: no captured response for this request.\n");
});

app.Run();
