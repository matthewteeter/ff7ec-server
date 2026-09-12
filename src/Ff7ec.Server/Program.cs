using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Extensions;
using Ff7ec.Server;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration.GetSection("Ff7ec");
int listenPort = config.GetValue("ListenPort", 443);
string capturesDir = config["CapturesDirectory"] ?? throw new InvalidOperationException("Ff7ec:CapturesDirectory not configured");
string certDir = config["CertDirectory"] ?? throw new InvalidOperationException("Ff7ec:CertDirectory not configured");
string gapsDir = config["GapsDirectory"] ?? throw new InvalidOperationException("Ff7ec:GapsDirectory not configured");
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

var app = builder.Build();

// Force-load the store at startup (rather than on first request) so load errors and the
// captured-response count show up immediately in the console.
var store = app.Services.GetRequiredService<ReplayStore>();
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

app.Run(async context =>
{
    var request = context.Request;
    var host = request.Headers.Host.ToString();
    var pathAndQuery = request.GetEncodedPathAndQuery();

    using var bodyStream = new MemoryStream();
    await request.Body.CopyToAsync(bodyStream);
    var bodyBytes = bodyStream.ToArray();

    if (store.TryGet(host, request.Method, pathAndQuery, out var captured))
    {
        context.Response.StatusCode = captured.StatusCode;
        foreach (var header in captured.ResponseHeaders)
        {
            if (suppressedHeaders.Contains(header.Name)) continue;
            context.Response.Headers[header.Name] = header.Value;
        }
        app.Logger.LogInformation("REPLAY {Method} {Host}{Path} -> {Status} ({Bytes} bytes) [{Source}]",
            request.Method, host, pathAndQuery, captured.StatusCode, captured.Body.Length, Path.GetFileName(captured.SourceFile));
        await context.Response.Body.WriteAsync(captured.Body);
        return;
    }

    var gapLogger = app.Services.GetRequiredService<GapLogger>();
    await gapLogger.LogAsync(request, bodyBytes);
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync("FF7EC offline server: no captured response for this request.\n");
});

app.Run();
