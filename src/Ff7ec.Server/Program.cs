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
builder.Services.AddSingleton(sp => new PartyStateMerger(
    sp.GetRequiredService<PartySettingsStore>(),
    sp.GetRequiredService<ILogger<PartyStateMerger>>()));

var app = builder.Build();

// Force-load the store at startup (rather than on first request) so load errors and the
// captured-response count show up immediately in the console.
var store = app.Services.GetRequiredService<ReplayStore>();
var partySettingsStore = app.Services.GetRequiredService<PartySettingsStore>();
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

app.Run(async context =>
{
    var request = context.Request;
    var host = request.Headers.Host.ToString();
    var pathAndQuery = request.GetEncodedPathAndQuery();

    using var bodyStream = new MemoryStream();
    await request.Body.CopyToAsync(bodyStream);
    var bodyBytes = bodyStream.ToArray();

    if (HttpMethods.IsPost(request.Method) &&
        writableSettingsEndpoints.Contains(request.Path.Value ?? string.Empty))
    {
        var userId = request.Query["user_id"].ToString();
        var contentHash = request.Headers["x-content-hash"].ToString();
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(contentHash) ||
            bodyBytes.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "Writable settings require nonempty user_id, x-content-hash, and request body.\n");
            return;
        }

        await partySettingsStore.AppendAsync(
            host,
            request.Path.Value!,
            pathAndQuery,
            userId,
            contentHash,
            request.ContentType,
            bodyBytes,
            context.RequestAborted);

        // Reuse the captured response headers as the secure-response template. The body
        // below is regenerated with CommonResponse.User.Update so the client applies the
        // saved party rows to its in-memory cache immediately.
        var responseTemplatePath =
            $"/api/pvt/store/purchase/restart/steam?user_id={Uri.EscapeDataString(userId)}";
        if (!store.TryGet(host, HttpMethods.Post, responseTemplatePath, out var responseTemplate))
        {
            app.Logger.LogError(
                "SETTINGS WRITE persisted but response template is missing: {Method} {Host}{Path}",
                HttpMethods.Post, host, responseTemplatePath);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(
                "Settings were saved, but no encrypted success-response template is available.\n");
            return;
        }

        context.Response.StatusCode = responseTemplate.StatusCode;
        foreach (var header in responseTemplate.ResponseHeaders)
        {
            if (suppressedHeaders.Contains(header.Name)) continue;
            context.Response.Headers[header.Name] = header.Value;
        }

        var responseBody = partyStateMerger.CreateWriteResponse(
            request.Path.Value!, userId, bodyBytes, responseTemplate.Body, context.Response.Headers);
        if (responseBody is null)
        {
            responseBody = responseTemplate.Body;
            app.Logger.LogWarning(
                "SETTINGS WRITE ACK could not include a client cache update; using {Template}",
                Path.GetFileName(responseTemplate.SourceFile));
        }

        context.Response.ContentLength = responseBody.Length;
        app.Logger.LogInformation(
            "SETTINGS WRITE ACK {Host}{Path} -> {Status} ({Bytes} bytes) with client cache update",
            host, request.Path, responseTemplate.StatusCode, responseBody.Length);
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
