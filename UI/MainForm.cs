using System.Text.Json.Nodes;
using CNM.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CNM.UI;

public sealed class MainForm : Form
{
    private static readonly Color DarkBg = Color.FromArgb(14, 16, 20);
    private static readonly Color LightBg = Color.FromArgb(238, 241, 246);

    private readonly App _app;
    private readonly WebView2 _web;
    private NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private ToolStripMenuItem _miToggle;
    private bool _exiting;
    private bool _webReady;
    private readonly Queue<string> _pending = new();   // 界面还没就绪就先攒着，就绪后补发（别丢）
    private bool _balloonShown;
    private int _logW = 1060;
    private int _logH = 700;

    public MainForm(App app)
    {
        _app = app;

        Text = "CNM — Chemingitch China Model";
        AutoScaleMode = AutoScaleMode.None;   // 尺寸自己按 DPI 换算，别让 WinForms 再缩一遍
        _logW = Math.Max(880, app.Settings.WinWidth);
        _logH = Math.Max(560, app.Settings.WinHeight);
        ClientSize = new Size(_logW, _logH);  // 占位，OnHandleCreated 里按真实 DPI 重算
        MinimumSize = new Size(860, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = IsLight ? LightBg : DarkBg;
        Icon = Branding.LoadAppIcon(32);
        Font = new Font("Microsoft YaHei UI", 9f);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);

        _app.Send += OnSend;
        _app.Toast += OnToast;
        _app.ThemeChanged += OnThemeChanged;
        _app.IconChanged += OnIconChanged;

        BuildTray();
    }

