using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// 城域网认证门户“自动登录”（实验性）：
///  1. 拉取门户页面（跟随重定向 / 自动识别 iframe 内嵌登录页）
///  2. 解析 HTML 表单：用户名输入框（text/tel/email/number）、密码输入框（password）、隐藏字段原样保留
///  3. 按表单 method/action 以 urlencoded 方式提交账号密码（不执行任何 JS —— 纯 JS 渲染的门户无法支持，会明确返回原因）
///  4. 提交后用 NCSI 探测复核真实连通性（唯一可信判据）
/// 安全说明：账号密码仅从本机 config.json 读取；日志与返回消息不含密码。
/// 门户形态千差万别，本助手是“尽力而为”：失败一律回退为打开浏览器人工登录。
/// </summary>
public static class PortalAutoLoginHelper
{
    public sealed record AutoLoginResult(bool Submitted, bool VerifiedOnline, string Message);

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string NcsiExpected = "Microsoft Connect Test";

    public static async Task<AutoLoginResult> TryLoginAsync(string portalUrl, string username, string password, CancellationToken ct)
    {
        try
        {
            // 1) 拉取门户页面
            using var req = new HttpRequestMessage(HttpMethod.Get, portalUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) QingShuToolkit");
            using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var baseUrl = resp.RequestMessage?.RequestUri?.ToString() ?? portalUrl;

            // 2) 解析表单（主页面没有密码框时，尝试第一个 iframe）
            var form = ParseForm(html);
            if (form.PasswordField.Length == 0)
            {
                var iframe = Regex.Match(html, @"<iframe\b[^>]*src\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase);
                if (iframe.Success)
                {
                    var iframeUrl = ResolveUrl(baseUrl, iframe.Groups[1].Value);
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                    using var resp2 = await Client.SendAsync(req2, ct).ConfigureAwait(false);
                    var html2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var form2 = ParseForm(html2);
                    if (form2.PasswordField.Length > 0) form = form2;
                    baseUrl = resp2.RequestMessage?.RequestUri?.ToString() ?? iframeUrl;
                }
            }

            if (form.PasswordField.Length == 0 || form.UserField.Length == 0)
                return new AutoLoginResult(false, false,
                    "自动登录失败：门户页面未找到标准登录表单（可能为纯 JS 渲染或扫码登录），已回退为浏览器人工登录。");

            // 3) 组装并提交
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var (name, value) in form.Hidden)
                fields.Add(new KeyValuePair<string, string>(name, value));
            fields.Add(new KeyValuePair<string, string>(form.UserField, username));
            fields.Add(new KeyValuePair<string, string>(form.PasswordField, password));
            // 部分门户要求 submit 按钮名值对，补一个通用值
            if (form.SubmitField.Length > 0)
                fields.Add(new KeyValuePair<string, string>(form.SubmitField, form.SubmitValue));

            var actionUrl = ResolveUrl(baseUrl, form.Action.Length > 0 ? form.Action : baseUrl);
            HttpResponseMessage post;
            if (string.Equals(form.Method, "get", StringComparison.OrdinalIgnoreCase))
            {
                var query = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
                post = await Client.GetAsync(actionUrl + (actionUrl.Contains('?') ? "&" : "?") + query, ct).ConfigureAwait(false);
            }
            else
            {
                using var content = new FormUrlEncodedContent(fields);
                post = await Client.PostAsync(actionUrl, content, ct).ConfigureAwait(false);
            }
            _ = await post.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 4) NCSI 复核（唯一可信判据：真的能拿到预期内容才算登录成功）
            await Task.Delay(800, ct).ConfigureAwait(false); // 给门户服务端一点入账时间
            var verify = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false, readBody: true).ConfigureAwait(false);
            if (verify.Ok && verify.StatusCode == 200 && verify.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
                return new AutoLoginResult(true, true, $"自动登录成功：已提交认证并通过连通性复核（{verify.ElapsedMs} ms）。");
            return new AutoLoginResult(true, false,
                $"自动登录已提交（门户响应 HTTP {(int)post.StatusCode}），但连通性复核未通过：账号可能输错、已在线别处登录或门户需要验证码。已回退为浏览器人工登录。");
        }
        catch (OperationCanceledException)
        {
            return new AutoLoginResult(false, false, "自动登录超时，已回退为浏览器人工登录。");
        }
        catch (Exception ex)
        {
            return new AutoLoginResult(false, false, $"自动登录异常（{ex.Message}），已回退为浏览器人工登录。");
        }
    }

    private sealed record PortalForm(string Action, string Method, string UserField, string PasswordField,
        string SubmitField, string SubmitValue, List<(string Name, string Value)> Hidden);

    private static PortalForm ParseForm(string html)
    {
        var formTag = Regex.Match(html, @"<form\b[^>]*>", RegexOptions.IgnoreCase);
        string action = "", method = "post";
        if (formTag.Success)
        {
            action = Attr(formTag.Value, "action");
            var m = Attr(formTag.Value, "method");
            if (m.Length > 0) method = m;
        }

        string user = "", pass = "", submit = "", submitValue = "";
        var hidden = new List<(string, string)>();
        foreach (Match im in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = im.Value;
            var name = Attr(tag, "name");
            var type = Attr(tag, "type").ToLowerInvariant();
            var value = Attr(tag, "value");
            if (name.Length == 0) continue;
            switch (type)
            {
                case "password" when pass.Length == 0: pass = name; break;
                case "hidden": hidden.Add((name, value)); break;
                case "submit" when submit.Length == 0: submit = name; submitValue = value; break;
                case "text" or "tel" or "email" or "number" or "" or null:
                    if (user.Length == 0) user = name; // 兜底：第一个文本框
                    break;
            }
            // 用户名字段名启发式优先
            if (user.Length > 0 && user != name &&
                Regex.IsMatch(name, @"user|account|uname|loginname|login_name|uid|auth", RegexOptions.IgnoreCase))
                user = name;
        }
        return new PortalForm(action, method, user, pass, submit, submitValue, hidden);
    }

    private static string Attr(string tag, string attr)
    {
        var m = Regex.Match(tag, $@"{attr}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(tag, $@"{attr}\s*=\s*([^\s""'>]+)", RegexOptions.IgnoreCase); // 无引号写法
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string ResolveUrl(string baseUrl, string url)
    {
        if (url.Length == 0) return baseUrl;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        try { return new Uri(new Uri(baseUrl), url).ToString(); }
        catch { return url; }
    }
}
