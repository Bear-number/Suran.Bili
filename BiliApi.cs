using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Suran.Bili;

/// <summary>B站回的是 JSON 里的 code 不等于 0，或干脆回一张 HTML 页（登录跳转/风控）</summary>
public sealed class BiliException : Exception
{
    public BiliException(int code, string message, string hint = "")
        : base(message + (hint.Length == 0 ? "" : "｜" + hint))
    {
        Code = code;
    }
    public BiliException(string message) : base(message)
    {
        Code = -1;
    }
    public int Code { get; }
}

/// <summary>
/// B站 web 接口的 HTTP 底座。免登录也必须带浏览器 UA + 对应站区的 Referer，
/// 否则就是间歇性 -352/-412；弹幕那个 xml 接口是 deflate 裸回，所以解压要开在这里。
/// </summary>
public sealed class BiliApi : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static readonly Dictionary<int, string> Hints = new() {
        [-101] = "没登录或登录过期：先 BiliLogin(action=generate) 让主人扫一下码",
        [-104] = "登录状态无效（cookie 已失效），要重新扫码",
        [-352] = "风控校验失败：B站要真实浏览器的指纹，纯 HTTP 绕不过去，也别重试",
        [-400] = "请求参数不对（多为缺必填或取值不合法）",
        [-403] = "访问权限不足：这条接口要么要 wbi 签名要么要登录",
        [-412] = "请求被拦（UA 或 Referer 不对），这是接口级拦截，不是账号问题",
        [2] = "参数错误",
        [10002] = "视频不存在或已被删除/加密",
        [12002] = "该页面的评论功能已关闭，或 oid 用错了（评论要的是 aid 不是 bvid）",
        [12006] = "没有这条评论（root 用错或楼中楼已被删）",
        [12008] = "内容被风控屏蔽（评论/弹幕常见，改措辞或别发）",
        [137] = "每日操作次数到达上限，明天再来",
        [3] = "账号权限不足或未登录",
        [-509] = "请求过于频繁，被限流了，先停手",
        [-799] = "请求太频繁（csrf 或参数重复），等几秒",
    };
    public static string Hint(int code) => Hints.GetValueOrDefault(code, "");
    public static string Describe(int code, string message)
    {
        string hint = Hint(code);
        return "接口回 code=" + code + "，message=" + (message.Length == 0 ? "（空）" : message)
            + (hint.Length == 0 ? "" : "。" + hint);
    }

    /// <summary>B站按“来源站区”查 Referer：搜索、空间、直播、弹幕各有要求</summary>
    public static string RefererFor(string url)
    {
        Uri parsed = new(url, UriKind.Absolute);
        return parsed.Host switch {
            "api.bilibili.com" when parsed.AbsolutePath.StartsWith("/x/web-interface/search") => "https://search.bilibili.com/",
            "api.bilibili.com" when parsed.AbsolutePath.StartsWith("/x/space") => "https://space.bilibili.com/",
            "api.bilibili.com" when parsed.AbsolutePath.StartsWith("/x/v2/reply") => "https://www.bilibili.com/",
            "api.bilibili.com" when parsed.AbsolutePath.StartsWith("/x/v3/fav") => "https://space.bilibili.com/",
            "api.live.bilibili.com" or "live.bilibili.com" => "https://live.bilibili.com/",
            "passport.bilibili.com" => "https://www.bilibili.com/",
            "comment.bilibili.com" => "https://www.bilibili.com/",
            _ => "https://www.bilibili.com/"
        };
    }
    /// <summary>BV 号/aid/纯数字链接统一成能用的形态</summary>
    public static (string Bvid, long Aid) ParseTarget(string target)
    {
        string text = target.Trim();
        System.Text.RegularExpressions.Match bv = System.Text.RegularExpressions.Regex.Match(text, "BV[0-9A-Za-z]{10}");
        System.Text.RegularExpressions.Match av = System.Text.RegularExpressions.Regex.Match(text, "(?i)av(\\d+)");
        long aid = 0;
        if (bv.Success == false && av.Success)
        {
            aid = long.Parse(av.Groups[1].Value);
        }
        else if (bv.Success == false && long.TryParse(text, out long plain) && !text.Contains("http"))
        {
            aid = plain;
        }
        else if (bv.Success == false)
        {
            System.Text.RegularExpressions.Match aidInUrl = System.Text.RegularExpressions.Regex.Match(text, "[?&/]aid=(\\d+)");
            if (aidInUrl.Success)
            {
                aid = long.Parse(aidInUrl.Groups[1].Value);
            }
        }
        return (bv.Success ? bv.Value : "", aid);
    }
    public static string Query(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        return string.Join("&", pairs.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? "")));
    }

    const string SpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";

    readonly HttpClient http;
    readonly SemaphoreSlim turn = new(1, 1);
    readonly object clock = new();
    Func<string>? cookieProvider;
    string buvidCookie = "";
    DateTime lastSentAt = DateTime.MinValue;

    /// <summary>
    /// 本机 2026-10-24 实测：不带 buvid 连打第三次起会被粘滞性 412（要等好几分钟），
    /// 带上 finger/spi 发的 buvid3/buvid4 连打五次全 200；而 Accept 发 */* 或 application/json 这类直接 412，
    /// 所以干脆不发 Accept。间隔是第二道保险。
    /// </summary>
    public BiliApi(string proxy, int timeoutSeconds, int minRequestGapMs = 350)
    {
        HttpClientHandler handler = new() {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        };
        if (proxy.Trim().Length > 0)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy.Trim());
        }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 180)) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        MinRequestGapMs = Math.Clamp(minRequestGapMs, 0, 5000);
    }

    public int MinRequestGapMs { get; }
    public string Buvid => buvidCookie;
    public string? LastBanReason { get; private set; }

    public void UseCookie(Func<string>? provider) => cookieProvider = provider;
    public bool HasCookie => cookieProvider?.Invoke().Length > 0;

    public async Task<JsonNode> GetAsync(string url, CancellationToken cancellationToken, string? referer = null)
    {
        return Parse(await SendAsync(url, null, referer, cancellationToken), url);
    }
    public async Task<JsonNode> PostAsync(string url, IDictionary<string, string> form, CancellationToken cancellationToken, string? referer = null)
    {
        return Parse(await SendAsync(url, form, referer, cancellationToken), url);
    }
    /// <summary>扫码轮询要同时收 Set-Cookie 头，所以单独给一条能拿响应头的路</summary>
    public async Task<(string Body, string SetCookie)> GetWithSetCookieAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", RefererFor(url));
        string cookie = cookieProvider?.Invoke() ?? "";
        if (cookie.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string setCookie = string.Join(",", response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : Array.Empty<string>());
        return (body, setCookie);
    }
    /// <summary>拿原始文本（弹幕那个 xml 接口不是 JSON）</summary>
    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken, string? referer = null)
    {
        return await SendAsync(url, null, referer, cancellationToken);
    }
    /// <summary>
    /// 直播那一段的解析整段是按 JsonElement 写的、并且带着一批实测断言，合并过来不重写它，
    /// 只把传输层换成这个：错误码照样进话术表，直播接口用 message/msg 两种键名。
    /// </summary>
    public async Task<JsonElement> GetElementAsync(string url, CancellationToken cancellationToken, string? referer = null)
    {
        using JsonDocument parsed = JsonDocument.Parse(await SendAsync(url, null, referer, cancellationToken));
        JsonElement root = parsed.RootElement.Clone();
        long code = ElementNumber(root, "code");
        if (code != 0)
        {
            string detail = ElementText(root, "message");
            if (detail.Length == 0)
            {
                detail = ElementText(root, "msg");
            }
            throw new BiliException((int)code, Describe((int)code, detail));
        }
        return root.TryGetProperty("data", out JsonElement data) ? data.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }
    static string ElementText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }
        return value.ValueKind switch {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }
    static long ElementNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }
        if (value.TryGetInt64(out long number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long text) ? text : 0;
    }

    async Task<string> SendAsync(string url, IDictionary<string, string>? form, string? referer, CancellationToken cancellationToken)
    {
        await EnsureBuvidAsync(cancellationToken);
        string body = await SendOnceAsync(url, form, referer, cancellationToken);
        if (LooksBanned(body))
        {
            buvidCookie = "";
            await EnsureBuvidAsync(cancellationToken, force: true);
            body = await SendOnceAsync(url, form, referer, cancellationToken);
            if (LooksBanned(body))
            {
                LastBanReason = "B站回的是网页而不是 JSON：要么被频控（412，换指纹重试过一次还是不通就得等几分钟，"
                    + "越轰挡得越久，必要时调大「请求最小间隔」），要么这条 URL 根本不是接口地址";
                throw new BiliException(-1, LastBanReason);
            }
        }
        return body;
    }
    /// <summary>
    /// 被挡时 B站回的是那张跳转/警告网页。只认 HTML 文档头——弹幕那个接口正当回的就是 &lt;?xml，不能一起当被挡。
    /// </summary>
    static bool LooksBanned(string body)
    {
        string text = body.TrimStart();
        return text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Precondition Failed");
    }
    async Task<string> SendOnceAsync(string url, IDictionary<string, string>? form, string? referer, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            await SpaceOut(cancellationToken);
            using HttpRequestMessage request = new(form == null ? HttpMethod.Get : HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Referer", referer ?? RefererFor(url));
            if (form != null)
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
            }
            string cookie = MergeCookie(cookieProvider?.Invoke() ?? "");
            if (cookie.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            if (form != null)
            {
                request.Content = new FormUrlEncodedContent(form);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            }
            MarkSent();
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new BiliException(-1, "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase
                    + "（" + new Uri(url).Host + new Uri(url).AbsolutePath + "）"
                    + ((int)response.StatusCode == 412 ? "：这是频控，不是参数写错" : ""));
            }
            return body;
        }
        finally
        {
            turn.Release();
        }
    }
    void MarkSent()
    {
        lock (clock)
        {
            lastSentAt = DateTime.Now;
        }
    }
    async Task SpaceOut(CancellationToken cancellationToken)
    {
        int wait;
        lock (clock)
        {
            wait = MinRequestGapMs <= 0 || lastSentAt == DateTime.MinValue ? 0
                : MinRequestGapMs - (int)(DateTime.Now - lastSentAt).TotalMilliseconds;
        }
        if (wait > 0)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }
    string MergeCookie(string accountCookie)
    {
        if (buvidCookie.Length == 0)
        {
            return accountCookie;
        }
        return accountCookie.Length == 0 ? buvidCookie : buvidCookie + "; " + accountCookie;
    }
    async Task EnsureBuvidAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (buvidCookie.Length > 0 && !force)
        {
            return;
        }
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, SpiUrl);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            JsonNode? data = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["data"];
            string b3 = BiliJson.Text(data, "b_3");
            string b4 = BiliJson.Text(data, "b_4");
            if (b3.Length > 0)
            {
                buvidCookie = "buvid3=" + b3 + (b4.Length > 0 ? "; buvid4=" + b4 : "");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 领不到指纹就只靠最小间隔撑着：真被 412 挡住时上面会报出明确原因
        }
    }
    static JsonNode Parse(string body, string url)
    {
        string text = body.Trim();
        if (text.StartsWith("{") == false)
        {
            throw new BiliException(-1, "接口没回 JSON，回的是" + (text.StartsWith("<") ? "一张网页（多半是没登录被跳走，或被风控挡了）" : "认不出的内容")
                + "：" + (text.Length > 80 ? text[..80] : text) + "（" + new Uri(url).AbsolutePath + "）");
        }
        JsonNode? node = JsonNode.Parse(text);
        int code = (int)BiliJson.Number(node, "code");
        string message = BiliJson.Text(node, "message");
        if (code != 0)
        {
            throw new BiliException(code, Describe(code, message));
        }
        return node ?? throw new BiliException(-1, "接口回了个空 JSON");
    }
    public void Dispose() => http.Dispose();
}

