<div align="center">

<img src="resources/ponyo_app_icon.png" width="88" alt="Ponyo 壁纸">

# Ponyo 壁纸

基于 [wallhaven.cc](https://wallhaven.cc) 的 Windows 桌面自动换壁纸工具

C# / .NET 10 WinForms · 绿色单文件 · 免安装

[![Release](https://img.shields.io/github/v/release/aaronfred/PonyoWallpaper?style=flat-square)](https://github.com/aaronfred/PonyoWallpaper/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/aaronfred/PonyoWallpaper/Release?style=flat-square&label=CI)](https://github.com/aaronfred/PonyoWallpaper/actions)

**[⬇ 下载最新版](https://github.com/aaronfred/PonyoWallpaper/releases)**

</div>

---

## ✨ 功能

- **频道浏览** — 风景 / 摄影 / 人物 / 动漫 4 大类 20+ 二级频道，中文分类树，瀑布流布局
- **自动更换** — 定时换壁纸（1–1440 分钟），自由勾选参与轮换的频道
- **全局快捷键** — `Ctrl+Alt+N` 下一张 / `Ctrl+Alt+P` 上一张
- **多显示器** — 所有屏幕同图，或每屏独立壁纸
- **代理链路** — 直连 → 反向代理 → 用户代理 → 公共代理池，逐级自动故障转移，国内网络可用
- **缓存管理** — LRU 自动淘汰，配额可调；收藏 / 历史 / 黑名单
- **主题** — 跟随系统 / 浅色 / 深色

## 📦 下载

到 [Releases](https://github.com/aaronfred/PonyoWallpaper/releases) 页面下载：

| 包 | 说明 |
|---|---|
| `-win-x64-full.zip` | 自包含单文件，解压即用，**无需安装 .NET**（约 47 MB） |
| `-win-x64-lite.zip` | 精简单文件，需已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（约 1 MB） |

## 🚀 快速上手

1. 解压后运行 `PonyoWallpaper.exe`，程序常驻系统托盘，双击托盘图标打开主界面
2. 左侧选择频道浏览，点击缩略图即设为壁纸
3. 底部工具栏：轮换频道 / 间隔 / 分辨率 / 填充 / 排序 / 多屏
4. **国内网络请先配代理**：设置 → 代理管理，推荐填反向代理地址（页面内有「反代搭建指引」），也可用本地代理 `socks5://127.0.0.1:7890`
5. NSFW 分类与 API Key 在隐藏面板：设置页版本号**连点 5 次**（或 `Ctrl+Shift+K`）唤出，需访问密码

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

推送 `v*` 标签（如 `v1.1.0`）会自动触发 GitHub Actions 构建并发布 Release。

---

<div align="center">

仅供个人学习交流使用 · 图片来源与版权归 [wallhaven.cc](https://wallhaven.cc) 及原作者所有

</div>
