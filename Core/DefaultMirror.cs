using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// 内置默认反代（隐藏资源，可多条）。
///
/// 为什么混淆：这是给所有用户兜底用的公共反代，一旦地址被从源码/二进制里抠出来扩散，
/// 很容易被人当成免费代理池刷、最终被封，全体用户一起受影响。所以：
/// <list type="bullet">
///   <item>地址以「异或 + 倒序 + Base64」形式存放，源码与 exe 里都不出现明文域名；</item>
///   <item>任何日志、状态栏、托盘提示、设置界面都不得显示该地址，统一用「默认代理」代称。</item>
/// </list>
/// 说明：这不是加密，抓包依然能看到地址 —— 目的只是把「随手抄走」的门槛抬高，
/// 而不是对抗有能力的逆向者。
///
/// v1.5.6：从单地址扩展为<b>多地址</b>（主 + 备用）。同一条 Worker 部署在多个域名上，
/// 某一条被墙/被限流时链路会自动滑到下一跳，不必等用户自己配代理。
/// </summary>
internal static class DefaultMirror
{
    // 每条：(Part1, Part2, 与 Part1 拼接后构成完整 Base64)
    //
    // 编码方案：明文 → UTF8 → 每字节 ^ 0x5B → 逆序 → Base64；
    // 再按固定位置切成两段分别存放，避免源码里出现单条可完整辨识的字面量。
    private static readonly (string A, string B)[] Parts =
    {
        // 主：wallhaven.kdns.fr
        ("KT11KDU/MHU1Pi06Mz", "c3Oix0dGEoKy8vMw=="),
        // 备：w2.wallhaven.kdns.fr
        ("KT11KDU/MHU1Pi06Mzc3Oix1aS", "x0dGEoKy8vMw=="),
    };

    private const byte Key = 0x5B;

    /// <summary>
    /// 全部内置默认反代地址（不含结尾斜杠），按优先级排列。
    /// 仅允许在链路构造处使用，不要输出到界面/日志。
    /// </summary>
    public static IReadOnlyList<string> Urls { get; } = Parts.Select(p => Decode(p.A, p.B)).ToList();

    /// <summary>首个内置默认反代地址（兼容旧调用点）。</summary>
    public static string Url => Urls[0];

    /// <summary>判断某地址是否属于内置默认反代（用于界面上的"默认代理"代称）。</summary>
    public static bool IsDefault(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var norm = url.Trim().TrimEnd('/');
        foreach (var u in Urls)
            if (string.Equals(norm, u, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Decode(string a, string b)
    {
        var bytes = Convert.FromBase64String(a + b);
        Array.Reverse(bytes);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= Key;
        return Encoding.UTF8.GetString(bytes);
    }
}
