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
- **一键换壁纸** — 双击缩略图直接应用；收藏 / 黑名单本地管理
- **自动更换** — 定时换壁纸（5–1440 分钟），自由勾选参与轮换的频道
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
| 2 | **反向代理** | wallhaven 反代地址，可配多条按序尝试，境内直连可达，**优先于一切代理**（管理页附「反代搭建指引」） |
| 3 | **用户代理池** | 自己的代理，每行一个，支持 `socks5://` 与 `http://`，可含 `user:pass@` 账密 |
| 4 | **公共代理池** | 程序自动从公共源探测抓取（超 6 小时或池内不足 2 个时自动刷新） |

配套能力（设置 → **代理管理**）：

- **优选代理** — 并发实测现有各条链路，一键切换到最快且可用的一级
- **优选代理池 / 更新代理源** — 对公共池重新探测，源地址可自定义
- **需代理的网址规则** — 域名后缀匹配，默认 wallhaven 三域名，可增删以便复用到其他程序
- 所有改动即时保存生效，无需重启

## 📦 下载

到 [Releases](https://github.com/aaronfred/PonyoWallpaper/releases) 页面下载：

| 包 | 说明 |
|---|---|
| `-win-x64-full.zip` | 自包含单文件，解压即用，**无需安装 .NET**（约 47 MB） |
| `-win-x64-lite.zip` | 精简单文件，需已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（约 1 MB） |

## 🚀 快速上手

1. 解压后运行 `PonyoWallpaper.exe`，程序常驻系统托盘，双击托盘图标打开主界面
2. 左侧选频道浏览；**单击**缩略图看大图，**双击**设为壁纸（悬停卡片也有「预览 / 收藏 / 设为壁纸」按钮）
3. 底部工具栏：轮换频道 / 间隔 / 分辨率 / 填充 / 排序 / 多屏
4. **国内网络先配代理**：设置 → 代理管理（见上方代理说明，推荐先填反代地址）
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
