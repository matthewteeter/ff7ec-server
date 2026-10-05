using System.Text.Json;

namespace Ff7ec.Server;

public sealed class StandaloneApi
{
    private readonly AccountExportStore _account;
    private readonly LocalGameContentStore _content;
    private readonly string _apiHost;
    private readonly string _webviewHost;
    private static readonly Dictionary<string, string> Endpoints = new(StringComparer.Ordinal)
    {
        ["/api/check"] = "GetCheck",
        ["/api/announcement/list"] = "PostAnnouncementList",
        ["/api/pvt/notice/check"] = "PostPvtNoticeCheck",
        ["/api/pvt/store/purchase/restart/steam"] = "PostPvtStorePurchaseRestartSteam",
    };

    public StandaloneApi(AccountExportStore account, LocalGameContentStore content, string apiHost, string webviewHost)
    {
        _account = account;
        _content = content;
        _apiHost = apiHost;
        _webviewHost = webviewHost;
        foreach (var (path, endpoint) in Endpoints)
            account.ValidateEndpointSchema(endpoint, Payload(path));
    }

    public bool Handles(HttpRequest request) =>
        request.Host.Host.Equals(_apiHost, StringComparison.OrdinalIgnoreCase) &&
        Endpoints.ContainsKey(request.Path.Value ?? "");

    public bool HasCorrectMethod(HttpRequest request) =>
        request.Path == "/api/check" ? HttpMethods.IsGet(request.Method) : HttpMethods.IsPost(request.Method);

    public byte[] CreateResponse(HttpRequest request, byte[] body, IHeaderDictionary headers)
    {
        string path = request.Path.Value!;
        return _account.CreateEndpointResponse(Endpoints[path], body, Payload(path), headers,
            validateRequest: path != "/api/check", includeUser: path.StartsWith("/api/pvt/", StringComparison.Ordinal));
    }

    private JsonElement Payload(string path)
    {
        var values = new Dictionary<string, object?>();
        if (path == "/api/check")
        {
            values = new()
            {
                ["IsMaintenance"] = false, ["IsForceUpdate"] = false, ["IsReview"] = false,
                // SystemOperator.CreateUrl adds the HTTPS scheme to these host values.
                ["ApiUrl"] = _apiHost, ["TermsUpdateTimes"] = 0L,
                ["OctoVersion"] = _content.OctoVersion, ["OctoUrl"] = _content.OctoHost,
                ["WebviewUrl"] = _webviewHost, ["ChatUrl"] = _apiHost,
                ["IsServiceUpdate"] = false,
            };
        }
        else if (path == "/api/announcement/list")
            values["Total"] = 0L;
        else if (path == "/api/pvt/notice/check")
            values = new()
            {
                ["IsReceiveFriendRequest"] = false, ["IsRefreshGuild"] = false,
                ["ChatGuildLatestMessageId"] = "", ["GuildJoinRequestLastReceiveDatetime"] = 0L,
                ["UnreadLastUserPersonalMessageId"] = 0L,
            };
        else
            values["BridgeTransactionId"] = "";
        return JsonSerializer.SerializeToElement(values);
    }
}
