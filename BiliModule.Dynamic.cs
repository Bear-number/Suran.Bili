using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;

namespace Suran.Bili;

public partial class BiliModule
{
    [XmlFunction(FunctionMode.OneShot)]
    [Description("看 B站动态（关注流，要登录）。category 可填 all/video/opus/ugc，size 条数；翻页把上一次回报里的 offset 传进来")]
    public Task BiliDynamic(string offset = "", int size = 10, string category = "all")
        => DynamicListAsync("all", offset, size, category);
    [XmlFunction(FunctionMode.OneShot)]
    [Description("看某个 UP 主发的动态（mid 是 UID）。size 条数，翻页传 offset")]
    public Task BiliUserDynamic(string mid, string offset = "", int size = 10)
        => DynamicListAsync("space", offset, size, "all", mid);
    [XmlFunction(FunctionMode.OneShot)]
    [Description("看一条动态的完整内容（id 用 BiliDynamic 回报里的 id_str）：正文、所附视频/图片/专栏、被转发的原动态、转赞评数")]
    public async Task BiliDynamicDetail(string id)
    {
        string trimmed = id.Trim();
        if (trimmed.Length == 0)
        {
            throw new Exception("没给动态 id（BiliDynamic 回报里每条都有 id_str）");
        }
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync("https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id=" + Uri.EscapeDataString(trimmed),
            DestroyCancellationToken, "https://www.bilibili.com/opus/");
        JsonNode? item = BiliJson.Pick(data, "data", "item");
        if (item == null)
        {
            interactor.Poke("动态 " + trimmed + " 取到了空内容（可能被删、设为不可见，或 id 不对）");
            return;
        }
        interactor.Poke(DescribeDynamic(item) + "\nhttps://t.bilibili.com/" + trimmed);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("发一条文字动态（写操作，默认关且默认要确认码）。atUids 用逗号分隔可以 @ 人")]
    public Task BiliPostDynamic(string text, string atUids = "") =>
        WriteRawAsync(BiliWrite.Dynamic, "我的动态", "发动态：" + SingleLine(text, 80), async csrf =>
        {
            BiliApi api = OpenApi();
            JsonNode data = await api.PostAsync("https://api.vc.bilibili.com/dynamic_svr/v1/dynamic_svr/create",
                Form(new {
                    dynamic_id = 0, type = 4, rid = 0, content = text,
                    up_choose_comment = 0, up_close_comment = 0, at_uids = atUids,
                    csrf_token = csrf, csrf
                }), DestroyCancellationToken, "https://t.bilibili.com/");
            string idStr = BiliJson.Text(BiliJson.Pick(data, "data"), "id_str");
            if (idStr.Length == 0)
            {
                return "接口回了 code 0 但没给动态 id，去 BiliDynamic 看一眼是不是真发上了，别当成功";
            }
            return "已发布 id_str " + idStr + "（" + BiliJson.Text(BiliJson.Pick(data, "data"), "type") + "）"
                + "\n要撤掉：BiliDeleteDynamic(id=『" + idStr + "』）";
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除自己发的一条动态（写操作）。id 用 BiliDynamic/BiliPostDynamic 回报里的 id_str")]
    public Task BiliDeleteDynamic(string id) =>
        WriteRawAsync(BiliWrite.DynamicDelete, id.Trim(), "删除动态 " + id.Trim(), async csrf =>
        {
            BiliApi api = OpenApi();
            await api.PostAsync("https://api.vc.bilibili.com/dynamic_svr/v1/dynamic_svr/rm_dynamic",
                Form(new { dynamic_id = id.Trim(), csrf_token = csrf, csrf }), DestroyCancellationToken, "https://t.bilibili.com/");
            JsonNode check = await api.GetAsync("https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id=" + Uri.EscapeDataString(id.Trim()),
                DestroyCancellationToken, "https://www.bilibili.com/opus/");
            string stillThere = BiliJson.Text(BiliJson.Pick(check, "data", "item"), "id_str");
            return stillThere == id.Trim()
                ? "接口说删了，但详情还能原样取到（可能有缓存）：再用 BiliUserDynamic 核对一次，别说已经删干净"
                : "详情已取不到，删除生效";
        });
    [XmlFunction(FunctionMode.OneShot)]
    [Description("给某条动态点赞/取消（不是视频点赞，视频用 BiliLike）")]
    public Task BiliLikeDynamic(string id, bool undo = false) =>
        WriteRawAsync(BiliWrite.DynamicLike, id.Trim(), (undo ? "取消赞动态 " : "赞动态 ") + id.Trim(), async csrf =>
        {
            string payload = new JsonObject {
            ["dyn_id_str"] = id.Trim(),
            ["up"] = undo ? 2 : 1,
            ["spmid"] = "333.1369.0.0"
        }.ToJsonString();            BiliApi api = OpenApi();
            JsonNode data = await api.PostJsonAsync("https://api.bilibili.com/x/dynamic/feed/dyn/thumb?csrf=" + Uri.EscapeDataString(csrf),
                payload, DestroyCancellationToken, "https://t.bilibili.com/" + id.Trim());
            return "回 code 0，data=" + (data["data"]?.ToJsonString() ?? "无") + "；这条 up=1|2 是设定状态不是切换，取消要再点一次 up=2";
        });

    async Task DynamicListAsync(string mode, string offset, int size, string category, string mid = "")
    {
        int take = Math.Clamp(size, 1, 20);
        string query = "timezone_id=Asia/Shanghai"
            + (mode == "space"
                ? "&host_mid=" + Uri.EscapeDataString(mid.Trim())
                : "&page=1&category=" + Uri.EscapeDataString(category.Trim().Length > 0 ? category.Trim() : "all"));
        if (offset.Trim().Length > 0)
        {
            query += "&offset=" + Uri.EscapeDataString(offset.Trim());
        }
        string url = "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/" + (mode == "space" ? "space" : "all") + "?" + query;
        BiliApi api = OpenApi();
        JsonNode data = await api.GetAsync(url, DestroyCancellationToken, "https://www.bilibili.com/");
        List<JsonNode?> items = ((BiliJson.Pick(data, "data", "items")?.AsArray()) ?? new JsonArray()).ToList();
        if (items.Count == 0)
        {
            interactor.Poke(mode == "space"
                ? "UID " + mid + " 没取到动态（没发过、设了可见范围，或 mid 不对）"
                : "动态流是空的（登录账号没关注任何人？）");
            return;
        }
        StringBuilder text = new((mode == "space" ? "该 UP 主" : "B站动态（" + (category.Trim().Length > 0 ? category : "all") + "）")
            + "最近 " + Math.Min(items.Count, take) + " 条" + (BiliJson.Flag(data["data"], "has_more") ? "，后面还有" : "，到底了") + "：");
        foreach (JsonNode? item in items.Take(take))
        {
            text.Append("\n").Append(DescribeDynamic(item));
        }
        string next = BiliJson.Text(data["data"], "offset");
        text.Append(next.Length > 0 ? "\n下一页：把 offset=\"" + next + "\" 传回同一个函数" : "\n（这次接口没给下一页游标）");
        interactor.Poke(text.ToString());
    }
    /// <summary>一条动态压成几行：作者·时间·类型·正文或所指内容·转赞评</summary>
    static string CountOf(JsonNode? stat, string name)
    {
        string value = BiliJson.Text(stat, name);
        return value.Length == 0 ? "0" : HumanCount(double.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
    }
    static string DescribeDynamic(JsonNode? item)
    {
        string idStr = BiliJson.Text(item, "id_str");
        string type = BiliJson.Text(item, "type").Replace("DYNAMIC_TYPE_", "");
        JsonNode? author = BiliJson.Pick(item, "modules", "module_author");
        JsonNode? stat = BiliJson.Pick(item, "modules", "module_stat");
        JsonNode? dyn = BiliJson.Pick(item, "modules", "module_dynamic");
        List<string> lines = new() {
            "· " + BiliJson.Text(author, "name") + " ｜" + TimeText(author) + " ｜" + DynamicTypeLabel(type)
                + " ｜id_str " + idStr
        };
        string body = SingleLine(BiliJson.Text(BiliJson.Pick(dyn, "desc"), "text"), 200);
        if (body.Length > 0)
        {
            lines.Add("    " + body);
        }
        string attached = DescribeMajor(BiliJson.Pick(dyn, "major"));
        if (attached.Length > 0)
        {
            lines.Add("    " + attached);
        }
        JsonNode? orig = item?["orig"];
        if (orig != null)
        {
            lines.Add(SingleLine("    原：" + BiliJson.Text(BiliJson.Pick(orig, "user"), "name") + "："
                + BiliJson.Text(BiliJson.Pick(orig, "content"), "description"), 160));
            string origAttached = DescribeMajor(BiliJson.Pick(orig, "major"));
            if (origAttached.Length > 0)
            {
                lines.Add("      " + origAttached);
            }
        }
        lines.Add("    转发 " + CountOf(stat, "forward") + " ｜评论 " + CountOf(stat, "comment")
            + " ｜点赞 " + CountOf(stat, "like") + " ｜https://t.bilibili.com/" + idStr);
        return string.Join("\n", lines);
    }
    static string TimeText(JsonNode? author)
    {
        string pub = BiliJson.Text(author, "pub_time");
        if (pub.Length > 0 && long.TryParse(pub, out long ts) && ts > 0)
        {
            return HumanDate(ts);
        }
        string pure = BiliJson.Text(author, "pub_ts");
        if (pure.Length > 0 && long.TryParse(pure, out long ts2) && ts2 > 0)
        {
            return HumanDate(ts2);
        }
        // 实测 pub_time 常常直接是"10月19日"这种人话，没有数字时间就把它原样用掉
        if (pub.Length > 0)
        {
            return pub;
        }
        string stamp = BiliJson.Text(author, "pub_time_name");
        return stamp.Length > 0 ? stamp : "时间未知";
    }
    static string DescribeMajor(JsonNode? major)
    {
        string kind = BiliJson.Text(major, "type");
        return kind switch {
            "ARCHIVE" or "UGC_SEASON" => "视频：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "archive"), "title"), 90)
                + "（" + BiliJson.Text(BiliJson.Pick(major, "archive"), "bvid") + "，" + BiliJson.Text(BiliJson.Pick(major, "archive"), "duration_text") + "）",
            "DRAW" => "图文：" + (BiliJson.Pick(major, "draw", "items")?.AsArray()?.Count ?? 0) + " 张图",
            "ARTICLE" => "专栏：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "article"), "title"), 90)
                + "（" + BiliJson.Text(BiliJson.Pick(major, "article"), "id") + " 字 " + BiliJson.Text(BiliJson.Pick(major, "article"), "desc") + "）",
            "OPUS" => "长文：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "opus"), "summary"), 90),
            "MUSIC" => "音乐：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "music"), "title"), 80),
            "PGC" => "番剧/影视：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "pgc"), "title"), 80),
            "LIVE" or "LIVE_RCMD" => "直播：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "live"), "title"), 80)
                + "（房间 " + BiliJson.Text(BiliJson.Pick(major, "live"), "roomid") + "）",
            "COMMON" => "分享：" + SingleLine(BiliJson.Text(BiliJson.Pick(major, "common"), "title"), 90)
                + " ← " + BiliJson.Text(BiliJson.Pick(major, "common"), "jump_url"),
            _ => ""
        };
    }
    static string DynamicTypeLabel(string raw)
    {
        return raw switch {
            "" => "动态",
            "VIDEO" or "UGC_SEASON" or "ARCHIVE" => "视频",
            "DRAW" => "图文",
            "ARTICLE" => "专栏",
            "OPUS" => "长文",
            "FORWARD" => "转发",
            "LIVE" or "LIVE_RCMD" => "直播",
            "MUSIC" => "音乐",
            "PGC" => "番剧影视",
            "WARP" => "文章",
            "FANS_ACTIVE" => "粉丝互动",
            _ => raw.ToLowerInvariant()
        };
    }
}
