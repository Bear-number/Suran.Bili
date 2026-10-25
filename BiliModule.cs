using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Suran.Bili;

[Module("B站查询与互动",
    "B 站（bilibili）全功能：视频详情与分P、关键词搜索（可按播放/收藏/最新排）、热门、相关推荐、UP 主资料与粉丝数、"
    + "评论区与楼中楼、弹幕全文与高频词；扫码登录后还能看收藏夹、稍后再看、历史记录、关注列表，"
    + "并在你打开写开关后点赞、投币、收藏、关注、回评论、发弹幕（默认关，且默认要主人确认）；直播侧是分区树、各大区在线人气榜、大区人气一览、单个房间或主播在不在播。",
    defaultCategory: "苏染的工具/B站")]
public partial class BiliModule(
    XmlFunctionCaller functionCaller,
    Interactor<BiliModule> interactor) :
    ChatBehaviour,
    IConfigurable<BiliConfig>
{
    public BiliConfig Configuration { get; set; } = null!;
    public BiliGuard Guard { get; } = new();
    public BiliPendings Pendings { get; } = new();

    BiliApi? shared;
    string sharedKey = "";

    /// <summary>共享一个底座实例：buvid 只领一次，最小间隔才真的存在；配置改了才重建</summary>
    BiliApi OpenApi()
    {
        string key = Configuration.ProxyUrl.Trim() + "|" + Configuration.RequestTimeoutSeconds + "|" + Configuration.MinRequestGapMs;
        if (shared != null && sharedKey == key)
        {
            return shared;
        }
        shared?.Dispose();
        BiliApi api = new(Configuration.ProxyUrl, Configuration.RequestTimeoutSeconds, Configuration.MinRequestGapMs);
        api.UseCookie(() => Auth.IsLoaded ? Auth.Header : "");
        shared = api;
        sharedKey = key;
        return api;
    }
    BiliAuth Auth => new(AlifePath.StorageFolderPath, Configuration.CookieFolder);
    int Take(int? requested) => Math.Clamp(requested ?? Configuration.DefaultItemCount, 1, 50);
    int ItemCount => Math.Clamp(Configuration.DefaultItemCount, 1, 50);

    protected override Task OnDestroy()
    {
        // 共享底座的 HttpClient 由这里收一次；热重载不留悬挂连接
        shared?.Dispose();
        shared = null;
        return Task.CompletedTask;
    }
    protected override Task OnAwake()
    {
        XmlHandler handler = new(this) {
            Description = "B 站内容查询与账号互动：看视频信息/搜索/热门/推荐/UP主/评论/弹幕，登录后还有收藏夹、稍后再看、历史、关注，"
                + "以及点赞投币收藏关注回评论发弹幕（写操作默认关）",
            Explanation = "先看 BiliEnv 摸清现在能不能用。免登录就能查视频、搜索、热门、推荐、用户、粉丝、评论、弹幕。"
                + "要看自己的收藏夹/历史/稍后再看，先 BiliLogin(action=generate) 把登录链接发给主人扫（这链接能登录账号，只走私聊别发群里）。"
                + "写动作（点赞/投币/收藏/关注/评论/弹幕）默认关，开着时也是先登记、拿到确认码再 BiliConfirm 执行。"
                + "被 -352 风控挡住是 B站在要真实浏览器指纹，不是参数写错，别重试。"
                + "写动作里只有「点赞」本机实测通过（同一端点带 like=1|2，用 has/like 即时回查，别看点赞总数——它有分钟级延迟）；"
                + "投币/收藏/关注/评论/弹幕/发动态/删动态是照文档拼的，还没实机验过，被 -400 或 -352 拒就照实说，别硬重试。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查视频详情：标题、UP主、分区、时长、发布时间、播放/点赞/投币/收藏/弹幕数、分P 列表。target 支持 BV号、av号、纯 aid 或整条链接")]
    public async Task BiliVideo(string target)
    {
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(ViewUrl(target), DestroyCancellationToken);
        JsonNode item = data["data"]!;
        interactor.Poke(DescribeVideo(item, data));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索视频：order 可填 totalrank(综合)/click(播放)/stow(收藏)/coins(投币)/dm(弹幕)/newest(最新)，留空用配置默认；page 翻页")]
    public async Task BiliSearch(string keyword, string order = "", int page = 1, int? take = null)
    {
        string sort = (order.Trim().Length > 0 ? order : Configuration.SearchOrder).Trim();
        string url = "https://api.bilibili.com/x/web-interface/search/type?" + BiliApi.Query(new Dictionary<string, string> {
            ["search_type"] = "video", ["keyword"] = keyword, ["page"] = Math.Clamp(page, 1, 100).ToString(CultureInfo.InvariantCulture)
        }) + (sort.Length > 0 ? "&order=" + Uri.EscapeDataString(sort) : "");
        BiliApi api = OpenApi();
        JsonNode data;
        try
        {
            data = await api.GetAsync(url, DestroyCancellationToken);
        }
        catch (BiliException e) when (e.Code == -352 && sort.Length > 0)
        {
            throw new Exception("带排序 " + sort + " 的搜索被风控挡了（-352）。换 order 留空（综合）再试一次——"
                + "这是 B站对非浏览器请求的拦截，不是关键词写错");
        }
        List<JsonNode?> results = ((data["data"]?["result"]?.AsArray()) ?? new JsonArray()).ToList();
        if (results.Count == 0)
        {
            interactor.Poke("搜「" + keyword + "」" + (sort.Length > 0 ? "（order=" + sort + "）" : "") + " 没有结果");
            return;
        }
        StringBuilder text = new("B站搜「" + keyword + "」" + (sort.Length == 0 ? "综合" : "按 " + sort)
            + "，第 " + page + " 页 " + results.Count + " 条（共 " + BiliJson.Text(data["data"], "numResults") + " 条）：");
        foreach (JsonNode? item in results.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeCard(item, "     "));
        }
        text.Append("\n要详情就 BiliVideo(BV号)，要发卡片/下载是别的插件的活");
        interactor.Poke(text.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("B站热门视频列表（免登录），page 翻页")]
    public async Task BiliHot(int page = 1, int? take = null)
    {
        string url = "https://api.bilibili.com/x/web-interface/popular?ps=" + ItemCount + "&pn=" + Math.Clamp(page, 1, 50).ToString(CultureInfo.InvariantCulture);
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken);
        List<JsonNode?> list = ((data["data"]?["list"]?.AsArray()) ?? new JsonArray()).ToList();
        StringBuilder text = new("B站热门第 " + page + " 页（" + list.Count + " 条" + (BiliJson.Flag(data["data"], "no_more") ? "，到底了" : "") + "）：");
        foreach (JsonNode? item in list.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeCard(item, "     "));
        }
        interactor.Poke(text.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("某个视频的相关推荐（B站自己给的那批），target 同 BiliVideo")]
    public async Task BiliRelated(string target, int? take = null)
    {
        (string bvid, long aid) = BiliApi.ParseTarget(target);
        string key = bvid.Length > 0 ? "bvid=" + bvid : "aid=" + aid.ToString(CultureInfo.InvariantCulture);
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync("https://api.bilibili.com/x/web-interface/archive/related?" + key, DestroyCancellationToken);
        List<JsonNode?> list = ((data["data"]?.AsArray()) ?? new JsonArray()).ToList();
        if (list.Count == 0)
        {
            interactor.Poke("这条视频没给相关推荐（" + (bvid.Length > 0 ? bvid : "av" + aid) + "）");
            return;
        }
        StringBuilder text = new("相关推荐（" + list.Count + " 条，取前 " + Math.Min(list.Count, Take(take)) + " 条）：");
        foreach (JsonNode? item in list.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeCard(item, "     "));
        }
        interactor.Poke(text.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查 UP 主资料：昵称、等级、签名、认证、粉丝数、投稿总数。mid 是 UID（纯数字）")]
    public async Task BiliUser(string mid)
    {
        if (long.TryParse(mid.Trim().Replace(" ", ""), out long uid) == false)
        {
            (string _, uid) = await ResolveAuthorAsync(mid);
        }
        BiliApi api = OpenApi();
        JsonNode card = await api.GetAsync("https://api.bilibili.com/x/web-interface/card?mid=" + uid.ToString(CultureInfo.InvariantCulture)
            + "&photo=false", DestroyCancellationToken);
        JsonNode stat = await api.GetAsync("https://api.bilibili.com/x/relation/stat?vmid=" + uid.ToString(CultureInfo.InvariantCulture), DestroyCancellationToken);
        JsonNode user = card["data"]?["card"] ?? throw new Exception("用户接口没给 card 层，形状可能变了");
        List<string> lines = new() {
            user["name"] + "（UID " + uid.ToString(CultureInfo.InvariantCulture) + "，等级 Lv" + BiliJson.Text(user, "level")
                + "，" + BiliJson.Text(stat["data"], "follower") + " 粉丝，关注 " + BiliJson.Text(stat["data"], "following") + "）",
            "签名：" + (StripHtml(BiliJson.Text(user, "sign")).Length == 0 ? "（没写）" : StripHtml(BiliJson.Text(user, "sign"))),
            "投稿 " + BiliJson.Text(card["data"], "archive_count") + " 个，空间 https://space.bilibili.com/" + uid.ToString(CultureInfo.InvariantCulture)
        };
        string official = StripHtml(BiliJson.Text(user, "Official.content") ?? "");
        if (official.Length > 0)
        {
            lines.Add("认证：" + official);
        }
        lines.Add("按 UID 列投稿要 wbi 签名，本机实测被 -352 风控挡住，所以这里查不到对方的投稿清单；"
            + "要追某人的更新可以 BiliSearch 关键词或直接看热门/推荐");
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("看评论区：sort 可填 hot/time，page 翻页。本机实测免登录时 sort=hot 会整个回空列表，"
        + "所以按热度取不到时插件会自动退回按时间取并说明；返回楼层、赞数、回复数，楼中楼用 BiliSubReplies")]
    public async Task BiliReplies(string target, string sort = "hot", int page = 1, int? take = null)
    {
        long aid = await ResolveAidAsync(target);
        List<string> tries = sort.Trim() == "time" ? new List<string> { "2" } : new List<string> { "0", "2" };
        List<JsonNode?> floors = new();
        long total = 0;
        string used = "";
        foreach (string code in tries)
        {
            JsonNode data = await ReplyPageAsync(aid, code, page);
            floors = ((data["data"]?["replies"]?.AsArray()) ?? new JsonArray()).ToList();
            total = (long)BiliJson.Value(BiliJson.Pick(data, "data", "page", "count"));
            used = code;
            if (floors.Count > 0)
            {
                break;
            }
        }
        if (floors.Count == 0)
        {
            interactor.Poke("这条视频的评论区取不到楼层（aid " + aid.ToString(CultureInfo.InvariantCulture)
                + "，声明 " + total + " 条）。12002 是评论区被关或 oid 用错，-352 是被风控");
            return;
        }
        StringBuilder text = new("评论第 " + page + " 页（总 " + total + " 条，按 "
            + (used == "2" ? "时间" : "热度")
            + (used == "2" && tries.Count > 1 ? "；按热度那次回的是空列表，已自动改用时间" : "") + "）：");
        foreach (JsonNode? reply in floors.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeReply(reply));
        }
        text.Append("\n想看某条下面的回复：BiliSubReplies(target, root=楼层参数里的 rpid)");
        interactor.Poke(text.ToString());
    }
    async Task<JsonNode> ReplyPageAsync(long aid, string sortCode, int page)
    {
        string url = "https://api.bilibili.com/x/v2/reply?type=1&oid=" + aid.ToString(CultureInfo.InvariantCulture)
            + "&pn=" + Math.Clamp(page, 1, 1000).ToString(CultureInfo.InvariantCulture)
            + "&ps=" + ItemCount + "&sort=" + sortCode;
        BiliApi api = OpenApi();
        return await api.GetAsync(url, DestroyCancellationToken);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("某条主评论下面的楼中楼。root 传 BiliReplies 回报里的 rpid")]
    public async Task BiliSubReplies(string target, long root, int? take = null)
    {
        long aid = await ResolveAidAsync(target);
        string url = "https://api.bilibili.com/x/v2/reply/reply?type=1&oid=" + aid.ToString(CultureInfo.InvariantCulture)
            + "&root=" + root.ToString(CultureInfo.InvariantCulture) + "&pn=1&ps=" + ItemCount;
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken);
        List<JsonNode?> replies = ((data["data"]?["replies"]?.AsArray()) ?? new JsonArray()).ToList();
        if (replies.Count == 0)
        {
            interactor.Poke("rpid " + root.ToString(CultureInfo.InvariantCulture) + " 下面没有回复，或这条楼中楼接口被挡（12006=没有该评论）");
            return;
        }
        StringBuilder text = new("rpid " + root.ToString(CultureInfo.InvariantCulture) + " 下的回复（" + replies.Count + " 条）：");
        foreach (JsonNode? reply in replies.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeReply(reply));
        }
        interactor.Poke(text.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("拉弹幕做统计与检索：默认给条数/密度/高频词/最密时段；传 at=秒数 取那一段的弹幕原文，传 word 找提到某词的所有弹幕")]
    public async Task BiliDanmaku(string target, int page = 1, double at = 0, string word = "", int? take = null)
    {
        (string bvid, long aid) = BiliApi.ParseTarget(target);
        JsonNode data = await VideoNodeAsync(target);
        long cid = PageCid(data, page);
        if (cid == 0)
        {
            throw new Exception("取不到这一话的 cid（弹幕按 cid 分片），page 参数是从 1 开始的分P序号");
        }
        string xml = await OpenApi().GetTextAsync("https://comment.bilibili.com/" + cid.ToString(CultureInfo.InvariantCulture) + ".xml",
            DestroyCancellationToken, "https://www.bilibili.com/video/" + (bvid.Length > 0 ? bvid : "av" + aid.ToString(CultureInfo.InvariantCulture)));
        List<BiliShot> shots = BiliDanmakuParser.Parse(xml);
        if (word.Trim().Length > 0)
        {
            List<BiliShot> hits = shots.Where(shot => shot.Text.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
            interactor.Poke("弹幕里提到「" + word + "」的 " + hits.Count + " 条（共 " + shots.Count + " 条）：\n"
                + (hits.Count == 0 ? "（一条没有）" : string.Join("\n", hits.Take(Take(take)).Select(shot => shot.Line()))));
            return;
        }
        if (at > 0)
        {
            List<BiliShot> window = BiliDanmakuParser.Around(shots, at, 15);
            interactor.Poke("视频 " + TimeSpan.FromSeconds(at).ToString(@"mm\:ss", CultureInfo.InvariantCulture)
                + " 前后 15 秒的弹幕 " + window.Count + " 条：\n"
                + (window.Count == 0 ? "（这段没有）" : string.Join("\n", window.Take(Take(take)).Select(shot => shot.Line()))));
            return;
        }
        interactor.Poke((bvid.Length > 0 ? bvid : "av" + aid.ToString(CultureInfo.InvariantCulture)) + " 第 " + page + " 话（cid "
            + cid.ToString(CultureInfo.InvariantCulture) + "）：" + BiliDanmakuParser.Summarize(shots, Take(take))
            + "\n要某段原文传 at=秒数，要查某个词传 word=");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自检：免登录四条主接口通不通、写开关与今日额度、cookie 状态（只报字段名不报值）、风控情况")]
    public async Task BiliEnv()
    {
        BiliAuth auth = Auth;
        List<string> lines = new() { "接口连通（免登录）：" };
        foreach ((string name, string url) in new (string, string)[] {
            ("视频详情", "https://api.bilibili.com/x/web-interface/view?bvid=BV1GJ411x7h7"),
            ("搜索", "https://api.bilibili.com/x/web-interface/search/type?search_type=video&keyword=%E6%B5%8B%E8%AF%95&page=1"),
            ("热门", "https://api.bilibili.com/x/web-interface/popular?ps=2&pn=1"),
            ("评论", "https://api.bilibili.com/x/v2/reply?type=1&oid=80433022&pn=1&ps=2&sort=0"),
            ("UP主投稿(wbi)", "https://api.bilibili.com/x/space/wbi/arc/search?mid=2&ps=2&pn=1")
        })
        {
            lines.Add("· " + name + "：" + await ProbeAsync(url));
        }
        lines.Add("登录：cookie 文件 " + auth.FilePath + "，" + (auth.IsLoaded
            ? "已存 " + auth.Count + " 项（字段名：" + string.Join(",", auth.Names.OrderBy(n => n)) + "），保存于 "
                + auth.SavedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + (auth.HasLoginCookies() ? "，键齐全" : "，缺登录键")
            : "还没有（免登录功能照常用）"));
        if (auth.IsLoaded)
        {
            lines.Add("登录态复验：" + await ProbeNavAsync());
        }
        lines.Add("写操作：" + (Configuration.EnableWrite ? "开关开" : "开关关") + "，" + (Configuration.RequireConfirm ? "要确认码" : "直接发")
            + "，每种每天上限 " + Configuration.DailyWriteLimit + " 次，间隔 " + Configuration.MinWriteGapSeconds + " 秒；今天已用 " + Guard.TotalToday + " 次");
        lines.Add("网络：" + (Configuration.ProxyUrl.Trim().Length == 0 ? "直连" : "走代理 " + Configuration.ProxyUrl.Trim())
            + "，超时 " + Configuration.RequestTimeoutSeconds + " 秒；UA 与 Referer 是必带的，缺了就间歇 -412/-352");
        lines.Add("待确认写动作：" + Pendings.CountActive(DateTime.Now) + " 条");
        interactor.Poke(string.Join("\n", lines));
    }

    async Task<string> ProbeAsync(string url)
    {
        try
        {
            BiliApi api = OpenApi();
            JsonNode data = await api.GetAsync(url, DestroyCancellationToken);
            return "code 0（data 有内容：" + (data["data"]?.ToJsonString().Length ?? 0) + " 字）";
        }
        catch (BiliException e)
        {
            return "code " + e.Code + (BiliApi.Hint(e.Code).Length == 0 ? "，" + e.Message : "，" + BiliApi.Hint(e.Code));
        }
        catch (Exception e)
        {
            return "不通：" + e.Message;
        }
    }
    async Task<string> ProbeNavAsync()
    {
        try
        {
            BiliApi api = OpenApi();
            JsonNode data = await api.GetAsync("https://api.bilibili.com/x/web-interface/nav", DestroyCancellationToken);
            return BiliJson.Flag(data["data"], "isLogin")
                ? "在登录态：" + BiliJson.Text(data["data"], "uname") + "（UID " + BiliJson.Text(data["data"], "mid") + "）"
                : "回 isLogin=false，cookie 已废，要重新扫码";
        }
        catch (BiliException e)
        {
            return "nav 查失败 code=" + e.Code + "（" + e.Message + "）";
        }
    }
    /// <summary>弹幕按 cid 分片，page 就是第几话（从 1 开始）</summary>
    static long PageCid(JsonNode data, int page)
    {
        JsonNode? list = BiliJson.Pick(data, "data", "pages");
        if (list is not JsonArray array || array.Count == 0)
        {
            return 0;
        }
        JsonNode? shot = array[Math.Clamp(page, 1, array.Count) - 1];
        return (long)BiliJson.Number(shot, "cid");
    }

    string ViewUrl(string target)
    {
        (string bvid, long aid) = BiliApi.ParseTarget(target);
        if (bvid.Length > 0)
        {
            return "https://api.bilibili.com/x/web-interface/view?bvid=" + bvid;
        }
        if (aid != 0)
        {
            return "https://api.bilibili.com/x/web-interface/view?aid=" + aid.ToString(CultureInfo.InvariantCulture);
        }
        throw new Exception("看不懂这条视频目标「" + target + "」，给 BV号、av号、纯 aid 或整条链接");
    }
    async Task<JsonNode> VideoNodeAsync(string target)
    {
        BiliApi api = OpenApi();
        return await api.GetAsync(ViewUrl(target), DestroyCancellationToken);
    }
    async Task<long> ResolveAidAsync(string target)
    {
        (string _, long aid) = BiliApi.ParseTarget(target);
        if (aid != 0)
        {
            return aid;
        }
        JsonNode data = await VideoNodeAsync(target);
        return (long)BiliJson.Number(data["data"], "aid");
    }
    async Task<(string Name, long Mid)> ResolveAuthorAsync(string target)
    {
        JsonNode data = await VideoNodeAsync(target);
        return (BiliJson.Text(BiliJson.Pick(data, "data", "owner"), "name"),
            (long)BiliJson.Number(BiliJson.Pick(data, "data", "owner"), "mid"));
    }
    string DescribeVideo(JsonNode item, JsonNode data)
    {
        List<string> lines = new() {
            StripHtml(BiliJson.Text(item, "title")) + "（" + BiliJson.Text(item, "bvid") + " / av" + BiliJson.Text(item, "aid") + "）",
            "UP：" + BiliJson.Text(BiliJson.Pick(item, "owner"), "name") + "（UID " + BiliJson.Text(BiliJson.Pick(item, "owner"), "mid") + "）"
                + "｜分区 " + BiliJson.Text(item, "tname_v2") + "/" + BiliJson.Text(item, "tname")
                + "｜" + HumanDuration(BiliJson.Number(item, "duration")) + "｜" + HumanDate(BiliJson.Number(item, "pubdate")),
            "播放 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "view"))
                + "｜点赞 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "like"))
                + "｜投币 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "coin"))
                + "｜收藏 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "favorite"))
                + "｜弹幕 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "danmaku"))
                + "｜评论 " + HumanCount(BiliJson.Number(BiliJson.Pick(item, "stat"), "reply")),
            "简介：" + (BiliJson.Text(item, "desc").Length == 0 ? "（空）" : SingleLine(BiliJson.Text(item, "desc"), 160))
        };
        JsonNode? pages = item["pages"];
        if (pages is JsonArray array && array.Count > 0)
        {
            lines.Add("分P 共 " + array.Count + " 话：" + string.Join("、", array.Take(8).Select((page, index) =>
                (index + 1).ToString(CultureInfo.InvariantCulture) + "." + SingleLine(BiliJson.Text(page, "part"), 24)
                + "（cid " + BiliJson.Text(page, "cid") + "）")));
            if (array.Count > 8)
            {
                lines.Add("  …还有 " + (array.Count - 8) + " 话");
            }
            if (array.Count > 1)
            {
                lines.Add("  弹幕与下载直链都是按话（cid）算的，看第几话要在 BiliDanmaku 里用 page 说明");
            }
        }
        return string.Join("\n", lines);
    }
    public static string DescribeCard(JsonNode? item, string pad)
    {
        string bvid = BiliJson.Text(item, "bvid");
        return pad + SingleLine(StripHtml(BiliJson.Text(item, "title")), 46)
            + "\n" + pad + "  " + (bvid.Length > 0 ? bvid : "av" + BiliJson.Text(item, "aid"))
            + "｜" + SingleLine(BiliJson.Text(item, "author").Length > 0 ? BiliJson.Text(item, "author") : BiliJson.Text(item, "name"), 18)
            + "｜播放 " + HumanCount(BiliJson.Number(item, "play"))
            + "｜" + (BiliJson.Text(item, "duration").Length > 0 ? BiliJson.Text(item, "duration") : HumanDuration(BiliJson.Number(item, "duration_new") / 1000))
            + "｜" + HumanDate(BiliJson.Number(item, "pubdate"));
    }
    static string DescribeReply(JsonNode? reply)
    {
        return "  rpid=" + BiliJson.Text(reply, "oid") + "|" + BiliJson.Text(reply, "like") + "赞|" + BiliJson.Text(reply, "rcount") + "回复 "
            + SingleLine(BiliJson.Text(BiliJson.Pick(reply, "member"), "uname"), 14) + "："
            + SingleLine(BiliJson.Text(BiliJson.Pick(reply, "content"), "message"), 120);
    }
    public static string StripHtml(string text) => Regex.Replace(text ?? "", "<[^>]*>", "");
    public static string SingleLine(string text, int max)
    {
        string one = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return one.Length > max ? one[..max] + "…" : one;
    }
    public static string HumanDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "时长未知";
        }
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "小时"
            + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "分" : span.Minutes.ToString(CultureInfo.InvariantCulture) + "分"
            + span.Seconds.ToString("00", CultureInfo.InvariantCulture) + "秒";
    }
    public static string HumanCount(double value)
    {
        if (value >= 10000)
        {
            return (value / 10000).ToString("0.#", CultureInfo.InvariantCulture) + "万";
        }
        return value.ToString("0", CultureInfo.InvariantCulture);
    }
    public static string HumanDate(double unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return "时间未知";
        }
        return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
