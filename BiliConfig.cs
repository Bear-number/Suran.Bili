using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Suran.Bili;

public class BiliConfig
{
    [DisplayName("开关·允许写操作")]
    [Description("是否允许点赞、投币、收藏等写操作，默认关。")]
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
    [Description("HTTP 代理地址，留空直连。")]
    public string ProxyUrl { get; set; } = "";

    [DisplayName("请求超时（秒）")]
    [Description("单次请求超时秒数，低于 10 秒容易偶发失败。")]
    public int RequestTimeoutSeconds { get; set; } = 20;

    [DisplayName("请求最小间隔（毫秒）")]
    [Description("两次请求的最小间隔毫秒数，默认 350。")]
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
    [Description("搜索排序方式，留空为综合。")]
    public string SearchOrder { get; set; } = "";
}
