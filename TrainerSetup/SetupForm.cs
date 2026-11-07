using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TrainerSetup
{
    internal sealed class SetupForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(23, 24, 27);
        private static readonly Color Panel = Color.FromArgb(32, 34, 38);
        private static readonly Color Field = Color.FromArgb(43, 46, 51);
        private static readonly Color Ink = Color.FromArgb(232, 232, 234);
        private static readonly Color Dim = Color.FromArgb(154, 156, 163);
        private static readonly Color Accent = Color.FromArgb(232, 163, 61);
        private static readonly Color OkColor = Color.FromArgb(94, 190, 126);
        private static readonly Color ErrColor = Color.FromArgb(224, 108, 90);

        private TextBox _txtDir;
        private Label _lblStatus;
        private TextBox _log;
        private Button _btnInstall;
        private Button _btnUninstall;
        private Button _btnLaunch;
        private Panel _verCard;
        private Label _lblGameVer;
        private Label _lblCompat;
        private Label _lblVerified;
        private string _probedDir;
        private GameProbe _probe;

        internal SetupForm()
        {
            Text = "Crossfire Legion 修改器  v" + Program.TrainerVersion;
            ClientSize = new Size(640, 592);
            MinimumSize = new Size(620, 592);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Bg;
            ForeColor = Ink;
            Font = PickFont(9f);
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildUi();
            Shown += (s, e) => AutoDetect(true);
        }

        private static Font PickFont(float size, bool bold = false)
        {
            string[] preferred = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (string name in preferred)
            {
                try
                {
                    var f = new Font(name, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, bold ? FontStyle.Bold : FontStyle.Regular);
        }

        private void BuildUi()
        {
            var head = new Label
            {
                Text = "Crossfire Legion  修改器",
                Font = PickFont(17f, true),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(22, 18),
            };
            Controls.Add(head);

            Controls.Add(new Label
            {
                Text = "单人 / AI 遭遇战可用  ·  重装游戏后重新运行本程序即可恢复",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 52),
            });

            Controls.Add(new Label
            {
                Text = "v" + Program.TrainerVersion,
                ForeColor = Accent,
                Font = PickFont(8.5f, true),
                AutoSize = true,
                Location = new Point(578, 26),
            });

            Controls.Add(new Label
            {
                Text = "游戏目录",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 92),
            });

            _txtDir = new TextBox
            {
                Location = new Point(24, 112),
                Size = new Size(430, 26),
                BackColor = Field,
                ForeColor = Ink,
                BorderStyle = BorderStyle.FixedSingle,
                Font = PickFont(9f),
            };
            _txtDir.TextChanged += (s, e) => RefreshStatus();
            Controls.Add(_txtDir);

            var browse = MakeButton("浏览…", false, new Point(462, 111), new Size(72, 28));
            browse.Click += (s, e) => Browse();
            Controls.Add(browse);

            var detect = MakeButton("自动检测", false, new Point(540, 111), new Size(76, 28));
            detect.Click += (s, e) => AutoDetect(false);
            Controls.Add(detect);

            _lblStatus = new Label
            {
                Text = "检测中…",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 150),
                Font = PickFont(9f, true),
            };
            Controls.Add(_lblStatus);

            _btnInstall = MakeButton("安装 / 修复", true, new Point(24, 180), new Size(190, 40));
            _btnInstall.Font = PickFont(10f, true);
            _btnInstall.Click += (s, e) => DoInstall();
            Controls.Add(_btnInstall);

            _btnUninstall = MakeButton("卸载", false, new Point(224, 180), new Size(120, 40));
            _btnUninstall.Click += (s, e) => DoUninstall();
            Controls.Add(_btnUninstall);

            _btnLaunch = MakeButton("启动游戏", false, new Point(354, 180), new Size(120, 40));
            _btnLaunch.Click += (s, e) => DoLaunch();
            Controls.Add(_btnLaunch);

            // ---- game version / compatibility card ----
            _verCard = new Panel
            {
                Location = new Point(24, 232),
                Size = new Size(592, 92),
                BackColor = Panel,
            };
            Controls.Add(_verCard);

            _verCard.Controls.Add(new Label
            {
                Text = "游戏版本",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(12, 10),
            });

            _lblGameVer = new Label
            {
                Text = "读取中…",
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(12, 29),
            };
            _verCard.Controls.Add(_lblGameVer);

            _lblCompat = new Label
            {
                Text = "适配状态检测中…",
                ForeColor = Dim,
                AutoSize = true,
                Font = PickFont(9f, true),
                Location = new Point(12, 50),
            };
            _verCard.Controls.Add(_lblCompat);

            _lblVerified = new Label
            {
                Text = "验证版本 build " + GameVersion.VerifiedBuildId + "  ·  " + GameVersion.VerifiedDate + "  ·  说明内置于本程序",
                ForeColor = Color.FromArgb(120, 122, 129),
                AutoSize = true,
                Location = new Point(12, 70),
            };
            _verCard.Controls.Add(_lblVerified);

            var btnDoc = MakeButton("适配说明…", false, new Point(470, 10), new Size(110, 26));
            btnDoc.Click += (s, e) => ShowDoc();
            _verCard.Controls.Add(btnDoc);

            Controls.Add(new Label
            {
                Text = "运行日志",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 338),
            });

            _log = new TextBox
            {
                Location = new Point(24, 356),
                Size = new Size(592, 148),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Panel,
                ForeColor = Dim,
                BorderStyle = BorderStyle.FixedSingle,
                Font = PickFont(8.5f),
                WordWrap = true,
            };
            Controls.Add(_log);

            Controls.Add(new Label
            {
                Text = "面板  10 个开关 + 速度滑条，全部可以直接用鼠标点（无需快捷键）",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 516),
            });

            Controls.Add(new Label
            {
                Text = "　　　F4 显示 / 隐藏面板；其余功能全部用鼠标点，不占用游戏的其他按键",
                ForeColor = Dim,
                AutoSize = true,
                Location = new Point(24, 536),
            });

            Controls.Add(new Label
            {
                Text = "卸载 = 删除 BepInEx / winhttp.dll / doorstop_config.ini / zzz_trainer.lua",
                ForeColor = Color.FromArgb(120, 122, 129),
                AutoSize = true,
                Location = new Point(24, 560),
            });

            Log("安装器就绪。本程序会把修改器写入游戏目录，原版游戏文件不做任何修改。");
        }

        private Button MakeButton(string text, bool accent, Point loc, Size size)
        {
            var b = new Button
            {
                Text = text,
                Location = loc,
                Size = size,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Accent : Field,
                ForeColor = accent ? Color.FromArgb(28, 24, 16) : Ink,
                Font = PickFont(9f, accent),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = accent
                ? Color.FromArgb(244, 178, 84)
                : Color.FromArgb(56, 60, 66);
            b.FlatAppearance.MouseDownBackColor = accent
                ? Color.FromArgb(210, 145, 50)
                : Color.FromArgb(38, 41, 46);
            return b;
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 Crossfire Legion 游戏目录（里面应有 Crossfire_Legion.exe）";
                dlg.ShowNewFolderButton = false;
                string current = _txtDir.Text.Trim();
                if (Directory.Exists(current)) dlg.SelectedPath = current;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // The user may pick either the game root or a parent folder.
                    string picked = dlg.SelectedPath;
                    _txtDir.Text = SteamFinder.IsGameDir(picked) ? picked : (FindUnder(picked) ?? picked);
                    RefreshStatus();
                }
            }
        }

        /// <summary>One level deep search, in case the user selected steamapps\common.</summary>
        private static string FindUnder(string dir)
        {
            try
            {
                if (SteamFinder.IsGameDir(dir)) return dir;
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (SteamFinder.IsGameDir(sub)) return sub;
                }
            }
            catch { }
            return null;
        }

        private void AutoDetect(bool silentIfMissing)
        {
            Cursor = Cursors.WaitCursor;
            string found = null;
            try { found = SteamFinder.FindGameDir(); }
            catch { }
            Cursor = Cursors.Default;

            if (found != null)
            {
                _txtDir.Text = found;
                RefreshStatus();
                if (!silentIfMissing) Log("已找到游戏: " + found);
            }
            else
            {
                if (!silentIfMissing)
                {
                    Log("未能自动找到游戏（可能尚未安装，或装在非 Steam 库目录）。请点“浏览…”手动选择。");
                    MessageBox.Show(this,
                        "没有自动找到 Crossfire Legion。\n\n如果游戏已经安装，请点“浏览…”手动选择游戏目录（里面应有 Crossfire_Legion.exe）。",
                        "未检测到游戏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                RefreshStatus();
            }
        }

        private void RefreshStatus()
        {
            string dir = _txtDir.Text.Trim();
            if (string.IsNullOrEmpty(dir))
            {
                SetStatus("请选择游戏目录", Dim);
                return;
            }

            if (!SteamFinder.IsGameDir(dir))
            {
                SetStatus("▲ 这个目录里没有 Crossfire_Legion.exe", ErrColor);
                return;
            }

            var st = Installer.GetStatus(dir);
            switch (st.State)
            {
                case "installed": SetStatus("● " + st.DetailCn, OkColor); break;
                case "partial": SetStatus("● " + st.DetailCn, Accent); break;
                default: SetStatus("○ " + st.DetailCn, Dim); break;
            }

            UpdateVersionBlock(dir);
        }

        /// <summary>Shows the detected build and whether it is the one this trainer was verified on.</summary>
        private void UpdateVersionBlock(string dir)
        {
            if (!string.Equals(dir, _probedDir, StringComparison.OrdinalIgnoreCase))
            {
                _probedDir = dir;
                _probe = null;
                Cursor = Cursors.WaitCursor;
                try { _probe = GameVersion.Probe(dir); }
                catch { _probe = null; }
                finally { Cursor = Cursors.Default; }
            }

            GameProbe p = _probe;
            if (p == null || !p.GameFound)
            {
                _lblGameVer.Text = "—";
                _lblCompat.Text = "未能读取游戏版本";
                _lblCompat.ForeColor = Dim;
                return;
            }

            string ver = p.ProductVersion.Length > 0 ? p.ProductVersion : "（版本号不可读）";
            string build = p.BuildId.Length > 0 ? "build " + p.BuildId : "build 不可读";
            _lblGameVer.Text = ver + "   ·   " + build;

            _lblCompat.Text = GameVersion.ShortVerdict(p);
            if (!p.Layout01) _lblCompat.ForeColor = ErrColor;
            else if (p.VersionExact && p.Broken.Count == 0) _lblCompat.ForeColor = OkColor;
            else if (p.VersionExact) _lblCompat.ForeColor = Accent;
            else _lblCompat.ForeColor = Accent;
        }

        /// <summary>Full compatibility note, including which features fail on a different build.</summary>
        private void ShowDoc()
        {
            string dir = _txtDir.Text.Trim();
            if (_probe == null || !string.Equals(dir, _probedDir, StringComparison.OrdinalIgnoreCase))
            {
                Cursor = Cursors.WaitCursor;
                try { _probe = GameVersion.Probe(dir); _probedDir = dir; } catch { _probe = null; }
                finally { Cursor = Cursors.Default; }
            }

            string text = GameVersion.DocText(_probe);

            using (var dlg = new Form())
            {
                dlg.Text = "适配说明 — Crossfire Legion 修改器 v" + Program.TrainerVersion;
                dlg.ClientSize = new Size(680, 560);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.BackColor = Bg;
                dlg.ForeColor = Ink;
                dlg.Font = PickFont(9f);

                var box = new TextBox
                {
                    Location = new Point(16, 16),
                    Size = new Size(648, 490),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = Panel,
                    ForeColor = Ink,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = PickFont(9f),
                    Text = text,
                    WordWrap = false,
                };
                dlg.Controls.Add(box);

                var copy = new Button
                {
                    Text = "复制全文",
                    Location = new Point(16, 516),
                    Size = new Size(110, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Field,
                    ForeColor = Ink,
                    TabStop = false,
                };
                copy.FlatAppearance.BorderSize = 0;
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(text); copy.Text = "已复制"; }
                    catch { copy.Text = "复制失败"; }
                };
                dlg.Controls.Add(copy);

                var ok = new Button
                {
                    Text = "关闭",
                    Location = new Point(554, 516),
                    Size = new Size(110, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Accent,
                    ForeColor = Color.FromArgb(28, 24, 16),
                    TabStop = false,
                    DialogResult = DialogResult.OK,
                };
                ok.FlatAppearance.BorderSize = 0;
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;
                dlg.CancelButton = ok;

                // the note is read-only: never let it open with everything selected
                dlg.Shown += (s, e) => { box.Select(0, 0); ok.Focus(); };

                dlg.ShowDialog(this);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void DoInstall()
        {
            string dir = _txtDir.Text.Trim();
            if (!SteamFinder.IsGameDir(dir))
            {
                MessageBox.Show(this, "请先选择正确的游戏目录（里面应有 Crossfire_Legion.exe）。",
                    "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor = Cursors.WaitCursor;
            _btnInstall.Enabled = false;
            OpResult r;
            try { r = Installer.Install(dir, Log); }
            finally { _btnInstall.Enabled = true; Cursor = Cursors.Default; }

            RefreshStatus();
            Log(r.Ok ? "✔ " + r.Message : "✘ " + r.Message);

            MessageBox.Show(this, r.Message, r.Ok ? "安装完成" : "安装失败",
                MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        private void DoUninstall()
        {
            string dir = _txtDir.Text.Trim();
            if (!SteamFinder.IsGameDir(dir))
            {
                MessageBox.Show(this, "请先选择正确的游戏目录。", "目录无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(this,
                "将从游戏目录移除修改器：\n\n• BepInEx 文件夹\n• winhttp.dll\n• doorstop_config.ini\n• zzz_trainer.lua\n\n游戏本体不受影响。继续吗？",
                "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            Cursor = Cursors.WaitCursor;
            _btnUninstall.Enabled = false;
            OpResult r;
            try { r = Installer.Uninstall(dir, Log); }
            finally { _btnUninstall.Enabled = true; Cursor = Cursors.Default; }

            RefreshStatus();
            Log(r.Ok ? "✔ " + r.Message : "✘ " + r.Message);
            MessageBox.Show(this, r.Message, r.Ok ? "已卸载" : "卸载失败",
                MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DoLaunch()
        {
            string dir = _txtDir.Text.Trim();
            var r = Installer.Launch(dir);
            Log(r.Ok ? "✔ " + r.Message : "✘ " + r.Message);
            if (!r.Ok)
                MessageBox.Show(this, r.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "]  " + msg + Environment.NewLine;
            _log.AppendText(line);
        }
    }
}
