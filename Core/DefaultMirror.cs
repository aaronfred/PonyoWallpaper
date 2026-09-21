using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// 内置默认反代（隐藏资源）。
///
/// 为什么混淆：这是给所有用户兜底用的公共反代，一旦地址被从源码/二进制里抠出来扩散，
/// 很容易被人当成免费代理池刷、最终被封，全体用户一起受影响。所以：
/// <list type="bullet">
///   <item>地址以「异或 + 倒序 + Base64」形式存放，源码与 exe 里都不出现明文域名；</item>
///   <item>任何日志、状态栏、托盘提示、设置界面都不得显示该地址，统一用「默认代理」代称；</item>
///   <item>它<b>不参与「优选代理」</b>（见 WallhavenClient.PickBestFromChainAsync 的过滤）。</item>
/// </list>
/// 说明：这不是加密，抓包依然能看到地址 —— 目的只是把「随手抄走」的门槛抬高，
/// 而不是对抗有能力的逆向者。
/// </summary>
internal static class DefaultMirror
{
    // 明文 → UTF8 → 每字节 ^ 0x5B → 逆序 → Base64；拆两段存放，避免单条可辨识字面量
    private const string Part1 = "KT11KDU/MHU1Pi06Mz";
    private const string Part2 = "c3Oix0dGEoKy8vMw==";
    private const byte Key = 0x5B;

    /// <summary>默认反代地址（不含结尾斜杠）。仅允许在链路构造处使用，不要输出到界面/日志。</summary>
    public static string Url { get; } = Decode();

    /// <summary>判断某地址是否就是内置默认反代（用于界面上的"默认代理"代称与优选排除）。</summary>
    public static bool IsDefault(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && string.Equals(url.Trim().TrimEnd('/'), Url, StringComparison.OrdinalIgnoreCase);

    private static string Decode()
    {
        var bytes = Convert.FromBase64String(Part1 + Part2);
        Array.Reverse(bytes);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= Key;
        return Encoding.UTF8.GetString(bytes);
    }
}
