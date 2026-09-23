using System.Text.RegularExpressions;

namespace PonyoWallpaper;

/// <summary>
/// CDN 镜像 / 反代候选展开（v1.5.2）。
///
/// 背景：GitHub 图库这类源「数据在境外、取图走公共 CDN」，只认一条链路时会因网络抖动
/// 整片卡片空白（v1.5.1 实测确实如此）。把同一张图展开成多条<b>等价</b>链路，
/// 逐条快速失败重试即可自愈 —— 这就是「不能直连就加代理或反代」的落地形态。
///
/// 2026-09-23 本机串行实测（8 文件 × 4 链路，全部 8/8 成功，平均耗时）：
///   raw.githubusercontent.com 0.4s ／ ghproxy.net 1.1s ／ cdn.jsdelivr.net 2.0s ／ fastly 2.6s
/// 故候选顺序按实测耗时排列；raw 直连虽然最快，但历史上会被墙，失败会自动降级到后面的反代。
/// </summary>
internal static class CdnMirror
{
    /// <summary>raw.githubusercontent.com/{owner}/{repo}/{branch}/{path...}</summary>
    private static readonly Regex RawRx = new(
        @"^https?://raw\.githubusercontent\.com/(?<repo>[^/]+/[^/]+)/(?<branch>[^/]+)/(?<path>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>任意 jsDelivr 官方域名 /gh/{owner}/{repo}@{branch}/{path...}</summary>
    private static readonly Regex JsDelivrRx = new(
        @"^https?://(?:cdn|fastly|gcore|testingcf)\.jsdelivr\.net/gh/(?<repo>[^/]+/[^/]+)@(?<branch>[^/]+)/(?<path>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>已被公共反代包了一层的地址（剥掉外层再解析，避免无限套娃）。</summary>
    private static readonly Regex WrappedRx = new(
        @"^https?://(?:ghproxy\.net|ghproxy\.cc|gh-proxy\.com|ghfast\.top|gh\.llkk\.cc|hub\.gitmirror\.com|gh\.api\.99988866\.xyz)/(?<inner>https?://.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 返回候选 URL（按优先级）。非 GitHub 系 URL 原样返回单条，调用方逻辑无需分支。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();
        if (!TryParse(url, out var repo, out var branch, out var path)) return new[] { url };
        return Build(repo, branch, path);
    }

    /// <summary>该 URL 是否可展开为多条镜像链路（GitHub raw / jsDelivr / 已被公共反代包裹）。</summary>
    public static bool IsMirrorable(string url)
        => !string.IsNullOrWhiteSpace(url) && TryParse(url, out _, out _, out _);

    private static bool TryParse(string url, out string repo, out string branch, out string path)
    {
        repo = branch = path = "";
        if (string.IsNullOrWhiteSpace(url)) return false;

        // 反代包裹：剥一层后递归（最多一层，够用且防死循环）
        if (WrappedRx.Match(url) is { Success: true } w)
            return TryParse(w.Groups["inner"].Value, out repo, out branch, out path);

        var m = RawRx.Match(url);
        if (!m.Success) m = JsDelivrRx.Match(url);
        if (!m.Success) return false;

        repo = m.Groups["repo"].Value;
        branch = m.Groups["branch"].Value;
        path = Uri.UnescapeDataString(m.Groups["path"].Value);
        return repo.Length > 0 && branch.Length > 0 && path.Length > 0;
    }

    private static IReadOnlyList<string> Build(string repo, string branch, string path)
    {
        var raw = $"https://raw.githubusercontent.com/{repo}/{branch}/{path}";
        // jsDelivr 要求路径逐段转义（否则含空格/特殊字符的仓库路径会 400）
        var enc = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

        var list = new List<string>(5)
        {
            raw,
            $"https://ghproxy.net/{raw}",
            $"https://cdn.jsdelivr.net/gh/{repo}@{branch}/{enc}",
            $"https://fastly.jsdelivr.net/gh/{repo}@{branch}/{enc}",
            $"https://gh-proxy.com/{raw}",
        };
        return list.Distinct(StringComparer.Ordinal).ToList();
    }
}
