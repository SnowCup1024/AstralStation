using AstralStation.Launch;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AstralStation.Services;

public sealed class DesktopHost : IDisposable
{
    private const uint TrayMessage = 0x8000 + 71;
    public static readonly uint RestoreMessage = RegisterWindowMessageW("AstralStation.Restore");
    private static readonly uint UpgradeMessage = RegisterWindowMessageW("AstralStation.Upgrade");
    private readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly nint window;
    private readonly SubclassProc callback;
    private readonly string identity, root;
    private NotifyIcon data;
    private bool tray;
    public event Action? RestoreRequested;
    public event Action? PauseRequested;
    public event Action? ExitRequested;
    public event Action? UpgradeRequested;
    public event Action<bool>? MenuActive;
    public event Action? UserRestored;
    public bool IsHidden => !IsWindowVisible(window) || IsIconic(window);
    public static string Identity(string root) => "AstralStation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant())))[..20];
    public DesktopHost(nint handle, string dataRoot)
    {
        window = handle; root = dataRoot; identity = Identity(root); callback = WndProc;
        if (!SetWindowSubclass(window, callback, 71, 0)) throw new InvalidOperationException("无法注册窗口消息处理。");
        SetPropW(window, identity, 1);
        SetPropW(window, "AstralStation.UpgradeProtocol", 1);
        data = new() { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = window, Id = 1, Flags = 7, Callback = TrayMessage, Tip = "星间驿站 · 启动管理", Info = "", InfoTitle = "" };
        // The executable carries the icon even in single-file publishing.
        data.Icon = ExtractIconW(0, Environment.ProcessPath!, 0);
    }
    public static void ActivateExisting(string root)
    {
        string identity = Identity(root);
        EnumWindows((handle, _) => { if (GetPropW(handle, identity) == 0) return true; GetWindowThreadProcessId(handle, out var pid); AllowSetForegroundWindow(pid); PostMessageW(handle, RestoreMessage, 0, 0); return false; }, 0);
    }
    // Return false for an equal/newer instance (including versions we cannot identify).
    public static bool RequestUpgrade(string root)
    {
        string identity = Identity(root);
        bool waiting = true;
        EnumWindows((handle, _) =>
        {
            if (GetPropW(handle, identity) == 0) return true;
            GetWindowThreadProcessId(handle, out var pid);
            AllowSetForegroundWindow(pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string? path = process.MainModule?.FileName;
                if (path is not null && CompareVersions(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion,
                    FileVersionInfo.GetVersionInfo(path).ProductVersion) > 0)
                {
                    // Earlier releases understand normal close; never terminate them forcibly.
                    PostMessageW(handle, GetPropW(handle, "AstralStation.UpgradeProtocol") != 0 ? UpgradeMessage : 0x10, 0, 0);
                    return false;
                }
            }
            catch { }
            PostMessageW(handle, RestoreMessage, 0, 0);
            waiting = false;
            return false;
        }, 0);
        return waiting;
    }
    internal static int CompareVersions(string? left, string? right)
    {
        static (Version Core, int Alpha)? Parse(string? value)
        {
            var parts = value?.Split('+')[0].Split('-', 2);
            if (parts is null || !Version.TryParse(parts[0], out var core)) return null;
            if (parts.Length == 1) return (core, int.MaxValue);
            return parts[1].StartsWith("alpha.") && int.TryParse(parts[1][6..], out int alpha) ? (core, alpha) : null;
        }
        var a = Parse(left); var b = Parse(right);
        if (a is null || b is null) return 0;
        int result = a.Value.Core.CompareTo(b.Value.Core);
        return result != 0 ? result : a.Value.Alpha.CompareTo(b.Value.Alpha);
    }
    public void SetTray(bool enabled)
    {
        if (tray == enabled) return;
        if (enabled && !Shell_NotifyIconW(0, ref data)) throw new InvalidOperationException("无法创建托盘图标。");
        if (!enabled) { Shell_NotifyIconW(2, ref data); if (IsHidden) Restore(); }
        tray = enabled;
    }
    public void Minimize() { ShowWindow(window, 6); if (tray) ShowWindow(window, 0); }
    public void Restore() { ShowWindow(window, 9); SetForegroundWindow(window); }
    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        try
        {
            if (message == 0x24) // WM_GETMINMAXINFO: device-independent minimum, recalculated on each monitor.
            {
                DefSubclassProc(hwnd, message, wParam, lParam);
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                double scale = GetDpiForWindow(hwnd) / 96d;
                limits.MinTrackSize = new Point { X = (int)Math.Ceiling(800 * scale), Y = (int)Math.Ceiling(600 * scale) };
                Marshal.StructureToPtr(limits, lParam, false);
                return 0;
            }
            if (message == RestoreMessage) { RestoreRequested?.Invoke(); return 0; }
            if (message == UpgradeMessage) { UpgradeRequested?.Invoke(); return 0; }
            if (message == taskbarCreated && tray) { if (!Shell_NotifyIconW(0, ref data)) { tray = false; Restore(); } }
            if (message == 5) { if (wParam == 1 && tray) ShowWindow(window, 0); else if (wParam == 0) UserRestored?.Invoke(); }
            if (message == TrayMessage)
            {
                if ((uint)lParam is 0x202 or 0x203) RestoreRequested?.Invoke();
                if ((uint)lParam is 0x205 or 0x7B)
                {
                    MenuActive?.Invoke(true); uint command = 0;
                    try
                    {
                        nint menu = CreatePopupMenu();
                        try { AppendMenuW(menu, 0, 1, "打开星间驿站"); AppendMenuW(menu, 0, 2, "暂停 / 恢复队列"); AppendMenuW(menu, 0, 3, "退出"); GetCursorPos(out var point); SetForegroundWindow(window); command = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, window, 0); }
                        finally { DestroyMenu(menu); }
                    }
                    finally { MenuActive?.Invoke(false); }
                    if (command == 1) RestoreRequested?.Invoke(); else if (command == 2) PauseRequested?.Invoke(); else if (command == 3) ExitRequested?.Invoke();
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("窗口消息处理失败：" + ex.Message); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private string StartupCommand => Quote(Environment.ProcessPath!) + " --data-dir " + Quote(root) + " --login-startup";
    public bool LoginEnabled { get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue(identity) is string command && !string.IsNullOrWhiteSpace(command); } }
    // The registration belongs to the data directory, not to a versioned EXE path.
    // Only retarget an existing registration; a deleted/disabled entry stays disabled.
    public void RefreshLoginTarget()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key?.GetValue(identity) is string command && !string.IsNullOrWhiteSpace(command) && command != StartupCommand)
            key.SetValue(identity, StartupCommand);
    }
    public void SetLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(identity, StartupCommand); else key.DeleteValue(identity, false);
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0; }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static void OpenFolder(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(Path.GetFullPath(path))) { UseShellExecute = true });
        else { string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!; if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("所在目录不存在。"); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
    }
    public static bool Focus(LaunchTask task, WindowsProcessHost processes)
    {
        var names = task.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase); var ids = processes.Snapshot().Where(p => names.Contains(p.Name)).Select(p => (uint)p.Id).ToHashSet(); nint found = 0;
        EnumWindows((hwnd, _) => { GetWindowThreadProcessId(hwnd, out var pid); if (!ids.Contains(pid) || !IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != 0 || (GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80) != 0) return true; DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4); if (cloaked != 0) return true; found = hwnd; return false; }, 0);
        if (found == 0) return false;
        if (IsIconic(found)) ShowWindow(found, 9); SetForegroundWindow(found); return true;
    }
    public void Dispose() { if (tray) Shell_NotifyIconW(2, ref data); if (data.Icon != 0) DestroyIcon(data.Icon); RemovePropW(window, identity); RemoveWindowSubclass(window, callback, 71); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIcon
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint window);
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetPropW(nint hwnd, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemovePropW(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attr, out int value, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint command, ref NotifyIcon data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint ExtractIconW(nint instance, string path, uint index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
