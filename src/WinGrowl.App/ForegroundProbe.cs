using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinGrowl.App;

internal static class ForegroundProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static bool IsProcessForeground(int pid)
    {
        if (pid <= 0) return false;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out var foregroundPid);
        return foregroundPid == (uint)pid;
    }

    public static bool IsApplicationForeground(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName)) return false;

        var token = applicationName.Split(new[] { ' ', '-', '_' }, 2,
            StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length == 0) return false;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith(token, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
