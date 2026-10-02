// MainForm.cs - 光标工坊主界面 + 托盘 + 守护定时器
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace CursorStudio
{
    public class MainForm : Form
    {
        FlowLayoutPanel cards;
        Dictionary<string, Panel> cardById = new Dictionary<string, Panel>();
        ComboBox roleCombo;
        CheckBox chkAutostart, chkWatchdog, chkRestoreOnExit;
        Label status;
        NotifyIcon tray;
        Timer watchdogTimer;
        int tickCount;
        bool reallyExit;

        public MainForm()
        {
            CursorCore.EnsureInit();
            BuildUi();

            // 持久化方案随登录自动生效（注册表本身已持久化，这里补齐槽位）
            CursorCore.ApplyOnStartup();

            BuildCards();
            SyncFromState();

            watchdogTimer = new Timer();
            watchdogTimer.Interval = 3000;
            watchdogTimer.Tick += delegate
            {
                tickCount++;
                if (CursorCore.Current.Watchdog)
                {
                    CursorCore.WatchdogTick();
                    if (tickCount % 10 == 0) CursorCore.FullReapply();   // ~30s 兜底
                }
            };
            watchdogTimer.Start();
        }

        // ---------------- 界面构建 ----------------

        void BuildUi()
        {
            Text = "光标工坊 CursorStudio";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 560);
            Font = new Font("Microsoft YaHei UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(CursorCore.ExePath); } catch { }

            Label l1 = new Label();
            l1.Text = "内置方案（点击应用）";
            l1.Location = new Point(16, 14);
            l1.AutoSize = true;
            l1.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            Controls.Add(l1);

            cards = new FlowLayoutPanel();
            cards.Location = new Point(16, 40);
            cards.Size = new Size(648, 170);
            cards.AutoScroll = true;
            cards.BorderStyle = BorderStyle.None;
            Controls.Add(cards);

            Label l2 = new Label();
            l2.Text = "自定义：把任意图片变成光标";
            l2.Location = new Point(16, 226);
            l2.AutoSize = true;
            l2.Font = l1.Font;
            Controls.Add(l2);

            Label l3 = new Label();
            l3.Text = "应用为：";
            l3.Location = new Point(16, 258);
            l3.AutoSize = true;
            Controls.Add(l3);

            roleCombo = new ComboBox();
            roleCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            roleCombo.Location = new Point(72, 254);
            roleCombo.Width = 170;
            foreach (string role in CursorCore.Roles)
                roleCombo.Items.Add(new RoleItem(role, CursorCore.RoleNames[role]));
            roleCombo.SelectedIndex = 0;
            Controls.Add(roleCombo);

            Button btnUpload = new Button();
            btnUpload.Text = "上传 PNG / 图片并应用…";
            btnUpload.Location = new Point(254, 252);
            btnUpload.Size = new Size(180, 28);
            btnUpload.Click += OnUpload;
            Controls.Add(btnUpload);

            Button btnEditor = new Button();
            btnEditor.Text = "自定义光标编辑器…";
            btnEditor.Location = new Point(452, 252);
            btnEditor.Size = new Size(180, 28);
            btnEditor.Click += delegate
            {
                using (EditorForm ef = new EditorForm())
                {
                    ef.Changed = delegate { BuildCards(); SyncFromState(); };
                    ef.ShowDialog(this);
                }
                BuildCards();
                SyncFromState();
            };
            Controls.Add(btnEditor);

            Button btnRestore = new Button();
            btnRestore.Text = "恢复系统默认光标";
            btnRestore.Location = new Point(16, 286);
            btnRestore.Size = new Size(220, 28);
            btnRestore.Click += delegate
            {
                CursorCore.RestoreDefault(true);
                SyncFromState();
                UpdateStatus("已恢复系统默认光标。");
            };
            Controls.Add(btnRestore);

            chkAutostart = MakeCheck("开机自启（登录后自动应用当前方案）", new Point(16, 322));
            chkAutostart.CheckedChanged += chkAutostart_CheckedChanged_proxy;
            chkWatchdog = MakeCheck("防还原守护（Windows 11 会话中途还原光标时自动重新应用，推荐开启）", new Point(16, 348));
            chkWatchdog.CheckedChanged += chkWatchdog_CheckedChanged_proxy;
            chkRestoreOnExit = MakeCheck("退出程序时恢复系统默认光标", new Point(16, 374));
            chkRestoreOnExit.CheckedChanged += chkRestoreOnExit_CheckedChanged_proxy;

            status = new Label();
            status.Location = new Point(16, 512);
            status.AutoSize = true;
            status.ForeColor = Color.FromArgb(64, 64, 64);
            Controls.Add(status);

            BuildTray();
        }

        CheckBox MakeCheck(string text, Point pos)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = pos;
            Controls.Add(c);
            return c;
        }

        void BuildCards()
        {
            // 释放旧卡片的预览图，避免反复重建时累积 GDI 句柄
            foreach (Control c in cards.Controls)
                foreach (Control c2 in c.Controls)
                {
                    PictureBox pb = c2 as PictureBox;
                    if (pb != null && pb.Image != null) pb.Image.Dispose();
                }
            cards.Controls.Clear();
            cardById.Clear();
            foreach (KeyValuePair<string, string> kv in CursorCore.GetSchemes())
            {
                Panel card = new Panel();
                card.Size = new Size(100, 152);
                card.BorderStyle = BorderStyle.FixedSingle;
                card.BackColor = Color.White;
                card.Cursor = Cursors.Hand;
                card.Tag = kv.Key;

                PictureBox pb = new PictureBox();
                pb.Size = new Size(96, 96);
                pb.Location = new Point(1, 1);
                pb.SizeMode = PictureBoxSizeMode.Zoom;
                pb.BackColor = Color.FromArgb(246, 246, 246);
                string preview = Path.Combine(CursorCore.SchemeDir(kv.Key), "preview.png");
                if (File.Exists(preview))
                {
                    try { pb.Image = Image.FromFile(preview); } catch { }
                }
                card.Controls.Add(pb);

                Label name = new Label();
                name.Text = kv.Value;
                name.Dock = DockStyle.Bottom;
                name.TextAlign = ContentAlignment.MiddleCenter;
                name.Height = 26;
                name.BackColor = Color.Transparent;
                card.Controls.Add(name);

                EventHandler click = delegate
                {
                    string id = (string)card.Tag;
                    if (CursorCore.ApplyScheme(id, true))
                    {
                        SyncFromState();
                        UpdateStatus("已应用方案：" + CursorCore.GetSchemes()[id]);
                    }
                    else
                    {
                        UpdateStatus("该方案还没有「正常选择」光标，暂时无法整体应用。");
                    }
                };
                card.Click += click;
                pb.Click += click;
                name.Click += click;

                cards.Controls.Add(card);
                cardById[kv.Key] = card;
            }
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(CursorCore.ExePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "光标工坊 CursorStudio";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowWindow(); });
            menu.Items.Add("恢复默认光标", null, delegate
            {
                CursorCore.RestoreDefault(true);
                SyncFromState();
                UpdateStatus("已恢复系统默认光标。");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { reallyExit = true; if (CursorCore.Current.RestoreOnExit) CursorCore.RestoreDefault(true); Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        // ---------------- 状态同步 ----------------

        void SyncFromState()
        {
            Settings s = CursorCore.Current;
            chkAutostart.CheckedChanged -= chkAutostart_CheckedChanged_proxy;
            chkWatchdog.CheckedChanged -= chkWatchdog_CheckedChanged_proxy;
            chkRestoreOnExit.CheckedChanged -= chkRestoreOnExit_CheckedChanged_proxy;

            chkAutostart.Checked = s.Autostart || CursorCore.GetAutostart();
            chkWatchdog.Checked = s.Watchdog;
            chkRestoreOnExit.Checked = s.RestoreOnExit;

            chkAutostart.CheckedChanged += chkAutostart_CheckedChanged_proxy;
            chkWatchdog.CheckedChanged += chkWatchdog_CheckedChanged_proxy;
            chkRestoreOnExit.CheckedChanged += chkRestoreOnExit_CheckedChanged_proxy;

            string current = s.CurrentScheme;
            foreach (KeyValuePair<string, Panel> kv in cardById)
            {
                bool active = kv.Key == current;
                kv.Value.BackColor = active ? Color.FromArgb(219, 236, 254) : Color.White;
                kv.Value.BorderStyle = active ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }
            UpdateStatus(null);
        }

        void UpdateStatus(string extra)
        {
            Settings s = CursorCore.Current;
            string schemeDesc;
            if (string.IsNullOrEmpty(s.CurrentScheme)) schemeDesc = "系统默认";
            else if (s.CurrentScheme == "custom")
            {
                string roleName;
                schemeDesc = CursorCore.RoleNames.TryGetValue(s.CustomRole, out roleName)
                    ? "自定义 - " + roleName : "自定义";
            }
            else
            {
                string n;
                if (CursorCore.GetSchemes().TryGetValue(s.CurrentScheme, out n)) schemeDesc = n;
                else schemeDesc = s.CurrentScheme;
            }
            string text = "当前：" + schemeDesc + "　|　守护：" + (s.Watchdog ? "运行中" : "已关闭") + "　|　开机自启：" + (CursorCore.GetAutostart() ? "已启用" : "未启用");
            if (!string.IsNullOrEmpty(extra)) text = extra + "　" + text;
            status.Text = text;
        }

        void OnUpload(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*";
                dlg.Title = "选择一张图片作为光标";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    RoleItem item = (RoleItem)roleCombo.SelectedItem;
                    string dest = Path.Combine(CursorCore.CustomDir, "custom_" + item.Role + ".cur");
                    CursorCore.ConvertPngToCur(dlg.FileName, item.Role, dest);
                    CursorCore.ApplyCustom(dest, item.Role, true);
                    SyncFromState();
                    UpdateStatus("已把图片应用为「" + item.Label + "」光标。");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "转换失败：" + ex.Message, "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // 代理事件（便于 SyncFromState 时挂/摘事件避免递归触发）
        void chkAutostart_CheckedChanged_proxy(object sender, EventArgs e)
        {
            CursorCore.Current.Autostart = chkAutostart.Checked;
            try { CursorCore.SetAutostart(chkAutostart.Checked); } catch { }
            CursorCore.SaveSettings();
            UpdateStatus(null);
        }
        void chkWatchdog_CheckedChanged_proxy(object sender, EventArgs e)
        {
            CursorCore.Current.Watchdog = chkWatchdog.Checked;
            CursorCore.SaveSettings();
            UpdateStatus(null);
        }
        void chkRestoreOnExit_CheckedChanged_proxy(object sender, EventArgs e)
        {
            CursorCore.Current.RestoreOnExit = chkRestoreOnExit.Checked;
            CursorCore.SaveSettings();
        }

        class RoleItem
        {
            public string Role, Label;
            public RoleItem(string role, string label) { Role = role; Label = label; }
            public override string ToString() { return Label; }
        }
    }
}
