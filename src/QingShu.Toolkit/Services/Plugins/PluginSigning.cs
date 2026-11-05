using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>
/// 插件官方签名验证。
/// · 算法：RSA-2048 + SHA-256 + PKCS#1 v1.5（.NET 8 内置，与 tools/plugin-sign.ps1
///   （Windows PowerShell 5.1 即可运行）跨版本互通，无第三方依赖）。
/// · 签名对象：plugin.json 的原始字节（签名工具先把主程序集 SHA-256 写入清单再签名，
///   因此一次验签同时锁定「清单内容」与「主程序集内容」，改任一即验签失败）。
/// · 公钥内置：官方私钥永不出现在本仓库；换钥 = 改此处常量 + 重新发布。
/// </summary>
public static class PluginSigning
{
    /// <summary>官方签名公钥（RSA XML；配套私钥由官方本人离线保管）。</summary>
    private const string OfficialPublicKeyXml =
        "<RSAKeyValue><Modulus>h9PpMvEcxD1b2ltf1VrLf6GD8irmYX+N87Mlu+gw5LOS7yvw1o3IE85Qdq6VM7ZTZRg76IX9LJVMww4ERRjikqsHp8cAUUMRaOOj4RRrMHcQFGfCVLF2flT3kGexwtcTD05lflICf/dc2SAgHB7yEadoXex1MuKToZhJh2hpDVDqepi06RPBidH5RRV42bru+HryJ5bKTovT+weCLWST6zlYNGJmB6Y+ayZMzEZeaa08Sr1dtYstOeoaob9VWCD4JF5B0lh98CsPNEeh3hVGRyuym9V9zx5LlomzZk9tfp1DZcAM3HTceyKrRX7uVF5KD1d55PwDI3ECDF8toE/3BQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    /// <summary>清单文件名 / 签名文件名（SDK 文档与签名脚本共同约定）。</summary>
    public const string ManifestFileName = "plugin.json";
    public const string SignatureFileName = "plugin.sig";

    private static string? _fingerprint;

    /// <summary>官方公钥指纹（SHA-256 前 8 字节，xx:xx 分组；UI 展示供人工比对）。</summary>
    public static string Fingerprint
    {
        get
        {
            if (_fingerprint != null) return _fingerprint;
            try
            {
                using var rsa = RSA.Create();
                rsa.FromXmlString(OfficialPublicKeyXml);
                var param = rsa.ExportParameters(false);
                var n = param.Modulus ?? Array.Empty<byte>();
                var hash = SHA256.HashData(n);
                var seg = new byte[8];
                Array.Copy(hash, seg, 8);
                var sb = new System.Text.StringBuilder();
                foreach (var b in seg) sb.Append(b.ToString("X2")).Append(':');
                _fingerprint = sb.ToString(0, sb.Length - 1);
            }
            catch { _fingerprint = "不可用"; }
            return _fingerprint;
        }
    }

    /// <summary>用内置官方公钥验证「清单字节 ↔ 签名字节」。成功返回 true；失败时给出面向用户的原因。</summary>
    public static bool VerifyManifest(byte[] manifestBytes, byte[] signature, out string error)
    {
        error = "";
        if (manifestBytes.Length == 0) { error = "plugin.json 为空或不可读"; return false; }
        if (signature.Length == 0) { error = "plugin.sig 缺失或为空（插件未经官方签名）"; return false; }
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(OfficialPublicKeyXml);
            bool ok = rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok) error = "签名与本机内置的官方公钥不符（插件非官方签名，或清单/签名文件被改动）";
            return ok;
        }
        catch (FormatException)
        {
            error = "plugin.sig 不是合法的 Base64 签名文件";
            return false;
        }
        catch (Exception ex)
        {
            error = $"验签执行失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>计算文件 SHA-256（十六进制小写，与签名工具写入清单的口径一致）。</summary>
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>从目录读清单 + 签名并验签（不含程序集哈希/版本等派生检查，那些在管理器完成）。</summary>
    public static PluginVerifyResult VerifyFolderCore(string folder)
    {
        string manifestPath = Path.Combine(folder, ManifestFileName);
        string sigPath = Path.Combine(folder, SignatureFileName);
        if (!File.Exists(manifestPath))
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = "缺少 plugin.json" };

        byte[] manifestBytes;
        try { manifestBytes = File.ReadAllBytes(manifestPath); }
        catch (Exception ex)
        {
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = $"plugin.json 不可读：{ex.Message}" };
        }

        PluginManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(manifestBytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = $"plugin.json 解析失败：{ex.Message}" };
        }
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.MainAssembly))
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = "plugin.json 缺少 id 或 mainAssembly 字段" };

        if (!File.Exists(sigPath))
            return new PluginVerifyResult { State = PluginVerifyState.Unsigned, Manifest = manifest, ErrorText = "缺少 plugin.sig（未签名）" };

        byte[] sig;
        try { sig = Convert.FromBase64String(File.ReadAllText(sigPath).Trim()); }
        catch (FormatException)
        {
            return new PluginVerifyResult { State = PluginVerifyState.BadSignature, Manifest = manifest, ErrorText = "plugin.sig 不是合法的 Base64 签名文件" };
        }
        catch (Exception ex)
        {
            return new PluginVerifyResult { State = PluginVerifyState.BadSignature, Manifest = manifest, ErrorText = $"plugin.sig 不可读：{ex.Message}" };
        }

        if (!VerifyManifest(manifestBytes, sig, out string sigError))
            return new PluginVerifyResult { State = PluginVerifyState.BadSignature, Manifest = manifest, ErrorText = sigError };

        return new PluginVerifyResult { State = PluginVerifyState.Ok, Manifest = manifest };
    }
}
