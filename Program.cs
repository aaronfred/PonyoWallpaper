namespace PonyoWallpaper;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 提权进程入口：应用 hosts 后立即退出（弹窗反馈结果）
        if (args.Length >= 2 && args[0] == "--apply-hosts")
        {
            var tmpPath = args[1].Trim('"');
            var ok = false;
            try
            {
                if (File.Exists(tmpPath))
                {
                    ok = HostsUpdater.Apply(File.ReadAllText(tmpPath));
                    File.Delete(tmpPath);
                }
            }
            catch { /* 静默 */ }
            MessageBox.Show(ok ? "hosts 已更新并刷新 DNS 缓存" : "hosts 更新失败，详见日志",
                ok ? "完成" : "错误", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return;
        }

        // 提权进程入口：移除 hosts 映射后立即退出（弹窗反馈结果）
        if (args.Length >= 1 && args[0] == "--remove-hosts")
        {
            var ok = false;
            try { ok = HostsUpdater.Remove(); } catch { /* 静默 */ }
            MessageBox.Show(ok ? "已移除本程序写入的 wallhaven hosts 段（其他条目未改动）" : "移除失败，详见日志",
                ok ? "完成" : "错误", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return;
        }

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
                // 用户代理为空（默认态）或公共池过期时，先自动探测公共代理池再测试，
                // 使自检不依赖任何手动配置
                ProxyTester.EnsurePublicPoolAsync(cfg, api).GetAwaiter().GetResult();
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
            var cache = new CacheManager(AppPaths.CacheFullDir, AppPaths.CacheThumbDir, cfg.CacheLimitMb);
            var engine = new RotationEngine(cfg, api, cache);
            var history = new HistoryStore();
            var favorites = new ListStore(AppPaths.FavoritesFile);
            var blacklist = new ListStore(AppPaths.BlacklistFile);

            // 首次启动/公共代理池过期：后台自动探测公共代理（http/https/socks5 不限，不阻塞界面）。
            // 用户代理（设置里识别成功的）优先于公共池。
            _ = ProxyTester.EnsurePublicPoolAsync(cfg, api);
            // 免费代理寿命以小时计：每 6 小时后台保活一次（内部有新鲜度判定，池健康则跳过）
            using var poolKeepAlive = new System.Threading.Timer(
                _ => { try { ProxyTester.EnsurePublicPoolAsync(cfg, api).GetAwaiter().GetResult(); } catch { /* 静默 */ } },
                null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));

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
                var keys = cfg.RotationChannels is { Count: > 0 }
                    ? cfg.RotationChannels
                    : Channels.All.Select(c => c.Key).ToList();
                string scope;
                if (keys.Count > 3) scope = $"{keys.Count} 个频道";
                else
                {
                    scope = string.Join("+", keys.Select(k =>
                        k == "nsfw" ? "NSFW" : Channels.Find(k)?.Name ?? k));
                    if (scope.Length == 0) scope = "未选择";
                }
                var tip = $"Ponyo壁纸 · 自动更换 {scope} · {cfg.IntervalMinutes}分/张";
                if (it != null) tip += $"\n当前: {it.Id}";
                tip += "\nCtrl+Alt+N/P 下一张/上一张";
                tray.SetTooltip(tip);
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