    private bool IsLight => string.Equals(_app.Settings.Theme, "light", StringComparison.OrdinalIgnoreCase);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowSize();
        ApplyTitleBarTheme();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyWindowSize();
    }

    /// <summary>把逻辑尺寸按当前 DPI 换算成物理像素，并兜到屏幕工作区内</summary>
    private void ApplyWindowSize()
    {
        try
        {
            float scale = DeviceDpi / 96f;
            var wa = Screen.FromHandle(Handle).WorkingArea;
            int maxW = (int)(wa.Width / scale) - 40;
            int maxH = (int)(wa.Height / scale) - 60;
            int w = Math.Max(860, Math.Min(_logW, maxW));
            int h = Math.Max(540, Math.Min(_logH, maxH));
            MinimumSize = new Size((int)(860 * scale), (int)(540 * scale));
            ClientSize = new Size((int)(w * scale), (int)(h * scale));
        }
        catch { }
    }

    private void OnThemeChanged(string theme)
    {
        try
        {
            if (IsHandleCreated) BeginInvoke(new Action(ApplyTitleBarTheme));
        }
        catch { }
    }

    private void OnIconChanged()
    {
        try
        {
            if (IsHandleCreated) BeginInvoke(new Action(ApplyAppIcon));
        }
        catch { }
    }

    /// <summary>换图标：窗口标题栏 + 托盘一起换</summary>
    private void ApplyAppIcon()
    {
        try
        {
            Icon = Branding.LoadAppIcon(32);
            if (_tray != null) _tray.Icon = Branding.LoadAppIcon(16);
        }
        catch { }
    }

    private void ApplyTitleBarTheme()
    {
        bool dark = !IsLight;
        DarkTitleBar.Apply(Handle, dark);
        // 界面底色也跟着走，避免切主题时闪一下
        var bg = dark ? DarkBg : LightBg;
        BackColor = bg;
        try { _web.DefaultBackgroundColor = bg; } catch { }
        // 托盘右键菜单跟着换，不然深色下弹出一块白的
        TrayMenu.ApplyTheme(_menu, dark);
    }

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = Branding.LoadAppIcon(16),
            Text = "CNM",
            Visible = true
        };

        var menu = TrayMenu.Create(out var miShow, out _miToggle, out var miExit);
        miShow.Click += (_, _) => Restore();
        _miToggle.Click += (_, _) =>
        {
            if (_app.Router.Running) _app.Router.Stop();
            else _app.Router.Start();
            RefreshTray();
        };
        miExit.Click += (_, _) => ExitApp();

        TrayMenu.ApplyTheme(menu, !IsLight);   // 菜单也跟主题，不要白底
        _menu = menu;
        menu.Opening += (_, _) => RefreshTray();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore();
        RefreshTray();
    }

    private void RefreshTray()
    {
        try
        {
            _miToggle.Text = _app.Router.Running ? "停止服务" : "启动服务";
            _tray.Text = "CNM — " + (_app.Router.Running ? $"服务中 :{_app.Router.Port}" : "服务已停");
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.EnsureVisible(this);   // 从脚本/计划任务启动时可能继承到 SW_HIDE：窗口"起来了但看不见"
        ApplyTitleBarTheme();   // 显示后再压一次，防止 DWM 在创建时把颜色重置
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        var env = Path.Combine(Settings.DataDir, "webview2");
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(wwwroot))
        {
            MessageBox.Show(this, "找不到界面目录：\n" + wwwroot, "CNM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            Directory.CreateDirectory(env);
            var core = await CoreWebView2Environment.CreateAsync(null, env, null);
            await _web.EnsureCoreWebView2Async(core);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "初始化 WebView2 失败：\n" + ex.Message +
                "\n\nWin11 自带 WebView2；Win10 需要装一下微软的 WebView2 Runtime（Evergreen 版）。",
                "CNM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var c = _web.CoreWebView2;
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.AreDevToolsEnabled = true;
        c.Settings.IsZoomControlEnabled = false;
        c.SetVirtualHostNameToFolderMapping("cnm.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        try
        {
            // 自定义图标给界面用（侧栏 logo 读这里）
            Directory.CreateDirectory(Settings.BrandDir);
            c.SetVirtualHostNameToFolderMapping("brand.cnm.local", Settings.BrandDir, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }
        try
        {
            // 出页面之前先把主题定下来，浅色时不会先闪一下深色
            await c.AddScriptToExecuteOnDocumentCreatedAsync(
                "try{document.documentElement.setAttribute('data-theme','" + (IsLight ? "light" : "dark") + "');}catch(e){}");
        }
        catch { }
        c.WebMessageReceived += OnWebMessage;
        c.NavigationCompleted += (_, _) => { _webReady = true; _app.StartUp(); FlushPending(); };

        // 界面是从磁盘上的 wwwroot 直接给的，WebView2 有磁盘缓存 ——
        // 换新版本后可能还在渲染上一次的 index.html / app.js / style.css（"我明明改了怎么没变"）。
        // 启动时清一次缓存，再带版本号导航，保证看到的一定是当前这份界面。
        try { await c.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); } catch { }
        _web.Source = new Uri("https://cnm.local/index.html?v=" + UiVersion(wwwroot));
    }

    /// <summary>界面文件的版本号（取这几个文件最后修改时间的最大值）—— 用来当 ?v= 的防缓存参数</summary>
    private static string UiVersion(string wwwroot)
    {
        long v = 0;
        foreach (var f in new[] { "index.html", "app.js", "style.css", "logos.js" })
        {
            try
            {
                var p = Path.Combine(wwwroot, f);
                if (File.Exists(p)) v = Math.Max(v, File.GetLastWriteTimeUtc(p).Ticks);
            }
            catch { }
        }
        return v.ToString();
    }

    private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch { return; }
        if (msg == null) return;

        string reqId = msg["reqId"]?.ToString() ?? "";
        try
        {
            var data = await _app.HandleAsync(msg);
            Reply(reqId, true, data, null);
        }
        catch (Exception ex)
        {
            Reply(reqId, false, null, ex.Message);
        }
    }

    private void Reply(string reqId, bool ok, JsonNode data, string error)
    {
        if (string.IsNullOrEmpty(reqId)) return;
        var o = new JsonObject
        {
            ["type"] = ok ? "reply" : "error",
            ["reqId"] = reqId,
            ["data"] = data,
            ["message"] = error ?? ""
        };
        Push(o.ToJsonString());
    }

    private void OnSend(string json) => Push(json);

    private void OnToast(string kind, string message)
    {
        var o = new JsonObject { ["type"] = "toast", ["kind"] = kind, ["msg"] = message };
        Push(o.ToJsonString());
    }

    private void Push(string json)
    {
        if (IsDisposed) return;
        if (!_webReady)
        {
            // 页面还没加载完（界面刚起来就发了 state / models 这种请求）：先攒着，别丢。
            // 以前是直接 return —— 那一侧的 Promise 只能干等到自己超时（表现为「开程序白屏半天」）。
            lock (_pending) { if (_pending.Count < 500) _pending.Enqueue(json); }
            return;
        }
        try
        {
            if (InvokeRequired) BeginInvoke(new Action<string>(Push), json);
            else _web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    /// <summary>界面就绪：把启动阶段攒下的消息按原顺序补发</summary>
    private void FlushPending()
    {
        string[] items;
        lock (_pending) { items = _pending.ToArray(); _pending.Clear(); }
        foreach (var s in items) Push(s);
    }

    private void Restore()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ApplyTitleBarTheme();   // 托盘回来时统一一次标题栏颜色
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _app.Settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                try { _tray.ShowBalloonTip(2000, "CNM 还在后台跑", "服务不中断，外面的调用不受影响。双击托盘图标回来。", ToolTipIcon.Info); } catch { }
            }
            return;
        }

        _exiting = true;
        try
        {
            float scale = DeviceDpi / 96f;
            _app.Settings.WinWidth = (int)(ClientSize.Width / scale);
            _app.Settings.WinHeight = (int)(ClientSize.Height / scale);
        }
        catch { }
        _app.Shutdown();
        try { _tray.Visible = false; } catch { }

        base.OnFormClosing(e);
        if (!e.Cancel)
        {
            // 确保进程退干净（子进程已在 Shutdown 里结束）
            BeginInvoke(new Action(() => Environment.Exit(0)));
        }
    }
}
