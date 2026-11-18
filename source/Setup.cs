using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Setup
{
    internal const string Plugin = "deepseek@deepseek-for-codex";
    private static readonly string[,] Package =
    {
        { "marketplace.json", @".agents\plugins\marketplace.json" },
        { "plugin.json", @"plugins\deepseek\plugin.json" },
        { "mcp.json", @"plugins\deepseek\mcp.json" },
        { "compat.json", @"plugins\deepseek\.codex-plugin\plugin.json" },
        { "compat-mcp.json", @"plugins\deepseek\.mcp.json" },
        { "SKILL.md", @"plugins\deepseek\skills\ask-deepseek\SKILL.md" },
        { "openai.yaml", @"plugins\deepseek\skills\ask-deepseek\agents\openai.yaml" },
        { "DeepSeekMcp.exe", @"plugins\deepseek\bin\DeepSeekMcp.exe" }
    };

    internal static void Install(string root, string newKey, string codex)
    {
        using (var mutex = new Mutex(false, @"Local\DeepSeekForCodex.Setup"))
        {
            try
            {
                if (!mutex.WaitOne(0))
                    throw new UserError("另一个安装程序正在运行，请稍后再试。");
            }
            catch (AbandonedMutexException) { }

            try
            {
                var credential = new Credential(root);
                if (newKey.Length > 0)
                    Credential.Validate(newKey);
                else
                    credential.Load();

                string marketplace = Path.Combine(root, "marketplace");
                for (int i = 0; i < Package.GetLength(0); i++)
                {
                    using (var resource = typeof(Setup).Assembly.GetManifestResourceStream(Package[i, 0]))
                    using (var buffer = new MemoryStream())
                    {
                        resource.CopyTo(buffer);
                        LocalFiles.WriteAtomically(Path.Combine(marketplace, Package[i, 1]), buffer.ToArray());
                    }
                }

                // Use the marketplace commands so Codex owns its configuration and cache.
                RunCodex(codex, marketplace, "plugin marketplace add \"" + marketplace + "\" --json", "注册本地插件源");
                RunCodex(codex, marketplace, "plugin add " + Plugin + " --json", "安装插件");
                if (newKey.Length > 0)
                    credential.Save(newKey);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    internal static string FindCodex()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(local))
        {
            var app = new DirectoryInfo(local).GetDirectories().OrderByDescending(dir => dir.LastWriteTimeUtc)
                .Select(dir => Path.Combine(dir.FullName, "codex.exe")).FirstOrDefault(File.Exists);
            if (app != null)
                return app;
        }

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (directory.Length == 0 || !Path.IsPathRooted(directory))
                continue;
            string candidate = Path.Combine(directory, "codex.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new UserError("找不到 Codex。请先打开 Windows 版 Codex 完成初始化，或在安装窗口中选择 codex.exe。");
    }

    private static void RunCodex(string path, string cwd, string arguments, string step)
    {
        var options = new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using (var process = Process.Start(options))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                }
                throw new UserError(step + "超时。请关闭 Codex 后重试。");
            }
            if (process.ExitCode != 0)
            {
                Task.WaitAll(output, error);
                string detail = String.IsNullOrWhiteSpace(error.Result) ? output.Result : error.Result;
                detail = detail.Trim();
                if (detail.Length > 1300)
                    detail = detail.Substring(0, 1300) + "…";
                throw new UserError(step + "失败（Codex 退出码 " + process.ExitCode + "）。\r\n\r\n" + detail);
            }
        }
    }
}

