using System;
using System.Collections.Generic;
using System.Linq;

namespace Suran.Bili;

public enum BiliWrite
{
    Like,
    Coin,
    Favorite,
    Follow,
    Reply,
    Danmaku,
    WatchLater
}

/// <summary>
/// 写操作的闸门：能力开关、每日额度、冷却、确认码。判定与计数全是纯逻辑，
/// 不碰网络，所以能在离线探针里一条条打。
/// </summary>
public sealed class BiliGuard
{
    public static readonly string[] WriteNames = new[] { "like", "coin", "favorite", "follow", "reply", "danmaku", "watchlater" };

    public static BiliWrite Parse(string name)
    {
        return name.Trim().ToLowerInvariant() switch {
            "like" or "赞" or "点赞" => BiliWrite.Like,
            "coin" or "投币" => BiliWrite.Coin,
            "favorite" or "收藏" => BiliWrite.Favorite,
            "follow" or "关注" => BiliWrite.Follow,
            "reply" or "评论" => BiliWrite.Reply,
            "danmaku" or "弹幕" => BiliWrite.Danmaku,
            "watchlater" or "稍后再看" => BiliWrite.WatchLater,
            _ => throw new Exception("不认的写动作「" + name + "」，可选：" + string.Join("/", WriteNames))
        };
    }
    public static string Label(BiliWrite write)
    {
        return write switch {
            BiliWrite.Like => "点赞",
            BiliWrite.Coin => "投币",
            BiliWrite.Favorite => "收藏",
            BiliWrite.Follow => "关注",
            BiliWrite.Reply => "发评论",
            BiliWrite.Danmaku => "发弹幕",
            _ => "稍后再看"
        };
    }
    public static int CooldownLeft(DateTime last, DateTime now, int gapSeconds)
    {
        if (gapSeconds <= 0 || last == default)
        {
            return 0;
        }
        double left = gapSeconds - (now - last).TotalSeconds;
        return left > 0 ? (int)Math.Ceiling(left) : 0;
    }

    DateTime day = DateTime.Today;
    readonly Dictionary<BiliWrite, int> counts = new();
    readonly Dictionary<BiliWrite, DateTime> lastAt = new();

    public int TotalToday => counts.Values.Sum();
    public int CountOf(BiliWrite write) => counts.GetValueOrDefault(write);
    /// <summary>放行返回 null；否则是给 AI 看的中文原因（原因里必须带"怎么办"）</summary>
    public string? Check(BiliConfig config, BiliWrite write, DateTime now)
    {
        RollDay(now);
        if (!config.EnableWrite)
        {
            return "写操作总开关是关的（" + Label(write) + " 属于写操作）。要真动账号数据，得主人先在插件配置里打开「开关·允许写操作」。";
        }
        int limit = Math.Clamp(config.DailyWriteLimit, 0, 100000);
        int used = CountOf(write);
        if (limit > 0 && used >= limit)
        {
            return "「" + Label(write) + "」今天已经做了 " + used + " 次，到每日上限了（改「每日写操作上限」可放宽）。";
        }
        int left = CooldownLeft(lastAt.GetValueOrDefault(write), now, Math.Clamp(config.MinWriteGapSeconds, 0, 3600));
        if (left > 0)
        {
            return "距上一次「" + Label(write) + "」还不够 " + config.MinWriteGapSeconds + " 秒，再等 " + left + " 秒。";
        }
        return null;
    }
    public void Record(BiliWrite write, DateTime now)
    {
        RollDay(now);
        counts[write] = CountOf(write) + 1;
        lastAt[write] = now;
    }

    void RollDay(DateTime now)
    {
        if (now.Date == day)
        {
            return;
        }
        day = now.Date;
        counts.Clear();
        lastAt.Clear();
    }
}

/// <summary>写动作的待确认清单：先把"对哪条、做什么、发什么内容"报给主人，凭码执行</summary>
public sealed record BiliPending(string Code, BiliWrite Write, string Target, string Text, string? Note, DateTime CreatedAt);

public sealed class BiliPendings
{
    public const int MaxLive = 5;
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);

    public static string NewCode(IEnumerable<string> taken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string code = Random.Shared.Next(0x1000, 0x10000).ToString("X4");
            if (!taken.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                return code;
            }
        }
        throw new Exception("确认码发不出来（连续 20 次撞码），先把待确认的几条清掉");
    }
    public static string Describe(BiliPending item)
    {
        return "确认码 " + item.Code + "（" + item.CreatedAt.ToString("HH:mm") + " 登记，十分钟内有效）："
            + BiliGuard.Label(item.Write) + " → " + item.Target
            + (item.Text.Length == 0 ? "" : "，内容：" + item.Text)
            + (item.Note == null ? "" : "，" + item.Note);
    }

    readonly Dictionary<string, BiliPending> items = new(StringComparer.OrdinalIgnoreCase);

    public int CountActive(DateTime now)
    {
        Expire(now);
        return items.Count;
    }
    public IReadOnlyList<BiliPending> Active(DateTime now)
    {
        Expire(now);
        return items.Values.ToList();
    }
    public BiliPending Add(BiliWrite write, string target, string text, string? note, DateTime now)
    {
        Expire(now);
        if (items.Count >= MaxLive)
        {
            throw new Exception("待确认的写动作已经攒了 " + items.Count + " 条，先确认掉或等过期，不然分不清要发哪条");
        }
        BiliPending item = new(NewCode(items.Keys), write, target, text, note, now);
        items[item.Code] = item;
        return item;
    }
    public BiliPending Take(string code, DateTime now)
    {
        Expire(now);
        if (!items.TryGetValue(code.Trim(), out BiliPending? item))
        {
            throw new Exception("确认码「" + code + "」不存在或已过期（十分钟内有效），当前待确认："
                + (items.Count == 0 ? "无" : string.Join("、", items.Keys)));
        }
        items.Remove(item.Code);
        return item;
    }
    void Expire(DateTime now)
    {
        foreach (string code in items.Values.Where(entry => now - entry.CreatedAt > Life).Select(entry => entry.Code).ToList())
        {
            items.Remove(code);
        }
    }
}
