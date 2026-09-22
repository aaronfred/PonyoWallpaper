namespace PonyoWallpaper;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // （v1.4.2）hosts 提权入口已随 hosts 管理拆分至独立工具 ProxyToolkit，此入口移除

        // 代理模块集成演示：输出配置快照（JSON）+ 规则命中自检，供其他程序参考接入方式
        if (args.Length >= 1 && args[0] == "--proxy-snapshot")
        {
            AppPaths.EnsureAll();
            var cfg = AppConfig.Load();
            Console.WriteLine(ProxyModule.Snapshot(cfg));
            Console.WriteLine("---");
            Console.WriteLine("NeedProxy(wallhaven.cc api) = " +
                ProxyModule.NeedProxy("https://wallhaven.cc/api/v1/search?q=cat", cfg));
            Console.WriteLine("NeedProxy(th.wallhaven.cc)  = " +
                ProxyModule.NeedProxy("https://th.wallhaven.cc/lg/vg/vg19y3.jpg", cfg));
            Console.WriteLine("NeedProxy(example.com)      = " +
                ProxyModule.NeedProxy("https://example.com/img.png", cfg));
            return;
        }

        // 端到端测试入口：搜索 → 下载 → 设置壁纸 → 退出（无 UI）
        if (args.Length >= 1 && args[0] == "--test-rotate")
        {
            try
            {
                AppPaths.EnsureAll();
                Logger.Init();
                Logger.Info("=== test-rotate ===");
                var cfg = AppConfig.Load();
                AppPaths.SetCacheRoot(cfg.CacheDirPath);
                using var api = new WallhavenClient(cfg.ApiKey, cfg.ProxyUrl, cfg.ProxyUser, cfg.ProxyPassword, cfg.ProxyUrls);
                api.SetMirrors(cfg.CfProxyUrls);
                var cache = new CacheManager(AppPaths.CacheFullDir, AppPaths.CacheThumbDir, cfg.CacheLimitMb);
                var engine = new RotationEngine(cfg, api, cache);
                var item = engine.NextAsync().GetAwaiter().GetResult();
                Logger.Info($"test-rotate => {(item == null ? "FAIL" : "OK " + item.Id + " " + item.Resolution)}");
            }
            catch (Exception ex) { Logger.Error("test-rotate", ex); }
            finally { Logger.Close(); }
            return;
        }

        // 代理自测入口：依次用 http / https / socks5 探测同一代理，结果写入日志（无 UI，便于定位协议问题）
        if (args.Length >= 1 && args[0] == "--test-proxy")
        {
            try
            {
                AppPaths.EnsureAll();
                Logger.Init();
                Logger.Info("=== test-proxy ===");
                var cfg = AppConfig.Load();
                var (pUrl, user, pass) = WallhavenClient.ParseProxyCredentials(
                    cfg.ProxyUrl, cfg.ProxyUser, cfg.ProxyPassword);
                var schemeEnd = pUrl.IndexOf("://", StringComparison.Ordinal);
                var hostPort = schemeEnd >= 0 ? pUrl[(schemeEnd + 3)..] : pUrl;
                hostPort = hostPort.TrimStart('/', '\\').TrimEnd('/');
                Logger.Info($"hostPort={hostPort} user={(string.IsNullOrEmpty(user) ? "(none)" : user)}");

                if (hostPort.Length == 0)
                {
                    Logger.Info("no proxy configured, abort");
                }
                else
                {
                    foreach (var scheme in new[] { "http", "https", "socks5" })
                    {
                        try
                        {
                            var handler = ProxyFactory.Create($"{scheme}://{hostPort}", user, pass);
                            if (handler == null) { Logger.Info($"[{scheme}] handler=null"); continue; }
                            using var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                            c.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
                            using var resp = c.GetAsync(
                                "https://wallhaven.cc/api/v1/search?categories=100&purity=100&page=1")
                                .GetAwaiter().GetResult();
                            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            Logger.Info($"[{scheme}] HTTP={(int)resp.StatusCode} bytes={body.Length}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"[{scheme}] FAIL: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Error("test-proxy", ex); }
            finally { Logger.Close(); }
            return;
        }

        if (!SingleInstance.Acquire())
        {
            MessageBox.Show("Ponyo壁纸已在运行", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            AppPaths.EnsureAll();
            Logger.Init();
            Logger.Info("=== ponyo wallpaper starting ===");

            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            var cfg = AppConfig.Load();
            AppPaths.SetCacheRoot(cfg.CacheDirPath);
            var api = new WallhavenClient(cfg.ApiKey, cfg.ProxyUrl, cfg.ProxyUser, cfg.ProxyPassword, cfg.ProxyUrls);
            api.SetMirrors(cfg.CfProxyUrls); // CF 反代：启用后对应链路直连反代
            api.SetManualProxy(cfg.ManualProxy, cfg.ManualProxyLocked); // v1.2.0：手填代理锁定（重启后保持）
            var cache = new CacheManager(AppPaths.CacheFullDir, AppPaths.CacheThumbDir, cfg.CacheLimitMb);
            var engine = new RotationEngine(cfg, api, cache);
            var history = new HistoryStore();
            var favorites = new ListStore(AppPaths.FavoritesFile);
            var blacklist = new ListStore(AppPaths.BlacklistFile);

            // v1.4.2：公共代理池的抓取/保活/扫描模式已随代理管理拆分至独立工具 ProxyToolkit。
            // 本程序链路固定为：直连 → 反代（自定义或内置默认）→ 手填代理，开箱即有可用链路。
            using var mainForm = new MainForm(cfg, api, cache, engine, history, favorites, blacklist);
            ThemeManager.Apply(mainForm, ThemeManager.ShouldUseDark(cfg.Theme));
            using var tray = new TrayIcon();
            using var hotkey = new HotkeyManager();

            hotkey.OnNext += async () => await engine.NextAsync();
            hotkey.OnPrev += async () => await engine.PrevAsync();

            SettingsForm? settingsForm = null;
            void OpenSettings()
            {
                if (settingsForm == null || settingsForm.IsDisposed)
                {
                    settingsForm = new SettingsForm(cfg, api, cache, () =>
                    {
                        ThemeManager.Apply(mainForm, ThemeManager.ShouldUseDark(cfg.Theme));
                        tray.ApplyTheme(ThemeManager.ShouldUseDark(cfg.Theme)); // 托盘菜单同步深浅色
                        mainForm.RebuildTree();
                        UpdateTooltip(null);
                    },
                    () => mainForm.RebuildThumbProxy()); // 代理变更：缩略图客户端即时重建
                    ThemeManager.Apply(settingsForm, ThemeManager.ShouldUseDark(cfg.Theme));
                }
                if (!settingsForm.Visible) settingsForm.Show(mainForm);
                else settingsForm.Activate();
            }

            tray.OnNext += async () => await engine.NextAsync();
            tray.OnPrev += async () => await engine.PrevAsync();
            // 托盘右键「收藏当前壁纸」：把桌面上正在用的那张加入收藏夹（面板可能是隐藏的 → 用气泡反馈）
            tray.OnFavoriteCurrent += () =>
            {
                var msg = mainForm.FavoriteCurrentWallpaper();
                mainForm.InvokeSetStatus(msg);
                tray.ShowBalloon("Ponyo壁纸 · 收藏", msg);
            };
            tray.OnShowMain += () =>
            {
                mainForm.Show();
                mainForm.WindowState = FormWindowState.Normal;
                mainForm.Activate();
            };
            tray.OnSettings += OpenSettings;
            mainForm.OnOpenSettings = OpenSettings;
            tray.OnExit += () => Application.Exit();

            engine.OnRotated += item =>
            {
                mainForm.InvokeSetStatus($"已应用 · {item.Id} · {item.Resolution}");
                history.Append(item, engine.CurrentChannelName, cache.FullPath(item.Id));
                UpdateTooltip(item);
            };

            void UpdateTooltip(WallpaperItem? it)
            {
                // 托盘 ToolTip 只保留快捷键提示，不再显示「自动更换 X 个频道」（避免与首页底部按钮重复）
                _ = it; // 保留入参供未来扩展
                tray.SetTooltip("Ctrl+Alt+N/P 下一张/上一张");
            }
            UpdateTooltip(null);

            if (!cfg.StartMinimized) mainForm.Show();
            engine.Start();

            Logger.Info($"ready. tray icon live. interval={cfg.IntervalMinutes}min rotation=[{string.Join(",", cfg.RotationChannels ?? new List<string>())}]");
            Application.Run(mainForm);

            engine.Stop();
            Logger.Info("shutdown.");
        }
        catch (Exception ex)
        {
            Logger.Error("fatal", ex);
            MessageBox.Show($"启动失败：{ex.Message}\n{ex.StackTrace}",
                "Ponyo壁纸", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SingleInstance.Release();
            Logger.Close();
        }
    }
}