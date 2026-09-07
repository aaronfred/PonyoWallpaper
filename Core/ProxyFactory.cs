using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// 代理处理器工厂。
///
/// 关键点：.NET 的 <see cref="WebProxy"/> 只支持 HTTP 代理，**不支持 SOCKS5**。
/// 此前下拉框选 SOCKS5 只是把 "socks5://host:port" 丢给 WebProxy，它被当成 HTTP 代理使用，
/// 结果必然连不上——这正是「不论选什么协议都报错」的根因。
///
/// 本类对 SOCKS5 改用 SocketsHttpHandler.ConnectCallback：
/// 自行完成 SOCKS5 握手（问候 → 可选用户名密码认证 → CONNECT），
/// 拿到隧道后再把 NetworkStream 交回 HttpClient，之后的 HTTP/1.1 与 TLS 全由 HttpClient 正常处理。
/// HTTP / HTTPS 代理仍走原生 WebProxy。
/// </summary>
internal static class ProxyFactory
{
    /// <summary>
    /// 按代理地址创建处理器。地址为空返回 null（调用方用无代理默认处理器）。
    /// 构造失败会抛出，由调用方捕获后降级直连。
    /// </summary>
    public static HttpMessageHandler? Create(string? proxyUrl, string? user, string? pass)
    {
        var (url, u, p) = WallhavenClient.ParseProxyCredentials(proxyUrl, user, pass);
        if (string.IsNullOrWhiteSpace(url)) return null;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd >= 0 ? url[..schemeEnd].ToLowerInvariant() : "http";
        var hostPort = schemeEnd >= 0 ? url[(schemeEnd + 3)..] : url;
        hostPort = hostPort.TrimStart('/', '\\').TrimEnd('/');
        if (hostPort.Length == 0) return null;

        if (scheme is "socks5" or "socks" or "socks5h")
            return CreateSocks5(hostPort, u, p);

        var web = new WebProxy($"{scheme}://{hostPort}");
        if (!string.IsNullOrWhiteSpace(u))
            web.Credentials = new NetworkCredential(u, p ?? "");
        return new SocketsHttpHandler { Proxy = web, UseProxy = true };
    }

    private static HttpMessageHandler CreateSocks5(string hostPort, string? user, string? pass)
    {
        var (host, port) = SplitHostPort(hostPort, 1080);

        return new SocketsHttpHandler
        {
            // 代理由 ConnectCallback 自行接管，不能让 HttpClient 再套一层 HTTP 代理
            UseProxy = false,
            Proxy = null,
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                    await HandshakeAsync(socket, ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port,
                        user, pass, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    /// <summary>完整 SOCKS5 握手：问候 →（可选）用户名密码认证 → CONNECT。</summary>
    private static async Task HandshakeAsync(Socket socket, string host, int port,
        string? user, string? pass, CancellationToken ct)
    {
        await using var ns = new NetworkStream(socket, ownsSocket: false);

        // ① 问候：声明支持 无认证(0x00) 与 用户名密码(0x02)
        bool hasCred = !string.IsNullOrWhiteSpace(user);
        byte[] greet = hasCred
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };
        await ns.WriteAsync(greet, ct).ConfigureAwait(false);

        var methodBuf = new byte[2];
        await ReadExactAsync(ns, methodBuf, 2, ct).ConfigureAwait(false);
        if (methodBuf[0] != 0x05)
            throw new IOException($"SOCKS5 握手失败：服务端协议版本 0x{methodBuf[0]:X2}（期望 0x05）");

        switch (methodBuf[1])
        {
            case 0x00:
                break; // 无需认证
            case 0x02:
                if (!hasCred) throw new IOException("SOCKS5 代理要求用户名密码，但未填写");
                await WriteAuthAsync(ns, user!, pass ?? "", ct).ConfigureAwait(false);
                break;
            default:
                throw new IOException($"SOCKS5 不支持的认证方式（method=0x{methodBuf[1]:X2}）");
        }

        // ② CONNECT 请求：VER CMD RSV ATYP [ADDR] PORT
        var req = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) { req.Add(0x04); req.AddRange(ip.GetAddressBytes()); }
            else { req.Add(0x01); req.AddRange(ip.GetAddressBytes()); }
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            if (hostBytes.Length is 0 or > 255) throw new IOException($"SOCKS5 目标主机名非法：{host}");
            req.Add(0x03);
            req.Add((byte)hostBytes.Length);
            req.AddRange(hostBytes);
        }
        req.Add((byte)(port >> 8));
        req.Add((byte)(port & 0xFF));
        await ns.WriteAsync(req.ToArray(), ct).ConfigureAwait(false);

        // ③ 响应：VER REP RSV ATYP [ADDR] PORT
        var head = new byte[4];
        await ReadExactAsync(ns, head, 4, ct).ConfigureAwait(false);
        if (head[0] != 0x05) throw new IOException($"SOCKS5 响应版本异常 0x{head[0]:X2}");
        if (head[1] != 0x00)
            throw new IOException($"SOCKS5 CONNECT 被拒绝：{RepText(head[1])}（REP=0x{head[1]:X2}）");

        int rest = head[3] switch
        {
            0x01 => 4 + 2,   // IPv4
            0x04 => 16 + 2,  // IPv6
            _ => -1          // 域名：长度需再读一字节
        };
        if (rest < 0)
        {
            var lenBuf = new byte[1];
            await ReadExactAsync(ns, lenBuf, 1, ct).ConfigureAwait(false);
            rest = lenBuf[0] + 2;
        }
        if (rest > 0)
        {
            var tail = new byte[rest];
            await ReadExactAsync(ns, tail, rest, ct).ConfigureAwait(false);
        }
    }

    /// <summary>SOCKS5 用户名/密码子协商（RFC 1929）。</summary>
    private static async Task WriteAuthAsync(Stream ns, string user, string pass, CancellationToken ct)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(pass);
        var req = new List<byte>(3 + u.Length + p.Length) { 0x01, (byte)u.Length };
        req.AddRange(u);
        req.Add((byte)p.Length);
        req.AddRange(p);
        await ns.WriteAsync(req.ToArray(), ct).ConfigureAwait(false);

        var rsp = new byte[2];
        await ReadExactAsync(ns, rsp, 2, ct).ConfigureAwait(false);
        if (rsp[1] != 0x00) throw new IOException("SOCKS5 用户名或密码错误");
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("SOCKS5 连接被对端提前关闭");
            read += n;
        }
    }

    private static (string Host, int Port) SplitHostPort(string hostPort, int defaultPort)
    {
        var s = hostPort.Trim();
        int port = defaultPort;
        var idx = s.LastIndexOf(':');
        // 排除 IPv6 字面量（含 ']' 之后的冒号才是端口）
        if (idx >= 0 && idx < s.Length - 1 && !s[(idx + 1)..].Contains(']'))
        {
            if (int.TryParse(s[(idx + 1)..], out var parsed)) { port = parsed; s = s[..idx]; }
        }
        return (s.Trim('[', ']'), port);
    }

    private static string RepText(byte rep) => rep switch
    {
        0x01 => "常规故障",
        0x02 => "规则集不允许连接",
        0x03 => "网络不可达",
        0x04 => "主机不可达",
        0x05 => "连接被拒绝",
        0x06 => "TTL 过期",
        0x07 => "命令不支持",
        0x08 => "地址类型不支持",
        _ => "未知错误"
    };
}
