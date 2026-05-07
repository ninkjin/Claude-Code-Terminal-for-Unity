using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeTerminalWebViewHost;

public sealed class TerminalHostForm : Form
{
    private readonly HostOptions options;
    private readonly WebView2 webView = new();
    private readonly System.Windows.Forms.Timer embeddedRecoveryTimer = new();
    private readonly System.Windows.Forms.Timer embeddedMaintenanceTimer = new();
    private EmbeddedWindowController? embeddedWindowController;
    private TerminalHostControlServer? controlServer;
    private int embeddedRecoveryTicksRemaining;

    public TerminalHostForm(HostOptions options)
    {
        this.options = options;

        Text = options.Title;
        Width = options.Width;
        Height = options.Height;
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 12, 12);

        if (options.Embedded)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(options.Left, options.Top, options.Width, options.Height);
        }

        webView.Dock = DockStyle.Fill;
        webView.DefaultBackgroundColor = Color.FromArgb(12, 12, 12);
        Controls.Add(webView);

        embeddedRecoveryTimer.Interval = 160;
        embeddedRecoveryTimer.Tick += (_, _) => RecoverEmbeddedWebView();
        embeddedMaintenanceTimer.Interval = 1000;
        embeddedMaintenanceTimer.Tick += (_, _) => RecoverEmbeddedWebView(continueBurst: false);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            if (options.Embedded)
            {
                embeddedWindowController = new EmbeddedWindowController(this, options.ParentWindowHandle, options.UseNativeChildWindow);
                embeddedWindowController.Attach();
                embeddedWindowController.ApplyScreenBounds(options.Left, options.Top, options.Width, options.Height);
                ScheduleEmbeddedRecovery(cycles: 4);
                embeddedMaintenanceTimer.Start();
                StartControlServer();
            }

            var userDataFolder = ResolvedUserDataFolder;
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.PermissionRequested += HandleWebViewPermissionRequested;
            webView.Source = options.Url;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "WebView2 Runtime is not installed. Install Microsoft Edge WebView2 Runtime, then open Claude Code Terminal again.",
                options.Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, options.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private static void HandleWebViewPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
        {
            args.State = CoreWebView2PermissionState.Allow;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        embeddedRecoveryTimer.Stop();
        embeddedRecoveryTimer.Dispose();
        embeddedMaintenanceTimer.Stop();
        embeddedMaintenanceTimer.Dispose();
        controlServer?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ScheduleEmbeddedRecovery(cycles: 4);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        ScheduleEmbeddedRecovery(cycles: 4);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        ScheduleEmbeddedRecovery(cycles: 4);
    }

    private void StartControlServer()
    {
        if (options.ControlPort <= 0)
        {
            return;
        }

        controlServer = new TerminalHostControlServer(
            options.ControlPort,
            (left, top, width, height) => BeginInvoke(() => ApplyEmbeddedBounds(left, top, width, height)));
        controlServer.Start();
    }

    private void ApplyEmbeddedBounds(int left, int top, int width, int height)
    {
        if (embeddedWindowController?.ApplyScreenBounds(left, top, width, height) == true)
        {
            ScheduleEmbeddedRecovery(cycles: 2);
        }
    }

    private void ScheduleEmbeddedRecovery(int cycles = 1)
    {
        if (!options.Embedded)
        {
            return;
        }

        embeddedRecoveryTicksRemaining = Math.Max(embeddedRecoveryTicksRemaining, Math.Max(1, cycles));
        embeddedRecoveryTimer.Stop();
        embeddedRecoveryTimer.Start();
    }

    private void RecoverEmbeddedWebView(bool continueBurst = true)
    {
        embeddedRecoveryTimer.Stop();
        embeddedWindowController?.ReapplyLastBounds();

        if (IsDisposed || webView.IsDisposed)
        {
            return;
        }

        Invalidate(true);
        Update();
        webView.Invalidate();
        webView.Update();

        if (webView.CoreWebView2 != null)
        {
            _ = webView.CoreWebView2.ExecuteScriptAsync(
                "document.body.style.transform='translateZ(0)'; window.dispatchEvent(new Event('resize')); if (window.fitTerminal) window.fitTerminal();");
        }

        if (!continueBurst)
        {
            return;
        }

        embeddedRecoveryTicksRemaining--;
        if (embeddedRecoveryTicksRemaining > 0)
        {
            embeddedRecoveryTimer.Start();
        }
    }

    private string ResolvedUserDataFolder =>
        !string.IsNullOrWhiteSpace(options.UserDataFolder)
            ? options.UserDataFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCodeTerminalForUnity",
                "WebView2");
}
