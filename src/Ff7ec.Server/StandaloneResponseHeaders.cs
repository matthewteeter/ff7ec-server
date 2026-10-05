using System.Globalization;
using System.Security.Cryptography;

namespace Ff7ec.Server;

public sealed class StandaloneResponseHeaders(AccountExportStore account, LocalGameContentStore content)
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Apply(HttpRequest request, IHeaderDictionary headers)
    {
        string languagePath = content.LanguageCatalogPath(request.Headers["x-language"].ToString());
        headers["Content-Type"] = "application/protobuf";
        headers["X-Content-Encoding-Secure"] = "1";
        headers["X-Server-Time"] = account.FrozenServerTime.ToString(CultureInfo.InvariantCulture);
        headers["X-Token"] = _token;
        headers["X-Server-Master-Version"] = content.MasterVersion;
        headers["X-Master-Base-Url"] = content.MasterBaseUrl;
        headers["X-Master-Path"] = content.MasterCatalogPath;
        headers["X-Master-Language-Path"] = languagePath;
        headers["X-Announcement-Update-Datetime"] = "0";
    }
}
