using CNM.Core;
using CNM.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 生成 exe 图标：CNM.exe --make-icon [路径.ico]
        if (args.Contains("--make-icon"))
        {
            var idx = Array.IndexOf(args, "--make-icon");
            var path = idx + 1 < args.Length ? args[idx + 1] : "app.ico";
            Branding.SaveIcon(Path.GetFullPath(path));
            Console.WriteLine("icon written: " + Path.GetFullPath(path));
            return;
        }

        // 候选图形对比表：CNM.exe --logo-sheet [路径.png]
        if (args.Contains("--logo-sheet"))
        {
            var idx = Array.IndexOf(args, "--logo-sheet");
            var path = idx + 1 < args.Length ? args[idx + 1] : "logo-sheet.png";
            Branding.CandidateSheet(Path.GetFullPath(path));
            Console.WriteLine("sheet written: " + Path.GetFullPath(path));
            return;
        }

        // 从文件设图标：CNM.exe --set-icon <图片>   还原：CNM.exe --clear-icon
        if (args.Contains("--set-icon"))
        {
            var idx = Array.IndexOf(args, "--set-icon");
            if (idx + 1 >= args.Length) { Console.WriteLine("用法: --set-icon <图片路径>"); return; }
            try
            {
                using var bmp = Branding.Decode(File.ReadAllBytes(Path.GetFullPath(args[idx + 1])));
                Branding.SaveUserIcon(bmp);
                Console.WriteLine("icon set: " + Settings.UserIcoPath);
            }
            catch (Exception ex) { Console.WriteLine("失败: " + ex.Message); }
            return;
        }

        if (args.Contains("--clear-icon"))
        {
            Branding.ClearUserIcon();
            Console.WriteLine("icon cleared");
            return;
        }

        // 托盘菜单外观自检：CNM.exe --menu-preview <png> [--light]
        if (args.Contains("--menu-preview"))
        {
            var idx = Array.IndexOf(args, "--menu-preview");
            var path = Path.GetFullPath(idx + 1 < args.Length ? args[idx + 1] : "menu-preview.png");
            bool light = args.Contains("--light");
            ApplicationConfiguration.Initialize();

            var menu = TrayMenu.Create(out _, out _, out _);
            menu.Items[1].Text = light ? "启动服务" : "停止服务";   // 两种状态都看一眼
            TrayMenu.ApplyTheme(menu, !light);
            menu.Show(new Point(60, 60));
            Application.DoEvents();
            Thread.Sleep(260);
            Application.DoEvents();

            using (var bmp = new Bitmap(menu.Width, menu.Height))
            {
                Native.PrintWindow(menu.Handle, bmp);
                if (Native.IsBlank(bmp)) Native.DrawToBitmap(menu, bmp);
                Console.WriteLine((Native.IsBlank(bmp) ? "cap fail  " : "cap ok  ") + menu.Width + "x" + menu.Height);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            menu.Close();
            Console.WriteLine("menu preview: " + path);
            return;
        }

        // 无界面装引擎 / 修引擎（自动化用）：
        //   CNM.exe --install-engine [vulkan|cpu|rocm|cuda-12.4|cuda-13.4] [--engine-mirror <加速前缀>] [--engine-dir <目录>]
        //   CNM.exe --install-engine-zip <本地.zip>                        [--engine-dir <目录>]
        if (args.Contains("--install-engine") || args.Contains("--install-engine-zip"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            // 没有附加控制台（stdout 被重定向时）上面那行会失败、中文变 "?"：直接换掉标准输出流
            try
            {
                Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                    new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
            catch { }   // stdout 被重定向时这句会抛，忽略
            string Opt(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
            }

            bool fromZip = args.Contains("--install-engine-zip");
            var i0 = Array.IndexOf(args, fromZip ? "--install-engine-zip" : "--install-engine");
            var val = i0 >= 0 && i0 + 1 < args.Length && !args[i0 + 1].StartsWith("--") ? args[i0 + 1] : "";

            string variant = "", zip = "";
            if (fromZip || val.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip = Path.GetFullPath(val);
            else variant = val.Length > 0 ? val : "vulkan";

            var mirror = Opt("--engine-mirror");
            if (mirror.Length == 0) { try { mirror = Settings.Load().EngineMirror; } catch { } }
            var dir = Opt("--engine-dir");

            Environment.Exit(EngineInstaller.RunCliAsync(variant, zip, mirror, dir.Length > 0 ? Path.GetFullPath(dir) : null)
                .GetAwaiter().GetResult());
        }

        //   CNM.exe --upstream-test <地址> [key] [--proxy <代理>]        看看中转站通不通、有哪些模型
        //   CNM.exe --upstream-chat <地址> [key] [模型] [--proxy <代理>] 真发一条对话，看转发通不通
        if (args.Contains("--upstream-test") || args.Contains("--upstream-chat"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            bool chat = args.Contains("--upstream-chat");
            var i = Array.IndexOf(args, chat ? "--upstream-chat" : "--upstream-test");
            var url = i + 1 < args.Length ? args[i + 1] : "";
            var key = i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : "";
            var model = "";
            if (chat && i + 3 < args.Length && !args[i + 3].StartsWith("--")) model = args[i + 3];
            var pi = Array.IndexOf(args, "--proxy");
            var proxy = pi >= 0 && pi + 1 < args.Length ? args[pi + 1] : "";

            var hub = new UpstreamHub(new Settings());
            var cfg = new UpstreamCfg { Name = "test", BaseUrl = url, ApiKey = key, Proxy = proxy };
            var info = hub.RefreshAsync(cfg, CancellationToken.None).GetAwaiter().GetResult();
            if (info.Ok)
            {
                Console.WriteLine(proxy.Length > 0 ? $"通了（走代理 {proxy}）：{info.Root}" : $"通了（直连）：{info.Root}");
                Console.WriteLine($"模型 {info.Models.Count} 个：");
                foreach (var m in info.Models.Take(50)) Console.WriteLine("  " + m);
            }
            else
            {
                Console.WriteLine("连不上：" + info.Error);
                Environment.Exit(1);
            }

            if (chat)
            {
                Console.WriteLine("--- 真发一条对话 ---");
                var (ok, detail) = hub.ChatProbeAsync(cfg, model, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(detail);
                Environment.Exit(ok ? 0 : 1);
            }
            Environment.Exit(0);
        }

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "CNM.SingleInstance.v1", out bool first);
        if (!first)
        {
            MessageBox.Show("CNM 已经在运行 —— 看右下角托盘图标。", "CNM",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var app = new App();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { app.Shutdown(); } catch { } };

        Application.Run(new MainForm(app));
    }
}
