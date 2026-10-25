using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Serein.Rtsp.Server;

/// <summary>
/// RTSP 认证上下文，包含请求中的认证信息。
/// </summary>
public sealed class RtspAuthContext
{
    /// <summary>认证方案（Basic / Digest）。</summary>
    public string Scheme { get; init; } = "";

    /// <summary>用户名。</summary>
    public string Username { get; init; } = "";

    /// <summary>密码（Basic Auth 时可用）。</summary>
    public string? Password { get; init; }

    /// <summary>Digest 响应值。</summary>
    public string? DigestResponse { get; init; }

    /// <summary>服务器提供的摘要认证随机数。</summary>
    public string? Nonce { get; init; }

    /// <summary>当前随机数已使用的请求计数。</summary>
    public string? NonceCount { get; init; }

    /// <summary>客户端生成的摘要认证随机数。</summary>
    public string? Cnonce { get; init; }

    /// <summary>摘要认证协商的保护质量选项。</summary>
    public string? Qop { get; init; }

    /// <summary>参与摘要计算的请求资源地址。</summary>
    public string? DigestUri { get; init; }

    /// <summary>RTSP 请求方法。</summary>
    public string Method { get; init; } = "";

    /// <summary>RTSP 请求 URL。</summary>
    public string Url { get; init; } = "";

    /// <summary>客户端远程端点。</summary>
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>
    /// 使用 Digest 参数验证密码是否正确。
    /// </summary>
    /// <param name="expectedPassword">用于验证认证摘要的密码。</param>
    /// <returns>使用 Digest 参数验证密码是否正确是否成立。</returns>
    public bool ValidateDigestPassword(string expectedPassword)
    {
        if (Nonce == null || NonceCount == null || Cnonce == null || DigestUri == null || DigestResponse == null)
            return false;

        var ha1 = Md5Hex($"{Username}:{DigestRealm}:{expectedPassword}");
        var ha2 = Md5Hex($"{Method}:{DigestUri}");
        var expected = Qop == "auth"
            ? Md5Hex($"{ha1}:{Nonce}:{NonceCount}:{Cnonce}:{Qop}:{ha2}")
            : Md5Hex($"{ha1}:{Nonce}:{ha2}");
        return string.Equals(expected, DigestResponse, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取或设置 DigestRealm。
    /// </summary>
    internal string DigestRealm { get; set; } = "RTSP Server";

    /// <summary>计算摘要认证所需的 MD5 十六进制文本。</summary>
    /// <param name="input">输入数据。</param>
    /// <returns>计算摘要认证所需的 MD5 十六进制文本得到的文本。</returns>
    private static string Md5Hex(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
