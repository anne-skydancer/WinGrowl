using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;
using WinGrowl.Core.Gntp;
using WinGrowl.Core.Registration;

namespace WinGrowl.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();

        var config = AppConfig.Load();
        var registry = new ApplicationRegistry();
        var toaster = new ToastBridge();
        using var log = new DiagLog();

        var serverOptions = new GntpServerOptions
        {
            Endpoint = config.GetEndpoint(),
            Password = config.Password,
            AllowNetworkClients = config.AllowNetworkClients,
        };

        var server = new GntpServer(serverOptions, registry);
        var syncCtx = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(syncCtx);

        // ToastNotificationManagerCompat supplies the unpackaged COM activation
        // registration without depending on the Windows App SDK Singleton MSIX.
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            try
            {
                var parsed = ToastArguments.Parse(args.Argument);
                bool focused = false;
                string route = "<none>";
                if (parsed.TryGetValue("senderPid", out var pidString)
                    && int.TryParse(pidString, out var pid) && pid > 0)
                {
                    focused = WindowActivator.FocusByPid(pid);
                    route = $"pid={pid}";
                }
                if (!focused
                    && parsed.TryGetValue("applicationName", out var appName)
                    && !string.IsNullOrEmpty(appName))
                {
                    focused = WindowActivator.FocusByApplicationName(appName);
                    route = route == "<none>" ? $"name='{appName}'" : route + $" fallback-name='{appName}'";
                }
                log.Write($"toast-click route={route} focused={focused}");
            }
            catch (Exception ex) { log.Write($"toast-click-error: {ex.Message}"); }
        };

        server.Diagnostic += msg => log.Write(msg);
        server.Registered += (_, r) => log.Write($"REGISTER app='{r.ApplicationName}' types={r.Types.Count}");
        server.Notification += (_, n) =>
        {
            // Resolve the exact sender instance both for click-back and to
            // avoid duplicating the source application's foreground alert.
            int? senderPid = n.SenderEndPoint is { } ep
                ? TcpPidResolver.ResolvePid(ep, serverOptions.Endpoint.Port)
                : null;
            string pidTag = senderPid is int p ? $"pid={p}" : "pid=?";
            bool senderIsForeground = senderPid is int resolvedPid
                ? ForegroundProbe.IsProcessForeground(resolvedPid)
                : ForegroundProbe.IsApplicationForeground(n.ApplicationName);
            // Snippet of Notification-Text so we can see what the source
            // app actually shipped — needed to debug "body missing" /
            // "type not arriving" reports against real GNTP traffic.
            int textLen = n.Text?.Length ?? 0;
            string textSnippet = n.Text is null
                ? "<null>"
                : (n.Text.Length <= 80 ? n.Text : n.Text.Substring(0, 80) + "...");
            textSnippet = textSnippet.Replace("\r", "\\r").Replace("\n", "\\n");
            log.Write($"NOTIFY app='{n.ApplicationName}' name='{n.NotificationName}' title='{n.Title}' text-len={textLen} text='{textSnippet}' {pidTag} senderIsForeground={senderIsForeground}");
            if (!config.ShowToasts) return;
            if (senderIsForeground)
            {
                log.Write($"toast-skipped sender-foreground app='{n.ApplicationName}' name='{n.NotificationName}' {pidTag}");
                return;
            }
            syncCtx.Post(_ =>
            {
                try
                {
                    toaster.Show(n, senderPid);
                    log.Write($"toast-dispatched app='{n.ApplicationName}' name='{n.NotificationName}' {pidTag}");
                }
                catch (Exception ex) { log.Write($"toast-error: {ex.Message}"); }
            }, null);
        };
        server.ProtocolError += (_, ex) => log.Write($"protocol-error {ex.Code}: {ex.Message}");

        var tray = new TrayIconHost(registry, config, () => Application.Exit());

        try
        {
            server.StartAsync().GetAwaiter().GetResult();
            var host = serverOptions.AllowNetworkClients ? "all interfaces" : "localhost";
            var status = $"Listening on {host} port {serverOptions.Endpoint.Port}";
            tray.SetStatus(status);
            log.Write(status + $" — log at {log.Path}");
        }
        catch (Exception ex)
        {
            tray.SetStatus($"listen failed: {ex.Message}");
            log.Write($"listen failed: {ex.Message}");
            MessageBox.Show($"Could not bind :{serverOptions.Endpoint.Port}:\n{ex.Message}", "WinGrowl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Application.ApplicationExit += async (_, _) =>
        {
            try { await server.DisposeAsync(); } catch { }
        };

        Application.Run();

        tray.Dispose();
    }
}
