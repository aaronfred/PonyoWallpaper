namespace PonyoWallpaper;

/// <summary>频道定义：categories 三位码 + q 查询参数。</summary>
internal sealed record ChannelDef(string Key, string Name, string Category, string Query);

/// <summary>
/// 内置频道：风景 / 摄影 / 人物 / 动漫 四组。
/// Query 与 wallhaven 搜索语法一致（+tag 必含，-tag 排除）。
/// </summary>
internal static class Channels
{
    public static readonly ChannelDef[] All =
    {
        // 风景 (categories=100)
        new("nature_landscape", "自然风光", "100", "+landscape +nature"),
        new("nature_sea",       "海洋沙滩", "100", "+sea +beach"),
        new("nature_mountain",  "山林瀑布", "100", "+mountains +forest"),
        new("nature_flower",    "花卉植物", "100", "+flowers +plants"),
        new("nature_sunset",    "日落晚霞", "100", "+sunset +sky"),
        new("nature_snow",      "雪景冰川", "100", "+winter +snow"),
        // 摄影 (categories=100)
        new("photo_city",      "城市建筑", "100", "+city +architecture"),
        new("photo_space",     "星空宇宙", "100", "+space +stars"),
        new("photo_minimalism","极简抽象", "100", "+minimalism"),
        new("photo_animals",   "动物生灵", "100", "+animals +wildlife"),
        new("photo_cars",      "汽车机械", "100", "+cars +vehicles"),
        // 人物 (categories=001)
        new("people_portrait", "人像写真", "001", "+portrait +people"),
        new("people_fashion",  "时尚美妆", "001", "+fashion +model"),
        new("people_sports",   "运动竞技", "001", "+sports +athlete"),
        new("people_movies",   "影视剧照", "001", "+movies +actor"),
        new("people_street",   "街头纪实", "001", "+street +photo"),
        new("people_art",      "艺术人体", "001", "+art +people"),
        // 动漫 (categories=010)
        new("anime_girls",     "二次元少女", "010", "+anime +girls"),
        new("anime_shonen",    "热血少年",   "010", "+anime +shonen"),
        new("anime_mecha",     "机甲科幻",   "010", "+mecha +scifi"),
        new("anime_games",     "游戏世界",   "010", "+games +anime"),
        new("anime_scenery",   "动漫风景",   "010", "+anime +scenery"),
        new("anime_animals",   "萌宠拟人",   "010", "+anime +animals"),
    };

    /// <summary>分类树分组与顺序：风景 → 摄影 → 人物 → 动漫（收藏固定最前，NSFW 最后）。</summary>
    public static readonly (string Title, string[] Keys)[] TreeGroups =
    {
        ("风景", new[] { "nature_landscape", "nature_sea", "nature_mountain", "nature_flower", "nature_sunset", "nature_snow" }),
        ("摄影", new[] { "photo_city", "photo_space", "photo_minimalism", "photo_animals", "photo_cars" }),
        ("人物", new[] { "people_portrait", "people_fashion", "people_sports", "people_movies", "people_street", "people_art" }),
        ("动漫", new[] { "anime_girls", "anime_shonen", "anime_mecha", "anime_games", "anime_scenery", "anime_animals" }),
    };

    public static ChannelDef? Find(string key) => Array.Find(All, c => c.Key == key);
}