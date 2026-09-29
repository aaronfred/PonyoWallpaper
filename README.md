<div align="center">

<img src="resources/ponyo_app_icon.png" width="88" alt="Ponyo 壁纸">

# Ponyo 壁纸

多源聚合的 Windows 桌面自动换壁纸工具（360 壁纸 / WallpaperCave / GitHub 图库 / [wallhaven.cc](https://wallhaven.cc)）

C# / .NET 10 WinForms · 绿色单文件 · 免安装

[![Release](https://img.shields.io/github/v/release/aaronfred/PonyoWallpaper?style=flat-square)](https://github.com/aaronfred/PonyoWallpaper/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/aaronfred/PonyoWallpaper/release.yml?style=flat-square&label=CI)](https://github.com/aaronfred/PonyoWallpaper/actions)

**[⬇ 下载最新版](https://github.com/aaronfred/PonyoWallpaper/releases)**

</div>

---

## ✨ 功能

- **多源聚合** — 默认启用 3 个免注册源（360 壁纸 / WallpaperCave / GitHub 图库），开箱即用；可选 wallhaven（需代理）
- **壁纸源多选** — 源菜单 = 顶部「全选」+ 各源勾选；只勾一个源时，左侧分类树与自动更换范围随之切换成**该源自己的分类**
- **频道浏览** — 多源模式下显示 风景 / 摄影 / 人物 / 动漫 4 大类；单源时显示该源的原生分类（如 360 的 18 个分类、WallpaperCave 的检索词）
- **瀑布流 + 无限下拉** — 自适应 2–6 列真瀑布流，滚动到底自动续页
- **大图预览** — 单击缩略图看大图（先摆缩略图占位，再加载原图），可复制 ID / 打开原页面
- **一键换壁纸** — 双击缩略图直接应用；悬停卡片有「预览 / 收藏 / 设为壁纸」按钮
- **分辨率筛选** — 1080p / 2K / 4K / 5K / 8K / 10K 六档
- **自动更换** — 定时换壁纸（5–1440 分钟）；范围可多选（分类 + **收藏**：从收藏夹随机取图），分类子菜单顶部有「全选」，一键勾选整类
- **托盘常驻** — 可设最小化即隐藏到托盘（同时释放缩略图，后台内存回到基线）；托盘右键含「下一张 / 上一张 / **收藏当前壁纸**」
- **全局快捷键** — `Ctrl+Alt+N` 下一张 / `Ctrl+Alt+P` 上一张
- **多显示器** — 所有屏幕同图，或每屏独立壁纸
- **零解码依赖** — 只处理 JPEG / PNG，不引入 webp/avif 解码库
- **缓存管理** — LRU 自动淘汰，配额可调；浏览只缓存 240px 小图
- **主题** — 跟随系统 / 浅色 / 深色

## 🌐 壁纸源（国内网络必读）

| 源 | 默认 | 网络 | 分类体系 |
|---|:---:|---|---|
| **360 壁纸** | ✅ | 免注册，纯国内直连 | 自家 18 类（风景大片 / 小清新 / 萌宠动物 / …） |
| **WallpaperCave** | ✅ | 免注册，国内可直连 | 检索词（landscape / city / anime girl …），只出电脑横屏壁纸 |
| **GitHub 图库** | ✅ | 走公共镜像多链路，无需配置 | 仓库目录关键词（约 4700 张，仅 jpg/png） |
| **wallhaven** | ☐ | **需反代 / 代理**，国内直连不可达 | 23 个通用频道，功能最全 |

## 🌐 代理链路（wallhaven 必需）

wallhaven 国内直连通常失败。程序为它内置**分级链路**，按优先级逐级尝试，失败自动切换：

| 优先级 | 链路 | 说明 |
|:---:|---|---|
| 1 | **直连** | 快速失败（连接 3s / 整体 5s），确认不可用后本会话不再尝试 |
| 2 | **反向代理** | 留空则使用**内置默认反代**（开箱即用，地址不显示，**主 + 备两条自动切换**）；也可填自己的反代，支持多条按序尝试，境内直连可达。**办公 / 企业网络环境首选**：单一域名 + 标准 443，流量特征与普通访问网站无异 |
| 3 | **用户代理** | 自己的代理，每行一个，支持 `socks5://` 与 `http://`，可含 `user:pass@` 账密 |

> **开箱即用**：程序内置默认反向代理（主 + 备），无需任何配置即可在国内网络直接使用；
> 主地址不可达时自动切到备用。它**不显示地址**；地址混淆存储，也不会写入日志。
> 默认反代只是个人学习用途的共享资源，请勿抓取地址用于批量/商业场景。
> 想用自己的反代/代理：按设置页「代理填写指南」在配置文件中填写（v1.5.8 起界面不再提供手填框）；或点设置页的「恢复默认代理」一键回到内置默认。

**链路粘性**：程序记住上次**实测有效**的那条链路（直连 / 默认反代 / 你的反代 / 你的代理），下次启动直接用它，只有连不上才开始轮换 —— 不会再出现「明明反代可用却一直走不通」的情况。

代理相关入口（设置页「当前代理」区）：

- **状态条** — 当前链路类型 + 实测延迟，持续不可达时提示 ⚠ 恢复默认
- **恢复默认代理** — 一键清掉自定义反代与手填代理（配置文件 `ManualProxy` / `CfProxyUrls`），回到内置默认反代
- **代理填写指南** — 各种代理填写示例（`ManualProxy`：HTTP / SOCKS5 / 账密）、自建反代字段（`CfProxyUrls`）、Cloudflare Worker / Caddy / Nginx 搭建指南与完整代码

## 📦 下载

到 [Releases](https://github.com/aaronfred/PonyoWallpaper/releases) 页面下载：

| 包 | 说明 |
|---|---|
| `-win-x64-full.zip` | 自包含单文件，解压即用，**无需安装 .NET**（约 49 MB） |
| `-win-x64-lite.zip` | 精简单文件，需已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（约 750 KB） |

## 🚀 快速上手

1. 解压后运行 `PonyoWallpaper.exe`，程序常驻系统托盘，双击托盘图标打开主界面
2. 左侧选分类浏览（默认 3 个免注册源，无需任何配置）；**单击**缩略图看大图，**双击**设为壁纸（悬停卡片也有「预览 / 收藏 / 设为壁纸」按钮）
3. 想只看某一个源：点底部「壁纸源」→ 只勾它一个，左侧树会切换成该源自己的分类
4. 底部工具栏：自动更换范围（随壁纸源变化：单源=该源分类+收藏，多源=大类频道+收藏；顶部可「全选」）/ 间隔 / 分辨率 / 填充 / 排序 / 多屏
5. **要用 wallhaven 才需要配代理**：开箱即用内置默认反代，无需配置；要自定义代理/反代，按设置页「代理填写指南」在配置文件中填写
6. 托盘图标右键可「下一张 / 上一张 / **收藏当前壁纸**」（收藏的是桌面上正在用的那张）
7. NSFW 分类与 API Key 在隐藏面板：设置页版本号**连点 5 次**（或 `Ctrl+Shift+K`）唤出，需访问密码

## 🔨 本地构建

```powershell
# full：自包含单文件（约 49 MB）
dotnet publish PonyoWallpaper.csproj -c Release -r win-x64 `
  -p:SelfContained=true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true -o release\full

# lite：framework-dependent 单文件（约 750 KB）
dotnet publish PonyoWallpaper.csproj -c Release -r win-x64 `
  -p:SelfContained=false -p:PublishSingleFile=true -o release\lite
```

推送 `v*` 标签（如 `v1.5.7`）会自动触发 GitHub Actions 构建并发布 Release。

> 单文件发布偶发 `MSB4018 GenerateBundle` 报错时，先删掉 `obj/Release` 与 `bin/Release` 再重发即可。

---

<div align="center">

仅供个人学习交流使用 · 图片来源与版权归各来源站点及原作者所有

</div>
