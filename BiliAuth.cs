using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Suran.Bili;

/// <summary>
/// 本机保存的 B 站登录 Cookie。原则：只进不出——提供"看有哪些字段名""验证是否还有效""删除"，
/// 但不提供导出完整 Cookie 的动作，避免凭据顺着聊天记录走到别处。
/// </summary>
public sealed class BiliAuth
{
    public static readonly string[] LoginCookieNames = new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid" };
    /// <summary>扫完码后 B站会换发新键名，收的时候按字段名收，别按旧键名硬挑</summary>
    public static readonly string[] IgnoreCookieNames = new[] { "buvid3", "buvid4", "b_nut", "fingerprint", "_uuid", "sid" };

    public static string HeaderOf(IEnumerable<KeyValuePair<string, string>> cookies)
    {
        return string.Join("; ", cookies.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key + "=" + pair.Value));
    }
    /// <summary>Set-Cookie 头与跳转 url 两条路都要收：只收一条就会出「说登录好了但 cookie 是空的」</summary>
    public static Dictionary<string, string> ParseSetCookie(string? setCookieHeader)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string piece in (setCookieHeader ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string pair = piece.Split(';')[0].Trim();
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
        return result;
    }
    public static Dictionary<string, string> ParseUrlQuery(string? url)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (url == null || Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) == false || string.IsNullOrEmpty(parsed.Query))
        {
            return result;
        }
        foreach (string pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return result;
    }
    public static bool LooksLoggedIn(JsonNode? navData)
    {
        return BiliJson.Flag(BiliJson.Pick(navData, "data"), "isLogin");
    }

    readonly string folder;
    readonly string path;
    readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

    public BiliAuth(string storageFolder, string folderName)
    {
        folder = Path.Combine(storageFolder, folderName.Trim('\\', '/'));
        path = Path.Combine(folder, "cookies.json");
        if (File.Exists(path) == false)
        {
            return;
        }
        string raw = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        JsonNode? root = raw.StartsWith("{") ? JsonNode.Parse(raw) : null;
        foreach (KeyValuePair<string, JsonNode?> entry in (root?.AsObject() ?? new JsonObject()))
        {
            if (entry.Key == "savedAt")
            {
                SavedAt = DateTime.TryParse(entry.Value?.ToString(), CultureInfo.InvariantCulture, out DateTime at) ? at : default;
                continue;
            }
            cookies[entry.Key] = entry.Value?.ToString() ?? "";
        }
    }

    public DateTime SavedAt { get; private set; }
    public bool IsLoaded => cookies.Count > 0;
    public string FilePath => path;
    public string FolderPath => folder;
    public int Count => cookies.Count;
    public string Header => HeaderOf(cookies);
    /// <summary>写接口要 csrf，就是 bili_jct；没登录时是空串</summary>
    public string Csrf => cookies.GetValueOrDefault("bili_jct", "");
    public string Uid => cookies.GetValueOrDefault("DedeUserID", "");
    public IEnumerable<string> Names => cookies.Keys;
    public void Merge(IEnumerable<KeyValuePair<string, string>> incoming)
    {
        foreach (KeyValuePair<string, string> pair in incoming)
        {
            if (pair.Value.Length > 0 && IgnoreCookieNames.Contains(pair.Key) == false)
            {
                cookies[pair.Key] = pair.Value;
            }
        }
    }
    public bool HasLoginCookies() => cookies.ContainsKey("SESSDATA") && cookies.ContainsKey("bili_jct") && cookies.ContainsKey("DedeUserID");
    public void Clear()
    {
        cookies.Clear();
        SavedAt = default;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(folder);
        JsonObject root = new();
        foreach ((string key, string value) in cookies)
        {
            root[key] = value;
        }
        root["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        SavedAt = DateTime.Now;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }
}

/// <summary>扫码登录的三步状态（本机实测：外层 code 恒为 0，状态在 data.code）</summary>
public static class BiliQr
{
    public const string GenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    public const string PollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=";

    public static string StateText(int code)
    {
        return code switch {
            0 => "已确认，正在收 cookie",
            86101 => "还没人扫码",
            86090 => "扫了，等主人在手机上点确认",
            86038 => "二维码已失效，重新生成一个",
            _ => "未知状态 code=" + code
        };
    }
    /// <summary>登录链接的字段名是 url，不存在 qrcode_url</summary>
    public static string ReadUrl(JsonNode? data) => BiliJson.Text(data, "url");
    public static string ReadKey(JsonNode? data) => BiliJson.Text(data, "qrcode_key");
}
