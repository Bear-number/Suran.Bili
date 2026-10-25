using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Suran.Bili;

public class BiliConfig
{
    [DisplayName("开关·允许写操作")]
    [Description("点赞/投币/收藏/关注/发评论/发弹幕/稍后再看都算写操作，会真的改动账号与视频数据，默认关。"
        + "打开后仍受下面「写操作先确认」与每日额度约束；这些写接口本机都还没实机验证过，第一次用建议只放一个动作")]
    public bool EnableWrite { get; set; } = false;

    [DisplayName("写操作先确认")]
    [Description("开：写动作先把「对哪条、做什么、发什么」报给主人，拿到确认码调 BiliConfirm 才真发。关掉就是即刻发出")]
    public bool RequireConfirm { get; set; } = true;

    [DisplayName("每日写操作上限（每种动作）")]
    [Description("点赞、投币、收藏各自单独计。0 = 不限（不建议）。B站自己也有每日上限（投币/点赞数），超了回 code=137")]
    public int DailyWriteLimit { get; set; } = 20;

    [DisplayName("两次写操作最小间隔（秒）")]
    [Description("连点最容易露出机器痕迹，默认 60 秒")]
    public int MinWriteGapSeconds { get; set; } = 60;

    [DisplayName("HTTP代理")]
    [Description("留空直连。B站 web 接口国内直连可用（本机 2026-10-24 实测），只有被地域或网络挡了才需要填，如 http://127.0.0.1:7890")]
    public string ProxyUrl { get; set; } = "";

    [DisplayName("请求超时（秒）")]
    [Description("单次接口超时，实测两三秒回，低于 10 秒容易偶发失败")]
    public int RequestTimeoutSeconds { get; set; } = 20;

    [DisplayName("请求最小间隔（毫秒）")]
    [Description("本机实测：不带 buvid 连打第三次起会被粘滞性 412（要等好几分钟才解）。底座已经自动去领 buvid3/buvid4，"
        + "这里再留一道保险，默认 350 毫秒；被挡得厉害就调大")]
    public int MinRequestGapMs { get; set; } = 350;

    [DisplayName("直播·榜单单次最大条数")]
    [Description("BiliLiveRank/BiliLiveOverview 的上限。B站这个接口超过 50 会被打回 20 条，所以范围就是 1-50")]
    public int MaxCount { get; set; } = 30;

    [DisplayName("默认返回条数")]
    [Description("列表类动作默认取几条（搜索、热门、评论、弹幕统计都吃这个）")]
    public int DefaultItemCount { get; set; } = 10;

    [DisplayName("弹幕最多拉多少条")]
    [Description("弹幕是一次性整段下发（deflate 压缩的 xml），条数只影响统计与截断，不影响请求本身。默认 3000 够用")]
    public int DanmakuScanLimit { get; set; } = 3000;

    [DisplayName("凭据目录")]
    [Description("相对 Alife Storage。扫码登录后的 Cookie 存在这里的 cookies.json，只进不出：插件不提供导出完整 Cookie 的动作")]
    public string CookieFolder { get; set; } = "Bili";

    [DisplayName("默认搜索排序")]
    [Description("可选 totalrank(综合)/click(播放)/stow(收藏)/coins(投币)/dm(弹幕)/newest(最新)。留空=综合；B站对部分排序会回 -352 风控，失败了插件会照实说")]
    public string SearchOrder { get; set; } = "";
}
