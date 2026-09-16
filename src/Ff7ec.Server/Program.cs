using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Extensions;
using Ff7ec.Server;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration.GetSection("Ff7ec");
int listenPort = config.GetValue("ListenPort", 443);
string capturesDir = config["CapturesDirectory"] ?? throw new InvalidOperationException("Ff7ec:CapturesDirectory not configured");
string certDir = config["CertDirectory"] ?? throw new InvalidOperationException("Ff7ec:CertDirectory not configured");
string gapsDir = config["GapsDirectory"] ?? throw new InvalidOperationException("Ff7ec:GapsDirectory not configured");
string dataDir = config["DataDirectory"] ?? throw new InvalidOperationException("Ff7ec:DataDirectory not configured");
string[] hostNames = config.GetSection("Hostnames").Get<string[]>()
    ?? throw new InvalidOperationException("Ff7ec:Hostnames not configured");

var (leafCert, rootCaCert) = CertManager.EnsureCertificates(certDir, hostNames);
Console.WriteLine($"[FF7EC] Using leaf cert '{leafCert.Subject}' (thumbprint {leafCert.Thumbprint})");
Console.WriteLine($"[FF7EC] Root CA thumbprint {rootCaCert.Thumbprint} - install {Path.Combine(certDir, "ff7ec-offline-ca.cer")} into Trusted Root if not already done.");

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(listenPort, listenOptions =>
    {
        listenOptions.UseHttps(httpsOptions =>
        {
            httpsOptions.ServerCertificateSelector = (_, _) => leafCert;
        });
    });
});

builder.Services.AddSingleton(sp => new ReplayStore(sp.GetRequiredService<ILogger<ReplayStore>>(), capturesDir));
builder.Services.AddSingleton(sp => new GapLogger(sp.GetRequiredService<ILogger<GapLogger>>(), gapsDir));
builder.Services.AddSingleton(sp => new PartySettingsStore(sp.GetRequiredService<ILogger<PartySettingsStore>>(), dataDir));
builder.Services.AddSingleton(sp => new StoryStateStore(sp.GetRequiredService<ILogger<StoryStateStore>>(), dataDir));
builder.Services.AddSingleton(sp => new PartyStateMerger(
    sp.GetRequiredService<PartySettingsStore>(),
    sp.GetRequiredService<StoryStateStore>(),
    sp.GetRequiredService<ILogger<PartyStateMerger>>()));

var app = builder.Build();

// Force-load the store at startup (rather than on first request) so load errors and the
// captured-response count show up immediately in the console.
var store = app.Services.GetRequiredService<ReplayStore>();
var partySettingsStore = app.Services.GetRequiredService<PartySettingsStore>();
var storyStateStore = app.Services.GetRequiredService<StoryStateStore>();
var partyStateMerger = app.Services.GetRequiredService<PartyStateMerger>();
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

app.Run(async context =>
{
    var request = context.Request;
    var host = request.Headers.Host.ToString();
    var pathAndQuery = request.GetEncodedPathAndQuery();

    using var bodyStream = new MemoryStream();
    await request.Body.CopyToAsync(bodyStream);
    var bodyBytes = bodyStream.ToArray();

    var requestPath = request.Path.Value ?? string.Empty;
    var isSettingsWrite = writableSettingsEndpoints.Contains(requestPath);
    var isEmptyWrite = emptyWriteEndpoints.Contains(requestPath);
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

        if (isSettingsWrite)
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

        if (storyStateEndpoints.Contains(requestPath))
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
        if (!store.TryGet(host, HttpMethods.Post, responseTemplatePath, out var responseTemplate))
        {
            app.Logger.LogError(
                "WRITE response template is missing: {Method} {Host}{Path}",
                HttpMethods.Post, host, responseTemplatePath);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(
                "No encrypted success-response template is available.\n");
            return;
        }

        context.Response.StatusCode = responseTemplate.StatusCode;
        foreach (var header in responseTemplate.ResponseHeaders)
        {
            if (suppressedHeaders.Contains(header.Name)) continue;
            context.Response.Headers[header.Name] = header.Value;
        }

        var responseBody = isSettingsWrite
            ? partyStateMerger.CreateWriteResponse(
                requestPath, userId, bodyBytes, responseTemplate.Body, context.Response.Headers)
            : partyStateMerger.CreateEmptyWriteResponse(
                requestPath, userId, bodyBytes, responseTemplate.Body, context.Response.Headers);
        if (responseBody is null && isSettingsWrite)
        {
            responseBody = responseTemplate.Body;
            app.Logger.LogWarning(
                "SETTINGS WRITE ACK could not include a client cache update; using {Template}",
                Path.GetFileName(responseTemplate.SourceFile));
        }
        else if (responseBody is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Could not generate a secure write response.\n");
            return;
        }

        context.Response.ContentLength = responseBody.Length;
        app.Logger.LogInformation(
            "WRITE ACK {Host}{Path} -> {Status} ({Bytes} bytes){Update}",
            host,
            request.Path,
            responseTemplate.StatusCode,
            responseBody.Length,
            isSettingsWrite ? " with client cache update" : string.Empty);
        await context.Response.Body.WriteAsync(responseBody);
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
        var mergedBody = partyStateMerger.MergeReplayResponse(
            request, captured.Body, context.Response.Headers);
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
