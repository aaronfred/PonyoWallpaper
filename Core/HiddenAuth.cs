using System.Security.Cryptography;
using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// 隐藏分类的访问保护。
/// 密码以 SHA256 + 固定盐的哈希形式保存，配置文件中不出现明文。
/// 解锁状态仅在本次进程内有效，程序重启后需重新验证。
/// </summary>
internal static class HiddenAuth
{
    private const string Salt = "PonyoWallpaper.Hidden.v1";

    /// <summary>是否已设置过访问密码（未设置时首次唤出隐藏面板需先设置）。</summary>
    public static bool HasPassword(string? hash) => !string.IsNullOrEmpty(hash);

    /// <summary>本次进程是否已解锁。</summary>
    public static bool Unlocked { get; private set; }

    public static void Unlock() => Unlocked = true;

    public static void Lock() => Unlocked = false;

    public static string Hash(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(Salt + plain);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static bool Verify(string plain, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return string.Equals(Hash(plain), hash, StringComparison.OrdinalIgnoreCase);
    }
}