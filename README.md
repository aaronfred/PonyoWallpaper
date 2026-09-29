<div align="center">

<img src="resources/ponyo_app_icon.png" width="88" alt="Ponyo 壁纸">

# Ponyo 壁纸

基于 [wallhaven.cc](https://wallhaven.cc) 的 Windows 桌面自动换壁纸工具

C# / .NET 10 WinForms · 绿色单文件 · 免安装

[![Release](https://img.shields.io/github/v/release/aaronfred/PonyoWallpaper?style=flat-square)](https://github.com/aaronfred/PonyoWallpaper/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/aaronfred/PonyoWallpaper/release.yml?style=flat-square&label=CI)](https://github.com/aaronfred/PonyoWallpaper/actions)

**[⬇ 下载最新版](https://github.com/aaronfred/PonyoWallpaper/releases)**

</div>

---

## ✨ 功能

- **频道浏览** — 风景 / 摄影 / 人物 / 动漫 4 大类 20+ 二级频道，中文分类树，瀑布流布局
- **大图预览** — 单击缩略图看大图，可复制 ID / 打开原页面
- **一键换壁纸** — 双击缩略图直接应用；悬停卡片有「预览 / 收藏 / 设为壁纸」按钮
- **分辨率筛选** — 1080p / 2K / 4K / 5K / 8K / 10K 六档
- **自动更换** — 定时换壁纸（5–1440 分钟）；分类子菜单顶部有「全选」，一键勾选整类
- **托盘常驻** — 可设最小化即隐藏到托盘；托盘右键含「下一张 / 上一张 / **收藏当前壁纸**」
- **全局快捷键** — `Ctrl+Alt+N` 下一张 / `Ctrl+Alt+P` 上一张
- **多显示器** — 所有屏幕同图，或每屏独立壁纸
- **hosts 加速** — 内置 hosts 更新与 DNS 刷新（可选）
- **缓存管理** — LRU 自动淘汰，配额可调
- **主题** — 跟随系统 / 浅色 / 深色

## 🌐 代理与代理池（国内网络必读）

国内直连 wallhaven 通常失败。程序内置 **四级代理链路**，按优先级逐级尝试，失败自动切换：

| 优先级 | 链路 | 说明 |
|:---:|---|---|
| 1 | **直连** | 默认先试 |
| 2 | **反向代理** | 留空则使用**内置默认反代**（开箱即用，地址不显示）；也可填自己的反代，支持多条按序尝试，境内直连可达，**优先于一切代理**（管理页附「反代搭建指引」）。**办公 / 企业网络环境首选**：单一域名 + 标准 443，流量特征与普通访问网站无异 |
| 3 | **用户代理池** | 自己的代理，每行一个，支持 `socks5://` 与 `http://`，可含 `user:pass@` 账密 |
| 4 | **公共代理池** | 从公共源探测抓取。默认 **低并发（Quiet）且仅手动触发**，对办公网络友好；内置默认反代可用时不再需要 |

> **开箱即用**：程序内置一条默认反向代理，无需任何配置即可在国内网络直接使用。
> 它**不显示地址**，只作为兜底链路（直连失败后自动使用）。
> 想用自己的反代：按设置页「代理填写指南」在配置文件 `CfProxyUrls` 中填写（v1.3.1 起代理管理页已移除）；或点设置页的「恢复默认代理」。
> 默认反代只是个人学习用途的共享资源，请勿抓取地址用于批量/商业场景。

代理相关入口（设置页）：

- **手填代理 + 延迟显示** — 设置页「当前代理」可直接填代理地址，回车或失焦即自动实测并锁定使用（不静默降级）；留空则回到自动链路。打开设置页会实测当前链路延迟，超过 2 秒会提示恢复默认
- **恢复默认代理** — 一键清掉自定义反代与手填代理，回到内置默认反代
- **代理填写指南** — 各种代理填写示例（HTTP / SOCKS5 / 账密）、自建反代配置文件字段、Cloudflare Worker / Caddy / Nginx 搭建指南与完整代码
- **公共代理池已移除** — 该功能由独立工具 ProxyToolkit 承接

## 📦 下载

到 [Releases](https://github.com/aaronfred/PonyoWallpaper/releases) 页面下载：

| 包 | 说明 |
|---|---|
| `-win-x64-full.zip` | 自包含单文件，解压即用，**无需安装 .NET**（约 47 MB） |
| `-win-x64-lite.zip` | 精简单文件，需已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（约 1 MB） |

## 🚀 快速上手

1. 解压后运行 `PonyoWallpaper.exe`，程序常驻系统托盘，双击托盘图标打开主界面
2. 左侧选频道浏览；**单击**缩略图看大图，**双击**设为壁纸（悬停卡片也有「预览 / 收藏 / 设为壁纸」按钮）；预览窗可「下载」当前原图（弹窗选择保存位置，直连不可用自动切默认代理）
3. 底部工具栏：轮换频道（每个大类子菜单顶部可「全选」；**收藏**也在轮换范围里，随机应用收藏夹的图）/ 间隔 / 分辨率 / 填充 / 排序 / 多屏
4. **国内网络先配代理**：开箱即用内置默认反代，无需配置；要自定义代理，直接在设置页「当前代理」框里手填（填完自动实测生效），自建反代按「代理填写指南」在配置文件中填写
5. 托盘图标右键可「下一张 / 上一张 / **收藏当前壁纸**」（收藏的是桌面上正在用的那张）
6. NSFW 分类与 API Key 在隐藏面板：设置页版本号**连点 5 次**（或 `Ctrl+Shift+K`）唤出，需访问密码

## 🔨 本地构建

```powershell
# full：自包含单文件（约 47 MB）
dotnet publish PonyoWallpaper.csproj -c Release -r win-x64 `
  -p:SelfContained=true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true -o release\full

# lite：framework-dependent 单文件（约 1 MB）
dotnet publish PonyoWallpaper.csproj -c Release -r win-x64 `
  -p:SelfContained=false -p:PublishSingleFile=true -o release\lite
```

推送 `v*` 标签（如 `v1.3.0`）会自动触发 GitHub Actions 构建并发布 Release。

---

<div align="center">

仅供个人学习交流使用 · 图片来源与版权归 [wallhaven.cc](https://wallhaven.cc) 及原作者所有

</div>
