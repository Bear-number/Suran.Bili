using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;

namespace Suran.Bili;

public partial class BiliModule
{
    // ---- 登录态 ----

    [XmlFunction(FunctionMode.OneShot)]
    [Description("B站扫码登录。action=generate 申请登录链接（把这链接只发给主人本人，别发到群里——拿到它就等于登他账号）；"
        + "action=poll 带 qrcodeKey 查扫码进度，成功后 cookie 只存本机；action=check 复查现在还有效吗；action=logout 清掉本机 cookie")]
    public async Task BiliLogin(string action = "check", string qrcodeKey = "")
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "generate":
                await QrGenerateAsync();
                return;
            case "poll":
                await QrPollAsync(qrcodeKey);
                return;
            case "logout":
                BiliAuth logout = Auth;
                logout.Clear();
                interactor.Poke("本机已存的 B站 cookie 已删除（文件 " + logout.FilePath + "）。免登录的查询照常能用");
                return;
            default:
                interactor.Poke("登录状态：" + await ProbeNavAsync() + "\ncookie 文件：" + Auth.FilePath
                    + "（" + (Auth.IsLoaded ? Auth.Count + " 项，字段名 " + string.Join(",", Auth.Names.OrderBy(n => n)) : "还没有") + "）"
                    + "\n要重新登录就 BiliLogin(action=generate)");
                return;
        }
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("我是谁：登录账号的昵称、UID、等级、大会员状态、硬币数。没登录会照实说")]
    public async Task BiliMe()
    {
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync("https://api.bilibili.com/x/web-interface/nav", DestroyCancellationToken);
        JsonNode? me = data["data"];
        if (!BiliJson.Flag(me, "isLogin"))
        {
            interactor.Poke("现在没登录（B站回" + BiliJson.Text(me, "message") + "）。要登录先 BiliLogin(action=generate)");
            return;
        }
        interactor.Poke(string.Join("\n", new[] {
            BiliJson.Text(me, "uname") + "｜UID " + BiliJson.Text(me, "mid") + "｜Lv" + BiliJson.Text(BiliJson.Pick(me, "level_info"), "current_level")
                + "｜硬币 " + BiliJson.Text(me, "money") + " 个",
            BiliJson.Flag(BiliJson.Pick(me, "vip"), "vipStatus") ? "大会员：" + BiliJson.Text(BiliJson.Pick(me, "vip"), "vip_label") : "不是大会员",
            "PIN：" + (BiliJson.Text(me, "pin").Length > 0 ? "已充电/舰长之类的标识有" : "无")
        }));
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("某个 UID 的收藏夹列表（公开的不用登录）。返回的 id 拿去给 BiliFavItems")]
    public async Task BiliFavorites(string mid = "", int? take = null)
    {
        long uid = mid.Trim().Length > 0 ? long.Parse(mid.Trim()) : (long)await OwnMidAsync();
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync("https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid="
            + uid.ToString(CultureInfo.InvariantCulture), DestroyCancellationToken);
        List<JsonNode?> list = ((data["data"]?["list"]?.AsArray()) ?? new JsonArray()).ToList();
        if (list.Count == 0)
        {
            interactor.Poke("UID " + uid.ToString(CultureInfo.InvariantCulture) + " 没有可见的收藏夹（要么是空的，要么设了私密——私密的接口不会列出来）");
            return;
        }
        StringBuilder text = new("UID " + uid.ToString(CultureInfo.InvariantCulture) + " 的收藏夹（" + list.Count + " 个）：");
        foreach (JsonNode? folder in list.Take(Take(take)))
        {
            text.Append("\n  id=").Append(BiliJson.Text(folder, "id")).Append(" ").Append(SingleLine(BiliJson.Text(folder, "name"), 30))
                .Append("｜").Append(BiliJson.Text(folder, "media_count")).Append(" 个")
                .Append(BiliJson.Text(folder, "intro").Contains("该收藏夹是秘密的") ? "（私密）" : "");
        }
        text.Append("\n取里面内容：BiliFavItems(mediaId=那个 id)");
        interactor.Poke(text.ToString());
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("某个收藏夹里的视频（私密的要登录后才取得到）")]
    public async Task BiliFavItems(long mediaId, int page = 1, int? take = null)
    {
        string url = "https://api.bilibili.com/x/v3/fav/resource/list?media_id=" + mediaId.ToString(CultureInfo.InvariantCulture)
            + "&pn=" + Math.Clamp(page, 1, 200).ToString(CultureInfo.InvariantCulture) + "&ps=" + ItemCount + "&keyword=&order=mtime&type=0&tid=0&platform=web";
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken);
        List<JsonNode?> list = ((BiliJson.Pick(data, "data", "medias")?.AsArray()) ?? new JsonArray()).ToList();
        if (list.Count == 0)
        {
            interactor.Poke("收藏夹 " + mediaId.ToString(CultureInfo.InvariantCulture) + " 里取不到条目（空、私密且没登录，或 id 不对）");
            return;
        }
        StringBuilder text = new("收藏夹 " + mediaId.ToString(CultureInfo.InvariantCulture) + " 第 " + page + " 页（" + list.Count + " 条）：");
        foreach (JsonNode? item in list.Take(Take(take)))
        {
            text.Append("\n  ").Append(SingleLine(StripHtml(BiliJson.Text(item, "title")), 44))
                .Append("\n      ").Append(BiliJson.Text(item, "bvid")).Append("｜UP ").Append(BiliJson.Text(BiliJson.Pick(item, "upper"), "name"))
                .Append("｜收藏于 ").Append(HumanDate(BiliJson.Number(item, "fav_time")));
        }
        interactor.Poke(text.ToString());
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("我的稍后再看列表（要登录）")]
    public async Task BiliWatchLater(int page = 1, int? take = null)
    {
        string url = "https://api.bilibili.com/x/collection/watching/later?pn=" + Math.Clamp(page, 1, 200).ToString(CultureInfo.InvariantCulture)
            + "&ps=" + ItemCount + "&platform=web";
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken, "https://www.bilibili.com/watchlater/");
        List<JsonNode?> list = ((data["data"]?.AsArray()) ?? new JsonArray()).ToList();
        if (list.Count == 0)
        {
            interactor.Poke("稍后再看是空的（或这个接口换了形状）");
            return;
        }
        StringBuilder text = new("稍后再看第 " + page + " 页（" + list.Count + " 条）：");
        foreach (JsonNode? item in list.Take(Take(take)))
        {
            text.Append("\n").Append(DescribeCard(item, "  "));
        }
        interactor.Poke(text.ToString());
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("我的观看历史（要登录）。想按标题找一个看过的视频就用 keyword")]
    public async Task BiliHistory(int size = 20, string keyword = "")
    {
        string url = "https://api.bilibili.com/x/web-interface/history/cursor?max=0&business=archive&arclink=archive&voice_join_id=&size="
            + Math.Clamp(size, 1, 100).ToString(CultureInfo.InvariantCulture);
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken);
        List<JsonNode?> list = ((BiliJson.Pick(data, "data", "list")?.AsArray()) ?? new JsonArray()).ToList();
        if (keyword.Trim().Length > 0)
        {
            list = list.Where(item => StripHtml(BiliJson.Text(BiliJson.Pick(item, "business"), "title")).Length > 0
                ? StripHtml(BiliJson.Text(BiliJson.Pick(item, "business"), "title")).Contains(keyword, StringComparison.OrdinalIgnoreCase)
                : StripHtml(BiliJson.Text(item, "title")).Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (list.Count == 0)
        {
            interactor.Poke("历史记录里" + (keyword.Trim().Length > 0 ? "没有提到「" + keyword + "」的" : "是空的") + "（要登录才有，且 B站只留最近的一段）");
            return;
        }
        StringBuilder text = new("观看历史" + (keyword.Trim().Length > 0 ? "里匹配「" + keyword + "」" : "") + "（" + list.Count + " 条）：");
        foreach (JsonNode? item in list.Take(ItemCount))
        {
            JsonNode? business = BiliJson.Pick(item, "business") ?? item;
            text.Append("\n  ").Append(SingleLine(StripHtml(BiliJson.Text(business, "title")), 40))
                .Append("\n      ").Append(BiliJson.Text(business, "bvid")).Append("｜看到 ").Append(BiliJson.Text(item, "part") ?? "整条")
                .Append(" 话｜").Append(HumanDate(BiliJson.Number(item, "view_at")));
        }
        interactor.Poke(text.ToString());
    }
    [XmlFunction(FunctionMode.OneShot)]
    [Description("某 UID 的关注列表（要登录，且只能看自己的或对方的公开设置）")]
    public async Task BiliFollowing(string mid = "", int page = 1, int? take = null)
    {
        long uid = mid.Trim().Length > 0 ? long.Parse(mid.Trim()) : (long)await OwnMidAsync();
        string url = "https://api.bilibili.com/x/relation/followings?vmid=" + uid.ToString(CultureInfo.InvariantCulture)
            + "&pn=" + Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture) + "&ps=" + ItemCount + "&order=follow";
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken, "https://space.bilibili.com/" + uid.ToString(CultureInfo.InvariantCulture) + "/relation/follow");
        List<JsonNode?> list = ((BiliJson.Pick(data, "data", "list")?.AsArray()) ?? new JsonArray()).ToList();
        if (list.Count == 0)
        {
            interactor.Poke("取不到关注列表（code 0 但 list 为空：多半是对方把关注设了私密，或没登录）");
            return;
        }
        StringBuilder text = new("UID " + uid.ToString(CultureInfo.InvariantCulture) + " 的关注第 " + page + " 页（共 "
            + BiliJson.Text(BiliJson.Pick(data, "data"), "total") + " 人，这页 " + list.Count + " 人）：");
        foreach (JsonNode? item in list.Take(Take(take)))
        {
            text.Append("\n  ").Append(BiliJson.Text(item, "mid")).Append(" ").Append(SingleLine(BiliJson.Text(item, "name"), 20))
                .Append("｜Lv").Append(BiliJson.Text(BiliJson.Pick(item, "level_info"), "current_level"));
        }
        interactor.Poke(text.ToString());
    }

    // ---- 写操作 ----

    [XmlFunction(FunctionMode.OneShot)]
    [Description("点赞（undo=true 取消赞）。会回查一次视频统计，看点赞数有没有真的变")]
    public Task BiliLike(string target, bool undo = false) =>
        WriteAsync(BiliWrite.Like, target, undo ? "取消点赞" : "点赞", "", async (aid, bvid, csrf) =>
        {
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.bilibili.com/x/web-interface/archive/" + (undo ? "like/cancel" : "like"),
                Form(new { aid, bvid, csrf }), DestroyCancellationToken);
            return await ReadBackLikeAsync(bvid, aid);
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("投币，amount 1 或 2（每天 2 币额度由 B站自己管）；alsoLike=true 顺带点赞")]
    public Task BiliCoin(string target, int amount = 1, bool alsoLike = false) =>
        WriteAsync(BiliWrite.Coin, target, "投 " + Math.Clamp(amount, 1, 2).ToString(CultureInfo.InvariantCulture) + " 币", "", async (aid, bvid, csrf) =>
        {
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.bilibili.com/x/web-interface/coin",
                Form(new { aid, bvid, multiply = Math.Clamp(amount, 1, 2), select_like = alsoLike ? 1 : 0, csrf }), DestroyCancellationToken);
            return await ReadBackLikeAsync(bvid, aid);
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("收藏到指定收藏夹（folderId 从 BiliFavorites 拿；不传就丢进默认收藏夹）")]
    public Task BiliFavorite(string target, string folderId = "", bool undo = false) =>
        WriteAsync(BiliWrite.Favorite, target, (undo ? "取消收藏" : "收藏") + (folderId.Trim().Length > 0 ? "到收藏夹 " + folderId : "到默认收藏夹"), "",
        async (aid, bvid, csrf) =>
        {
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.bilibili.com/x/v3/fav/resource/deal",
                Form(new {
                    rid = aid, biz_type = 2,
                    add_media_ids = undo ? "" : (folderId.Trim().Length > 0 ? folderId.Trim() : "0"),
                    del_media_ids = undo ? (folderId.Trim().Length > 0 ? folderId.Trim() : "0") : "",
                    csrf
                }), DestroyCancellationToken);
            return await ReadBackLikeAsync(bvid, aid);
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("关注/取消关注某个 UID（undo=true 取关）")]
    public Task BiliFollow(string mid, bool undo = false) =>
        WriteRawAsync(BiliWrite.Follow, "UID " + mid, undo ? "取消关注" : "关注", async csrf =>
        {
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.bilibili.com/x/relation/modify",
                Form(new { fid = mid.Trim(), act = undo ? 2 : 1, re_base = 0, device = 1, csrf }),
                DestroyCancellationToken, "https://space.bilibili.com/");
            JsonNode stat = await api.GetAsync("https://api.bilibili.com/x/relation/stat?vmid=" + mid.Trim(), DestroyCancellationToken);
            return "对方粉丝数现在 " + BiliJson.Text(stat["data"], "follower") + "；注意这条接口本机未实机验证，真效果以 BiliUser 复查为准";
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("发评论。root 传楼层 rpid 就是回复某条评论")]
    public Task BiliReply(string target, string text, long root = 0) =>
        WriteRawAsync(BiliWrite.Reply, target, "回复：" + SingleLine(text, 60), async csrf =>
        {
            long aid = await ResolveAidAsync(target);
            BiliApi api = OpenApi();
            JsonNode data = await api.PostAsync("https://api.bilibili.com/x/v2/reply/add",
                Form(new {
                    oid = aid, type = 1, message = text,
                    root = root == 0 ? null : root.ToString(CultureInfo.InvariantCulture),
                    plat = 1, csrf
                }), DestroyCancellationToken, "https://www.bilibili.com/");
            return "接口回 code 0，楼号 rpid " + BiliJson.Text(BiliJson.Pick(data, "data"), "rpid") + "；"
                + "要确认发出去了就 BiliReplies(target) 看最前面有没有";
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("发弹幕（要 cid：BiliVideo 的分P 那行有）。progress 是视频内秒数")]
    public Task BiliSendDanmaku(string target, string text, double progress = 0, long cid = 0) =>
        WriteRawAsync(BiliWrite.Danmaku, target, "弹幕：" + SingleLine(text, 30) + " @ " + TimeSpan.FromSeconds(Math.Max(0, progress)).ToString(@"mm\:ss"), async csrf =>
        {
            JsonNode data = await VideoNodeAsync(target);
            long oid = cid != 0 ? cid : PageCid(data, 1);
            if (oid == 0)
            {
                throw new Exception("取不到 cid，弹幕要按话发。BiliVideo 看一下分P 那行的 cid，或者直接传 cid=");
            }
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.bilibili.com/x/v2/dm/web/send",
                Form(new {
                    oid, bvid = BiliJson.Text(data["data"], "bvid"), msg = text,
                    progress = (long)(progress * 1000), ctime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    mode = 1, fontsize = 25, color = 16777215, robot = 0, bundle="", csrf
                }), DestroyCancellationToken, "https://www.bilibili.com/");
            return "接口收了；这条本机没实机验证过，去 BiliDanmaku(target, at=" + Math.Round(progress).ToString(CultureInfo.InvariantCulture)
                + ") 回查看到才算发上";
        });

    [XmlFunction(FunctionMode.OneShot)]
    [Description("用确认码执行登记过的写动作")]
    public async Task BiliConfirm(string code)
    {
        BiliPending item = Pendings.Take(code, DateTime.Now);
        switch (item.Write)
        {
            case BiliWrite.Like:
                await BiliLike(item.Target);
                return;
            case BiliWrite.Coin:
                await BiliCoin(item.Target, item.Note == "2" ? 2 : 1);
                return;
            case BiliWrite.Favorite:
                await BiliFavorite(item.Target, item.Note ?? "");
                return;
            case BiliWrite.Follow:
                await BiliFollow(item.Target);
                return;
            case BiliWrite.Reply:
                await BiliReply(item.Target, item.Text);
                return;
            case BiliWrite.Danmaku:
                await BiliSendDanmaku(item.Target, item.Text);
                return;
            default:
                throw new Exception(BiliGuard.Label(item.Write) + " 这种写动作还没实现，登记时就不该过闸门");
        }
    }

    async Task QrGenerateAsync()
    {
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(BiliQr.GenerateUrl, DestroyCancellationToken);
        string url = BiliQr.ReadUrl(data["data"]);
        string key = BiliQr.ReadKey(data["data"]);
        if (url.Length == 0 || key.Length == 0)
        {
            throw new Exception("B站没给登录链接（url/qrcode_key 有空）：data=" + data["data"]?.ToJsonString());
        }
        interactor.Poke("登录链接（约 180 秒过期，只发给主人本人，别发到群里或日志里）：\n" + url
            + "\n主人用手机 B站 App 扫完，用 BiliLogin(action=poll, qrcodeKey=\"" + key + "\") 收 cookie");
    }
    async Task QrPollAsync(string qrcodeKey)
    {
        if (qrcodeKey.Trim().Length == 0)
        {
            throw new Exception("要 qrcodeKey（generate 那步给的），不然我查不了进度");
        }
        BiliAuth auth = Auth;
        BiliApi api = OpenApi();
        (string body, string setCookie) = await api.GetWithSetCookieAsync(BiliQr.PollUrl + Uri.EscapeDataString(qrcodeKey.Trim()), DestroyCancellationToken);
        JsonNode data = JsonNode.Parse(body.Trim().StartsWith("{") ? body : throw new BiliException(-1, "轮询接口没回 JSON")) ?? throw new BiliException(-1, "轮询回了个空");
        int state = (int)BiliJson.Number(BiliJson.Pick(data, "data"), "code");
        if (state != 0)
        {
            interactor.Poke("扫码进度：" + BiliQr.StateText(state) + "（data.code=" + state.ToString(CultureInfo.InvariantCulture) + "）"
                + (state == 86038 ? "\n重新 BiliLogin(action=generate) 拿一条新链接" : ""));
            return;
        }
        auth.Merge(BiliAuth.ParseSetCookie(setCookie));
        auth.Merge(BiliAuth.ParseUrlQuery(BiliJson.Text(BiliJson.Pick(data, "data"), "url")));
        auth.Save();
        interactor.Poke("登录成功，cookie 已存本机 " + auth.FilePath + "（字段名：" + string.Join(",", auth.Names.OrderBy(n => n))
            + "，值不外泄）。复验：" + await ProbeNavAsync() + "\n现在能查收藏夹、稍后再看、历史了");
    }
    async Task<double> OwnMidAsync()
    {
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync("https://api.bilibili.com/x/web-interface/nav", DestroyCancellationToken);
        double mid = BiliJson.Number(data["data"], "mid");
        if (mid == 0)
        {
            throw new Exception("没登录就拿不到自己的 UID。这类查询要么先 BiliLogin 扫码，要么把 mid 传进来");
        }
        return mid;
    }
    static Dictionary<string, string> Form(object values)
    {
        Dictionary<string, string> form = new();
        foreach (var property in values.GetType().GetProperties())
        {
            object? value = property.GetValue(values);
            form[property.Name] = value?.ToString() ?? "";
        }
        return form;
    }
    string RequireCsrf()
    {
        BiliAuth auth = Auth;
        if (auth.IsLoaded == false)
        {
            throw new Exception("本机还没存 B站 cookie，写不了。先 BiliLogin(action=generate) 让主人扫码");
        }
        string csrf = auth.Csrf;
        if (csrf.Length == 0)
        {
            throw new Exception("cookie 里没有 bili_jct（csrf），这份登录态做不了写操作。BiliLogin(action=logout) 清掉重新扫一次");
        }
        return csrf;
    }
    /// <summary>写动作的统一流程：开关与额度 → 要不要确认 → 真发 → 记账</summary>
    async Task WriteAsync(BiliWrite write, string target, string describe, string note, Func<long, string, string, Task<string>> send)
    {
        (string bvid, long aid) = BiliApi.ParseTarget(target);
        if (bvid.Length == 0 && aid == 0)
        {
            throw new Exception("看不懂视频目标「" + target + "」");
        }
        long realAid = aid != 0 ? aid : (long)BiliJson.Number((await VideoNodeAsync(target))["data"], "aid");
        await RunGuardAsync(write, target, describe, note, async () =>
            await send(realAid, bvid.Length > 0 ? bvid : "av" + realAid.ToString(CultureInfo.InvariantCulture), RequireCsrf()));
    }
    async Task WriteRawAsync(BiliWrite write, string target, string describe, Func<string, Task<string>> send)
    {
        await RunGuardAsync(write, target, describe, "", () => send(RequireCsrf()));
    }
    async Task RunGuardAsync(BiliWrite write, string target, string describe, string note, Func<Task<string>> action)
    {
        string? blocked = Guard.Check(Configuration, write, DateTime.Now);
        if (blocked != null)
        {
            throw new Exception(blocked);
        }
        if (Configuration.RequireConfirm)
        {
            BiliPending item = Pendings.Add(write, target, describe, note, DateTime.Now);
            interactor.Poke("先不发出。\n" + BiliPendings.Describe(item)
                + "\n主人点头后 BiliConfirm(\"" + item.Code + "\") 才真的动他账号。");
            return;
        }
        await DoWriteAsync(write, target, describe, action);
    }
    async Task DoWriteAsync(BiliWrite write, string target, string describe, Func<Task<string>> action)
    {
        string result = await action();
        Guard.Record(write, DateTime.Now);
        interactor.Poke("已对 " + target + " " + describe + "。\n" + result
            + "\n今天「" + BiliGuard.Label(write) + "」已用 " + Guard.CountOf(write) + " 次（上限 " + Configuration.DailyWriteLimit + "）");
    }
    async Task<string> ReadBackLikeAsync(string bvid, long aid)
    {
        try
        {
            JsonNode data = await VideoNodeAsync(bvid.Length > 0 ? bvid : aid.ToString(CultureInfo.InvariantCulture));
            JsonNode? stat = data["data"]?["stat"];
            return "回查统计：播放 " + HumanCount(BiliJson.Number(stat, "view")) + "、点赞 " + HumanCount(BiliJson.Number(stat, "like"))
                + "、投币 " + HumanCount(BiliJson.Number(stat, "coin")) + "、收藏 " + HumanCount(BiliJson.Number(stat, "favorite"))
                + "。这些数有分钟级延迟，没立刻变不代表没做到";
        }
        catch (BiliException e)
        {
            return "动作发出后回查统计失败（" + e.Message + "），不影响已经发出的那次";
        }
    }
}