public static class BiliJson
{
    public static string Text(JsonNode? node, string name, string fallback = "")
    {
        JsonNode? value = node?[name];
        if (value == null)
        {
            return fallback;
        }
        return value.GetValueKind() switch {
            JsonValueKind.String => value.GetValue<string>() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>().ToString(),
            _ => fallback
        };
    }
    /// <summary>节点本身就是个数字时用这个（列表页的 count 那种）</summary>
    public static double Value(JsonNode? node, double fallback = 0)
    {
        if (node == null)
        {
            return fallback;
        }
        try
        {
            return node.GetValue<double>();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return double.TryParse(node.ToString(), out double parsed) ? parsed : fallback;
        }
    }
    public static double Number(JsonNode? node, string name, double fallback = 0)
    {
        JsonNode? value = node?[name];
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return value.GetValue<double>();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            // B站一半的数字字段回的是字符串（"mid":"2"、"follower":"143万" 这类），走文本兜底
            return double.TryParse(value.ToString(), out double parsed) ? parsed : fallback;
        }
    }
    public static bool Flag(JsonNode? node, string name)
    {
        JsonNode? value = node?[name];
        if (value == null)
        {
            return false;
        }
        try
        {
            return value.GetValue<bool>();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return bool.TryParse(value.ToString(), out bool parsed) && parsed;
        }
    }
    public static JsonNode? Pick(JsonNode? node, params string[] path)
    {
        JsonNode? current = node;
        foreach (string key in path)
        {
            current = current?[key];
            if (current == null)
            {
                return null;
            }
        }
        return current;
    }
}
