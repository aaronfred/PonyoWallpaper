using System.Runtime.InteropServices;

namespace PonyoWallpaper;

/// <summary>
/// 全局快捷键：Ctrl+Alt+N 下一张、Ctrl+Alt+P 上一张。
/// 基于 RegisterHotKey + 隐藏消息窗口。
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? OnNext;
    public event Action? OnPrev;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
        // 0x4E = N, 0x50 = P
        if (!RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT, 0x4E))
            Logger.Warn("register hotkey Ctrl+Alt+N failed (may conflict)");
        if (!RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT, 0x50))
            Logger.Warn("register hotkey Ctrl+Alt+P failed (may conflict)");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case 1: OnNext?.Invoke(); break;
                case 2: OnPrev?.Invoke(); break;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, 1);
        UnregisterHotKey(Handle, 2);
        DestroyHandle();
    }
}