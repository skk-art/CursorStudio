// EditorForm.cs - 自定义光标编辑器
// 用途：选一张图片 → 调整输出尺寸与热点 → 立即应用到某角色，或保存为自定义方案（主页出现同名卡片）。
// 同名方案再次保存即覆盖对应角色——这就是"修改已有方案"的路径。
// 自用工具，界面从简：左侧放大像素预览（单击设热点），右侧参数与操作。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CursorStudio
{
    public class EditorForm : Form
    {
        // 试戴用：把 .cur 加载为光标句柄（与 CursorCore 内的声明互不影响）
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadCursorFromFileW(string path);

        class DbPanel : Panel { public DbPanel() { DoubleBuffered = true; } }

        class RoleItem
        {
            public string Role, Label;
            public RoleItem(string role, string label) { Role = role; Label = label; }
            public override string ToString() { return Label; }
        }

        DbPanel canvas;          // 放大像素预览，单击设置热点
        PictureBox realSize;     // 1:1 实际大小预览
        Label lblSrc, status;
        RadioButton r32, r48, r64;
        ComboBox roleCombo, delCombo;
        NumericUpDown numX, numY;
        TextBox txtScheme;

        Image sourceImage;       // 原图（从内存流加载，避免锁住源文件）
        MemoryStream imgStream;
        Bitmap outBmp;           // 输出位图（outSize x outSize）
        int outSize = 32;
        int hotX, hotY;
        string srcPath = "";
        Cursor tryCursor;        // "试戴"用的光标，窗口关闭时还原
        bool syncingHotspot;

        // 保存/删除方案后通知主窗口刷新卡片
        public Action Changed;

        public EditorForm()
        {
            CursorCore.EnsureInit();
            BuildUi();
            ApplyRoleDefaultHotspot();
            UpdateOutput();
            UpdateStatus("先选择图片，然后调整尺寸与热点；同名保存即修改已有方案。");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cursor = Cursors.Default;
            if (tryCursor != null) { tryCursor.Dispose(); tryCursor = null; }
            base.OnFormClosed(e);
        }

        // ---------------- 界面构建 ----------------

        void BuildUi()
        {
            Text = "自定义光标编辑器 - 光标工坊";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 436);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label step1 = new Label();
            step1.Text = "第 1 步：选择一张图片（PNG / JPG / BMP / GIF 均可）";
            step1.Location = new Point(16, 14);
            step1.AutoSize = true;
            step1.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            Controls.Add(step1);

            Button btnPick = new Button();
            btnPick.Text = "选择图片…";
            btnPick.Location = new Point(16, 40);
            btnPick.Size = new Size(110, 28);
            btnPick.Click += OnPick;
            Controls.Add(btnPick);

            lblSrc = new Label();
            lblSrc.Location = new Point(136, 45);
            lblSrc.Size = new Size(468, 18);
            lblSrc.AutoEllipsis = true;
            lblSrc.ForeColor = Color.FromArgb(96, 96, 96);
            Controls.Add(lblSrc);

            canvas = new DbPanel();
            canvas.Location = new Point(16, 78);
            canvas.Size = new Size(208, 208);
            canvas.BorderStyle = BorderStyle.FixedSingle;
            canvas.Paint += Canvas_Paint;
            canvas.MouseClick += Canvas_MouseClick;
            Controls.Add(canvas);

            Label hint = new Label();
            hint.Text = "单击左图设置热点（红框 = 点击点）";
            hint.Location = new Point(16, 290);
            hint.AutoSize = true;
            hint.ForeColor = Color.FromArgb(96, 96, 96);
            Controls.Add(hint);

            realSize = new PictureBox();
            realSize.Location = new Point(16, 314);
            realSize.Size = new Size(72, 72);
            realSize.BorderStyle = BorderStyle.FixedSingle;
            realSize.BackColor = Color.White;
            realSize.Paint += RealSize_Paint;
            Controls.Add(realSize);

            Label rsLbl = new Label();
            rsLbl.Text = "实际大小";
            rsLbl.Location = new Point(94, 340);
            rsLbl.AutoSize = true;
            Controls.Add(rsLbl);

            // ---- 右列：参数与操作 ----

            Label szLbl = new Label();
            szLbl.Text = "输出尺寸：";
            szLbl.Location = new Point(248, 16);
            szLbl.AutoSize = true;
            Controls.Add(szLbl);

            r32 = SizeRadio("32", new Point(330, 12));
            r48 = SizeRadio("48", new Point(382, 12));
            r64 = SizeRadio("64", new Point(434, 12));
            r32.Checked = true;

            Label roleLbl = new Label();
            roleLbl.Text = "应用为角色：";
            roleLbl.Location = new Point(248, 48);
            roleLbl.AutoSize = true;
            Controls.Add(roleLbl);

            roleCombo = new ComboBox();
            roleCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            roleCombo.Location = new Point(344, 44);
            roleCombo.Width = 170;
            foreach (string role in CursorCore.Roles)
                roleCombo.Items.Add(new RoleItem(role, CursorCore.RoleNames[role]));
            roleCombo.SelectedIndex = 0;
            roleCombo.SelectedIndexChanged += delegate { ApplyRoleDefaultHotspot(); };
            Controls.Add(roleCombo);

            Label hotLbl = new Label();
            hotLbl.Text = "热点：";
            hotLbl.Location = new Point(248, 82);
            hotLbl.AutoSize = true;
            Controls.Add(hotLbl);

            Label xLbl = new Label(); xLbl.Text = "X"; xLbl.Location = new Point(300, 84); xLbl.AutoSize = true; Controls.Add(xLbl);
            numX = HotspotInput(new Point(318, 78));
            Label yLbl = new Label(); yLbl.Text = "Y"; yLbl.Location = new Point(388, 84); yLbl.AutoSize = true; Controls.Add(yLbl);
            numY = HotspotInput(new Point(406, 78));

            Button btnHotDefault = new Button();
            btnHotDefault.Text = "角色默认位置";
            btnHotDefault.Location = new Point(480, 76);
            btnHotDefault.Size = new Size(124, 26);
            btnHotDefault.Click += delegate { ApplyRoleDefaultHotspot(); };
            Controls.Add(btnHotDefault);

            Head("保存为自定义方案（主页出现同名卡片，同名再保存即修改）", new Point(248, 120));

            Label nameLbl = new Label();
            nameLbl.Text = "方案名：";
            nameLbl.Location = new Point(248, 152);
            nameLbl.AutoSize = true;
            Controls.Add(nameLbl);

            txtScheme = new TextBox();
            txtScheme.Location = new Point(312, 148);
            txtScheme.Width = 180;
            txtScheme.Text = "我的方案";
            Controls.Add(txtScheme);

            Button btnSave = new Button();
            btnSave.Text = "保存到方案";
            btnSave.Location = new Point(248, 182);
            btnSave.Size = new Size(244, 30);
            btnSave.Click += OnSave;
            Controls.Add(btnSave);

            Head("立即应用（不保存方案，只替换所选角色）", new Point(248, 226));

            Button btnApply = new Button();
            btnApply.Text = "应用到所选角色";
            btnApply.Location = new Point(248, 254);
            btnApply.Size = new Size(150, 30);
            btnApply.Click += OnApply;
            Controls.Add(btnApply);

            Button btnTry = new Button();
            btnTry.Text = "试戴预览";
            btnTry.Location = new Point(406, 254);
            btnTry.Size = new Size(86, 30);
            btnTry.Click += OnTry;
            Controls.Add(btnTry);

            Head("管理自定义方案", new Point(248, 300));

            delCombo = new ComboBox();
            delCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            delCombo.Location = new Point(248, 326);
            delCombo.Width = 160;
            Controls.Add(delCombo);

            Button btnDelete = new Button();
            btnDelete.Text = "删除方案";
            btnDelete.Location = new Point(416, 324);
            btnDelete.Size = new Size(76, 26);
            btnDelete.Click += OnDelete;
            Controls.Add(btnDelete);

            status = new Label();
            status.Location = new Point(16, 400);
            status.AutoSize = true;
            status.ForeColor = Color.FromArgb(64, 64, 64);
            Controls.Add(status);

            RefreshDelCombo();
        }

        RadioButton SizeRadio(string text, Point pos)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.Location = pos;
            r.AutoSize = true;
            r.CheckedChanged += SizeRadioChanged;
            Controls.Add(r);
            return r;
        }

        Label Head(string text, Point pos)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = pos;
            l.AutoSize = true;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Controls.Add(l);
            return l;
        }

        NumericUpDown HotspotInput(Point pos)
        {
            NumericUpDown n = new NumericUpDown();
            n.Location = pos;
            n.Size = new Size(56, 24);
            n.Minimum = 0;
            n.Maximum = outSize - 1;
            n.ValueChanged += delegate
            {
                if (syncingHotspot) return;
                if (n == numX) hotX = (int)n.Value; else hotY = (int)n.Value;
                canvas.Invalidate();
            };
            Controls.Add(n);
            return n;
        }

        // ---------------- 图片与输出 ----------------

        void OnPick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                dlg.Title = "选择一张图片";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    byte[] bytes = File.ReadAllBytes(dlg.FileName);
                    MemoryStream ms = new MemoryStream(bytes);
                    Image img = Image.FromStream(ms);   // 流需与 Image 同生命周期，保持打开避免锁源文件
                    if (sourceImage != null) sourceImage.Dispose();
                    if (imgStream != null) imgStream.Dispose();
                    sourceImage = img;
                    imgStream = ms;
                    srcPath = dlg.FileName;
                    lblSrc.Text = srcPath;
                    UpdateOutput();
                    UpdateStatus("图片已加载，调整右侧参数后保存或应用。");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法读取图片：" + ex.Message, "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void SizeRadioChanged(object sender, EventArgs e)
        {
            RadioButton r = (RadioButton)sender;
            if (!r.Checked) return;
            outSize = int.Parse(r.Text);
            UpdateOutput();
            ApplyRoleDefaultHotspot();
        }

        // 按当前原图与输出尺寸重新生成 outBmp，并夹紧热点
        void UpdateOutput()
        {
            Bitmap newBmp = null;
            if (sourceImage != null)
            {
                newBmp = new Bitmap(outSize, outSize, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(newBmp))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    double ratio = Math.Min(outSize / Math.Max(1.0, sourceImage.Width), outSize / Math.Max(1.0, sourceImage.Height));
                    int w = Math.Max(1, (int)Math.Round(sourceImage.Width * ratio));
                    int h = Math.Max(1, (int)Math.Round(sourceImage.Height * ratio));
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(sourceImage, new Rectangle((outSize - w) / 2, (outSize - h) / 2, w, h),
                            0, 0, sourceImage.Width, sourceImage.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            Bitmap old = outBmp;
            outBmp = newBmp;
            if (old != null) old.Dispose();

            numX.Maximum = outSize - 1;
            numY.Maximum = outSize - 1;
            hotX = Math.Max(0, Math.Min(outSize - 1, hotX));
            hotY = Math.Max(0, Math.Min(outSize - 1, hotY));
            SyncHotspotInputs();
            canvas.Invalidate();
            realSize.Invalidate();
        }

        void ApplyRoleDefaultHotspot()
        {
            RoleItem it = roleCombo.SelectedItem as RoleItem;
            if (it == null) return;
            int[] hp = CursorCore.DefaultHotspot(it.Role, outSize);
            hotX = hp[0];
            hotY = hp[1];
            SyncHotspotInputs();
            canvas.Invalidate();
        }

        void SyncHotspotInputs()
        {
            syncingHotspot = true;
            numX.Value = Math.Max(numX.Minimum, Math.Min(numX.Maximum, hotX));
            numY.Value = Math.Max(numY.Minimum, Math.Min(numY.Maximum, hotY));
            syncingHotspot = false;
        }

        int ZoomFactor()
        {
            int size = outBmp != null ? outBmp.Width : outSize;
            return Math.Max(1, Math.Min((canvas.ClientSize.Width - 8) / size, (canvas.ClientSize.Height - 8) / size));
        }

        void Canvas_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(244, 244, 244));
            if (outBmp == null)
            {
                string msg = "先选择一张图片";
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (Brush br = new SolidBrush(Color.FromArgb(150, 150, 150)))
                {
                    SizeF sz = g.MeasureString(msg, f);
                    g.DrawString(msg, f, br, (canvas.ClientSize.Width - sz.Width) / 2, (canvas.ClientSize.Height - sz.Height) / 2);
                }
                return;
            }
            int zoom = ZoomFactor();
            int disp = outBmp.Width * zoom;
            int offX = (canvas.ClientSize.Width - disp) / 2, offY = (canvas.ClientSize.Height - disp) / 2;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(outBmp, new Rectangle(offX, offY, disp, disp));
            using (Pen pen = new Pen(Color.Red))
            {
                g.DrawRectangle(pen, offX + hotX * zoom, offY + hotY * zoom, zoom, zoom);
            }
        }

        void Canvas_MouseClick(object sender, MouseEventArgs e)
        {
            if (outBmp == null || e.Button != MouseButtons.Left) return;
            int zoom = ZoomFactor();
            int disp = outBmp.Width * zoom;
            int offX = (canvas.ClientSize.Width - disp) / 2, offY = (canvas.ClientSize.Height - disp) / 2;
            int px = (e.X - offX) / zoom, py = (e.Y - offY) / zoom;
            if (px < 0 || py < 0 || px >= outBmp.Width || py >= outBmp.Height) return;
            hotX = px;
            hotY = py;
            SyncHotspotInputs();
            canvas.Invalidate();
        }

        void RealSize_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            if (outBmp == null) return;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(outBmp, (realSize.ClientSize.Width - outBmp.Width) / 2, (realSize.ClientSize.Height - outBmp.Height) / 2);
        }

        // ---------------- 操作 ----------------

        bool RequireImage()
        {
            if (sourceImage != null && File.Exists(srcPath)) return true;
            MessageBox.Show(this, "请先选择一张图片。", "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        void OnSave(object sender, EventArgs e)
        {
            if (!RequireImage()) return;
            RoleItem it = (RoleItem)roleCombo.SelectedItem;
            try
            {
                string id = CursorCore.MakeSchemeId(txtScheme.Text);
                CursorCore.SaveCursorToScheme(srcPath, it.Role, id, outSize, hotX, hotY);
                txtScheme.Text = id;   // 便于继续往同一方案里保存其他角色
                RefreshDelCombo();
                // 该方案正在使用中 → 立即重新应用，让修改马上生效
                if (CursorCore.Current.CurrentScheme == id) CursorCore.ApplyScheme(id, true);
                bool canApply = File.Exists(Path.Combine(CursorCore.SchemeDir(id), "Arrow.cur"));
                UpdateStatus("已保存「" + id + "」的「" + it.Label + "」。" + (canApply
                    ? "可在主页点击该卡片整体应用。"
                    : "该方案还没有「正常选择」角色，保存箭头后才能整体应用。"));
                if (Changed != null) Changed();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnApply(object sender, EventArgs e)
        {
            if (!RequireImage()) return;
            RoleItem it = (RoleItem)roleCombo.SelectedItem;
            try
            {
                string dest = Path.Combine(CursorCore.CustomDir, "custom_" + it.Role + ".cur");
                CursorCore.BuildCursorFile(srcPath, outSize, hotX, hotY, dest);
                CursorCore.ApplyCustom(dest, it.Role, true);
                UpdateStatus("已把图片应用为「" + it.Label + "」光标（重启/登录后仍生效）。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "应用失败：" + ex.Message, "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 只在本窗口内预览效果，不动系统设置，关闭窗口即还原
        void OnTry(object sender, EventArgs e)
        {
            if (!RequireImage()) return;
            RoleItem it = (RoleItem)roleCombo.SelectedItem;
            try
            {
                string dest = Path.Combine(CursorCore.CustomDir, "try_" + it.Role + ".cur");
                CursorCore.BuildCursorFile(srcPath, outSize, hotX, hotY, dest);
                IntPtr h = LoadCursorFromFileW(dest);
                if (h == IntPtr.Zero) { UpdateStatus("试戴加载失败（文件无法解析）。"); return; }
                Cursor c = new Cursor(h);
                Cursor = c;
                if (tryCursor != null) tryCursor.Dispose();
                tryCursor = c;
                UpdateStatus("本窗口内已试戴，关闭窗口即还原。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "试戴失败：" + ex.Message, "光标工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnDelete(object sender, EventArgs e)
        {
            string id = delCombo.SelectedItem as string;
            if (id == null) { UpdateStatus("没有可删除的自定义方案。"); return; }
            if (MessageBox.Show(this, "确定删除方案「" + id + "」？此操作不可撤销。", "光标工坊",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (CursorCore.DeleteScheme(id))
            {
                UpdateStatus("已删除方案「" + id + "」。");
                RefreshDelCombo();
                if (Changed != null) Changed();
            }
        }

        void RefreshDelCombo()
        {
            delCombo.Items.Clear();
            foreach (KeyValuePair<string, string> kv in CursorCore.GetSchemes())
                if (!CursorCore.IsBuiltinScheme(kv.Key)) delCombo.Items.Add(kv.Key);
            if (delCombo.Items.Count > 0) delCombo.SelectedIndex = 0;
        }

        void UpdateStatus(string s) { status.Text = s; }
    }
}