internal sealed class SetupWindow : Form
{
    private readonly TextBox keyBox = new TextBox();
    private readonly TextBox codexBox = new TextBox();
    private readonly Label status = new Label();
    private readonly Button browse = new Button();
    private readonly Button verify = new Button();
    private readonly Button install = new Button();
    private bool busy;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWindow());
    }

    private SetupWindow()
    {
        Text = "DeepSeek · Codex 插件安装";
        ClientSize = new Size(610, 352);
        MinimumSize = new Size(565, 365);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.White;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 12),
            ColumnCount = 1,
            RowCount = 8
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(panel);

        var heading = Caption("在 Codex 中使用 DeepSeek", 8);
        heading.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
        panel.Controls.Add(heading, 0, 0);
        panel.Controls.Add(Caption("插件调用使用你的 DeepSeek API 账户，并返回每次调用的 token 用量。", 18), 0, 1);

        panel.Controls.Add(Caption("DeepSeek API 密钥", 5), 0, 2);
        keyBox.Dock = DockStyle.Top;
        keyBox.Margin = new Padding(0, 0, 0, 5);
        keyBox.UseSystemPasswordChar = true;
        keyBox.MaxLength = 512;
        keyBox.AccessibleName = "DeepSeek API 密钥";
        panel.Controls.Add(keyBox, 0, 3);
        bool saved = new Credential(LocalFiles.Root).Exists;
        var hint = Caption(saved ? "已保存密钥。留空可沿用，填写新密钥可覆盖。" : "密钥由 Windows 加密并保存到当前账户。", 13);
        hint.ForeColor = Color.FromArgb(76, 85, 99);
        panel.Controls.Add(hint, 0, 4);
        panel.Controls.Add(CodexPicker(), 0, 5);

        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0, 10, 0, 6);
        status.ForeColor = hint.ForeColor;
        status.Text = "安装后，重新启动 Codex 并在新聊天中明确请求使用 DeepSeek。";
        panel.Controls.Add(status, 0, 6);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        install.Text = "保存并安装";
        verify.Text = "验证密钥";
        install.Width = 116;
        verify.Width = 100;
        install.Height = verify.Height = 34;
        actions.Controls.Add(install);
        actions.Controls.Add(verify);
        panel.Controls.Add(actions, 0, 7);

        AcceptButton = install;
        install.Click += InstallClicked;
        verify.Click += VerifyClicked;
        FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
    }

    private Control CodexPicker()
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Top, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var label = Caption("Codex 程序路径", 5);
        row.Controls.Add(label, 0, 0);
        row.SetColumnSpan(label, 2);
        codexBox.Dock = DockStyle.Top;
        codexBox.ReadOnly = true;
        codexBox.AccessibleName = "Codex 程序路径";
        codexBox.Margin = new Padding(0, 4, 8, 0);
        try { codexBox.Text = Setup.FindCodex(); }
        catch (UserError) { }
        row.Controls.Add(codexBox, 0, 1);

        browse.Text = "浏览…";
        browse.AutoSize = true;
        browse.Margin = new Padding(0);
        browse.Click += (sender, args) =>
        {
            using (var dialog = new OpenFileDialog { Filter = "Codex 程序 (codex.exe)|codex.exe", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    codexBox.Text = dialog.FileName;
            }
        };
        row.Controls.Add(browse, 1, 1);
        return row;
    }

    private static Label Caption(string text, int gap)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, gap) };
    }

    private async void VerifyClicked(object sender, EventArgs args)
    {
        await RunAsync("正在联系 DeepSeek…", async () =>
        {
            var entered = keyBox.Text.Trim();
            var key = entered.Length > 0 ? entered : new Credential(LocalFiles.Root).Load();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                try { await new DeepSeekApi(key).VerifyAsync(timeout.Token); }
                catch (OperationCanceledException) { throw new UserError("验证密钥超时。请检查网络连接后重试。"); }
            }
            status.Text = "密钥验证通过。验证只查询模型列表。";
        });
    }

    private async void InstallClicked(object sender, EventArgs args)
    {
        await RunAsync("正在安装 Codex 插件…", async () =>
        {
            string key = keyBox.Text.Trim();
            string codex = codexBox.Text;
            if (!File.Exists(codex))
                throw new UserError("请选择已安装的 codex.exe。");
            await Task.Run(() => Setup.Install(LocalFiles.Root, key, codex));
            keyBox.Clear();
            status.Text = "安装完成。请完全退出并重新打开 Codex，然后开启新聊天。";
            MessageBox.Show(this, "打开新聊天并输入：请用 DeepSeek 回答「你好」，并显示 token 用量。",
                "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private async Task RunAsync(string working, Func<Task> action)
    {
        busy = true;
        install.Enabled = verify.Enabled = browse.Enabled = keyBox.Enabled = false;
        status.Text = working;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            string detail = ex is UserError ? ex.Message : "操作失败（" + ex.GetType().Name + "）。请检查 Codex 版本和当前账户的目录权限。";
            status.Text = "操作失败。请按提示处理后重试。";
            MessageBox.Show(this, detail, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            install.Enabled = verify.Enabled = browse.Enabled = keyBox.Enabled = true;
        }
    }
}
