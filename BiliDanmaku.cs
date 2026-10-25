using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Suran.Bili;

public sealed record BiliShot(double AtSecond, int Mode, int FontSize, string Color, long SendAt, string Text)
{
    public string Clock()
    {
        return TimeSpan.FromSeconds(AtSecond).ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
    public string Line()
    {
        return "[" + Clock() + "] " + (Mode == 0 || Mode == 1 || Mode == 6 ? "" : Mode == 5 ? "(顶端)" : Mode == 4 ? "(底部)" : "(特殊)") + Text;
    }
}

/// <summary>
/// 弹幕 xml 解析。B站的字段顺序是固定的 p="时间,模式,字号,颜色,池,发送者hash,row,发送秒"，
/// 且整个文件是 deflate 压缩的，交给 HttpClient 自动解压后只剩这一层要处理。
/// </summary>
public static class BiliDanmakuParser
{
    static readonly Regex ShotPattern = new(@"<d\s+p=""([^"")]*)""\s*>(.*?)</d>", RegexOptions.Compiled | RegexOptions.Singleline);

    public static List<BiliShot> Parse(string xml)
    {
        List<BiliShot> shots = new();
        foreach (Match match in ShotPattern.Matches(xml ?? ""))
        {
            string[] parts = match.Groups[1].Value.Split(',');
            if (parts.Length < 4)
            {
                continue;
            }
            double at = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) ? t : 0;
            int mode = int.TryParse(parts[1], out int m) ? m : 1;
            int size = int.TryParse(parts[2], out int s) ? s : 25;
            string color = FormatColor(long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long c) ? c : 16777215);
            long sendAt = parts.Length >= 8 && long.TryParse(parts[7], out long stamp) ? stamp : 0;
            string text = WebUtility.HtmlDecode(match.Groups[2].Value).Replace("\r", "").Replace("\n", " ").Trim();
            if (text.Length > 0)
            {
                shots.Add(new BiliShot(at, mode, size, color, sendAt, text));
            }
        }
        return shots;
    }
    public static string FormatColor(long value)
    {
        return "#" + value.ToString("X6", CultureInfo.InvariantCulture);
    }
    public static List<(string Word, int Count)> TopWords(IEnumerable<BiliShot> shots, int take, int minLength = 2)
    {
        return shots.SelectMany(shot => Regex.Split(shot.Text, @"[\s，。！？、,.!?:：；;…~]+"))
            .Where(word => word.Length >= minLength && !word.All(char.IsDigit))
            .GroupBy(word => word)
            .OrderByDescending(group => group.Count())
            .Take(Math.Clamp(take, 1, 50))
            .Select(group => (group.Key, group.Count()))
            .ToList();
    }
    public static List<BiliShot> Around(IEnumerable<BiliShot> shots, double second, double windowSeconds)
    {
        double span = Math.Clamp(windowSeconds, 0.5, 600);
        return shots.Where(shot => Math.Abs(shot.AtSecond - second) <= span).OrderBy(shot => shot.AtSecond).ToList();
    }
    public static string Summarize(List<BiliShot> shots, int top)
    {
        if (shots.Count == 0)
        {
            return "一条弹幕都没解析出来（可能是这个分P 没有弹幕，或者 xml 结构变了）";
        }
        double last = shots.Max(shot => shot.AtSecond);
        List<string> lines = new() {
            "弹幕 " + shots.Count + " 条，覆盖到 " + TimeSpan.FromSeconds(last).ToString(@"mm\:ss", CultureInfo.InvariantCulture)
                + "，密度 " + (last > 0 ? (shots.Count / last).ToString("0.0", CultureInfo.InvariantCulture) : "-") + " 条/秒"
        };
        lines.Add("颜色分布：" + string.Join("、", shots.GroupBy(shot => shot.Color).OrderByDescending(g => g.Count()).Take(4)
            .Select(g => g.Key + "×" + g.Count())));
        lines.Add("高频词：" + string.Join("、", TopWords(shots, top).Select(pair => pair.Word + "×" + pair.Count)));
        List<(string Bucket, int Count)> buckets = shots
            .GroupBy(shot => (int)(shot.AtSecond / 60))
            .Select(group => (TimeSpan.FromMinutes(group.Key).ToString(@"mm\:ss", CultureInfo.InvariantCulture), group.Count()))
            .OrderByDescending(pair => pair.Item2).Take(3).ToList();
        lines.Add("最密的三分钟：" + (buckets.Count == 0 ? "无" : string.Join("、", buckets.Select(pair => pair.Bucket + " 起 " + pair.Count + " 条"))));
        return string.Join("\n", lines);
    }
}
