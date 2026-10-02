// GenCursors.cs - 光标资源生成器
// 生成 5 套配色方案 x 14 个光标角色（.cur 静态 + .ani 动画），并输出 app.ico。
// .ani 布局与 Windows 自带 aero_busy.ani 一致：RIFF/ACON + anih(jifRate 单位=1/60秒) + LIST(fram){icon...}
// 用法: GenCursors.exe <输出根目录>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace CursorStudio.Gen
{
    static class GenCursors
    {
        const int Sup = 128;   // 超采样画布边长（抗锯齿）
        const int Out = 32;    // 最终光标尺寸

        class Scheme
        {
            public string Id, Name;
            public Color Fill, Dark, Accent;
            // 浅色填充的方案（经典白/小狗奶油色）用深色描边才在浅色背景上可见
            public Color Stroke { get { return (Id == "classic" || Id == "puppy") ? Dark : Fill; } }   // 线条类光标主色
            public Color Spin   { get { return Id == "classic" ? Accent : Fill; } } // 加载圈高亮色
        }

        static readonly List<Scheme> Schemes = new List<Scheme> {
            new Scheme{ Id="classic", Name="经典白", Fill=Color.White,                          Dark=Color.FromArgb(45,45,45),    Accent=Color.FromArgb(0,120,215)  },
            new Scheme{ Id="sakura",  Name="樱花粉", Fill=Color.FromArgb(255,128,171),          Dark=Color.FromArgb(184,61,109),  Accent=Color.FromArgb(255,128,171)},
            new Scheme{ Id="mint",    Name="薄荷绿", Fill=Color.FromArgb(46,204,148),           Dark=Color.FromArgb(15,133,94),   Accent=Color.FromArgb(46,204,148) },
            new Scheme{ Id="night",   Name="暗夜紫", Fill=Color.FromArgb(167,139,250),          Dark=Color.FromArgb(104,77,212),  Accent=Color.FromArgb(167,139,250)},
            new Scheme{ Id="sunset",  Name="活力橙", Fill=Color.FromArgb(251,146,60),           Dark=Color.FromArgb(192,86,18),   Accent=Color.FromArgb(251,146,60) },
            // 小狗汪汪：配色取自小狗角色设定图（蜂蜜棕/奶油色/深棕/腮红粉）
            // 文本/调整大小等功能角色用该配色重绘经典形状；Arrow/Wait/AppStarting/Hand/Help
            // 五个角色由 DogCursorGen 用设定图抠出的小狗本体覆盖
            new Scheme{ Id="puppy",   Name="小狗汪汪", Fill=Color.FromArgb(243,227,201),        Dark=Color.FromArgb(74,54,38),    Accent=Color.FromArgb(232,154,164)},
        };

        static readonly string[] StaticRoles = {
            "Arrow","Help","Crosshair","IBeam","No","SizeNS","SizeWE","SizeNWSE","SizeNESW","SizeAll","UpArrow","Hand"
        };

        // 各角色热点（32px 逻辑坐标）——必须与绘制出的图案对准，否则点击会偏移
        static readonly Dictionary<string, int[]> Hotspots = new Dictionary<string, int[]> {
            {"Arrow",new[]{1,1}},      {"Help",new[]{1,1}},       {"IBeam",new[]{16,16}},
            {"Crosshair",new[]{16,16}},{"No",new[]{16,16}},       {"SizeNS",new[]{16,16}},
            {"SizeWE",new[]{16,16}},   {"SizeNWSE",new[]{16,16}}, {"SizeNESW",new[]{16,16}},
            {"SizeAll",new[]{16,16}},  {"UpArrow",new[]{16,2}},   {"Hand",new[]{14,2}},
            {"Wait",new[]{16,16}},     {"AppStarting",new[]{1,1}},
        };

        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : ".";
            if (args.Length > 1 && args[1] == "png") { BuildQaSheet(Path.Combine(root, "qa_sheet.png")); return 0; }
            string outDir = Path.Combine(root, "cursors");
            Directory.CreateDirectory(outDir);

            foreach (Scheme sc in Schemes)
            {
                string dir = Path.Combine(outDir, sc.Id);
                Directory.CreateDirectory(dir);

                foreach (string role in StaticRoles)
                {
                    using (Bitmap b = Render(g => Draw(g, role, sc, 0), Out))
                        File.WriteAllBytes(Path.Combine(dir, role + ".cur"), BuildCur(b, Hotspots[role][0], Hotspots[role][1]));
                }

                // 动画：忙碌(Wait) 与 后台运行(AppStarting)，24 帧，jifRate=3（50ms/帧，与系统 aero_busy 相同）
                File.WriteAllBytes(Path.Combine(dir, "Wait.ani"), BuildAni(f => Render(g => Draw(g, "Wait", sc, f), Out), 24, 16, 16));
                File.WriteAllBytes(Path.Combine(dir, "AppStarting.ani"), BuildAni(f => Render(g => Draw(g, "AppStarting", sc, f), Out), 24, 1, 1));

                // 预览图（UI 用）
                using (Bitmap p = Render(g => Draw(g, "Arrow", sc, 0), Sup))
                    p.Save(Path.Combine(dir, "preview.png"), ImageFormat.Png);
            }

            File.WriteAllText(Path.Combine(outDir, "manifest.txt"), BuildManifest(), new UTF8Encoding(false));

            // 程序图标（经典白箭头 16/32/48）
            string icoPath = Path.Combine(root, "app.ico");
            List<Bitmap> icoFrames = new List<Bitmap>();
            foreach (int s in new int[] { 16, 32, 48 }) icoFrames.Add(Render(g => Draw(g, "Arrow", Schemes[0], 0), s));
            File.WriteAllBytes(icoPath, BuildIco(icoFrames));
            foreach (Bitmap b in icoFrames) b.Dispose();

            Console.WriteLine("OK: " + outDir);
            return 0;
        }

        // QA：渲染全部方案 x 角色的对照图
        static void BuildQaSheet(string path)
        {
            string[] roles = new string[] { "Arrow","IBeam","Crosshair","No","SizeAll","SizeNWSE","SizeNESW","Hand","UpArrow","Help","Wait","AppStarting" };
            int cell = 44, labelW = 96;
            using (Bitmap sheet = new Bitmap(labelW + roles.Length * cell, Schemes.Count * cell + 24, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                using (Font f = new Font("Microsoft YaHei", 9f))
                {
                    for (int r = 0; r < Schemes.Count; r++)
                        g.DrawString(Schemes[r].Name, f, Brushes.Black, 4, r * cell + 14);
                    for (int c = 0; c < roles.Length; c++)
                        g.DrawString(roles[c], f, Brushes.Black, labelW + c * cell, Schemes.Count * cell + 4);
                }
                for (int r = 0; r < Schemes.Count; r++)
                {
                    for (int c = 0; c < roles.Length; c++)
                    {
                        string role = roles[c];
                        int frame = (role == "Wait" || role == "AppStarting") ? 6 : 0;
                        using (Bitmap b = Render(gg => Draw(gg, role, Schemes[r], frame), 32))
                            g.DrawImage(b, labelW + c * cell + 5, r * cell + 5, 32, 32);
                    }
                }
                sheet.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("QA sheet: " + path);
        }

        static string BuildManifest()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Scheme sc in Schemes) sb.Append(sc.Id).Append('|').AppendLine(sc.Name);
            return sb.ToString();
        }

        // ---------------- 绘制 ----------------

        // 在超采样画布上绘制后高质量缩小到 outSize
        static Bitmap Render(Action<Graphics> draw, int outSize)
        {
            Bitmap big = new Bitmap(Sup, Sup, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(Sup / 32f, Sup / 32f);   // 之后全部使用 32px 逻辑坐标
                draw(g);
            }
            if (outSize == Sup) return big;
            Bitmap small = new Bitmap(outSize, outSize, PixelFormat.Format32bppArgb);
            using (Graphics g2 = Graphics.FromImage(small))
            {
                g2.CompositingQuality = CompositingQuality.HighQuality;
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g2.DrawImage(big, new Rectangle(0, 0, outSize, outSize), 0, 0, Sup, Sup, GraphicsUnit.Pixel, ia);
                }
            }
            big.Dispose();
            return small;
        }

        static void Draw(Graphics g, string role, Scheme sc, int frame)
        {
            switch (role)
            {
                case "Arrow": Outline(g, ArrowPath(1f, 0f, 0f), sc, 3.4f, 1.35f); break;
                case "UpArrow": Outline(g, UpArrowPath(), sc, 3.4f, 1.35f); break;
                case "IBeam": Stroke(g, IBeamPath(), sc, 2.2f); break;
                case "Crosshair": Stroke(g, CrossPath(), sc, 2.2f); break;
                case "No": Stroke(g, NoPath(), sc, 2.8f); break;
                case "SizeNS": Outline(g, SizeArrowPath(0f), sc, 3.2f, 1.3f); break;
                case "SizeWE": Outline(g, SizeArrowPath(90f), sc, 3.2f, 1.3f); break;
                case "SizeNESW": Outline(g, SizeArrowPath(45f), sc, 3.2f, 1.3f); break;
                case "SizeNWSE": Outline(g, SizeArrowPath(-45f), sc, 3.2f, 1.3f); break;
                case "SizeAll":
                    OutlineMulti(g, new GraphicsPath[] { SizeArrowPath(0f), SizeArrowPath(90f) }, sc, 3.2f, 1.3f); break;
                case "Hand": DrawHand(g, sc); break;
                case "Help": DrawHelp(g, sc); break;
                case "Wait": DrawWait(g, sc, frame); break;
                case "AppStarting": DrawAppStarting(g, sc, frame); break;
            }
        }

        // 实心形状：白色外描边 + 填充 + 深色内描边（浅色/深色背景都清晰）
        static void Outline(Graphics g, GraphicsPath p, Scheme sc, float whiteW, float darkW)
        {
            using (Pen w = new Pen(Color.White, whiteW)) { w.LineJoin = LineJoin.Round; g.DrawPath(w, p); }
            using (SolidBrush b = new SolidBrush(sc.Fill)) g.FillPath(b, p);
            using (Pen d = new Pen(sc.Dark, darkW)) { d.LineJoin = LineJoin.Round; g.DrawPath(d, p); }
            p.Dispose();
        }

        static void OutlineMulti(Graphics g, GraphicsPath[] ps, Scheme sc, float whiteW, float darkW)
        {
            using (Pen w = new Pen(Color.White, whiteW)) { w.LineJoin = LineJoin.Round; foreach (GraphicsPath p in ps) g.DrawPath(w, p); }
            using (SolidBrush b = new SolidBrush(sc.Fill)) foreach (GraphicsPath p in ps) g.FillPath(b, p);
            using (Pen d = new Pen(sc.Dark, darkW)) { d.LineJoin = LineJoin.Round; foreach (GraphicsPath p in ps) g.DrawPath(d, p); }
            foreach (GraphicsPath p in ps) p.Dispose();
        }

        // 线条形状：白色宽线打底 + 主色线
        static void Stroke(Graphics g, GraphicsPath p, Scheme sc, float w)
        {
            using (Pen white = new Pen(Color.White, w + 3.0f))
            {
                white.LineJoin = LineJoin.Round; white.StartCap = LineCap.Round; white.EndCap = LineCap.Round;
                g.DrawPath(white, p);
            }
            using (Pen c = new Pen(sc.Stroke, w))
            {
                c.LineJoin = LineJoin.Round; c.StartCap = LineCap.Round; c.EndCap = LineCap.Round;
                g.DrawPath(c, p);
            }
            p.Dispose();
        }

        static GraphicsPath ArrowPath(float scale, float dx, float dy)
        {
            PointF[] pts = new PointF[] {
                new PointF(1,1), new PointF(1,24), new PointF(7,18.5f), new PointF(11,27),
                new PointF(14.5f,25.5f), new PointF(10.5f,17), new PointF(17.5f,17)
            };
            return Transformed(pts, scale, dx, dy, true);
        }

        static GraphicsPath UpArrowPath()
        {
            PointF[] pts = new PointF[] {
                new PointF(16,2), new PointF(7.5f,10.5f), new PointF(12,10.5f), new PointF(12,26),
                new PointF(20,26), new PointF(20,10.5f), new PointF(24.5f,10.5f)
            };
            return Transformed(pts, 1f, 0f, 0f, true);
        }

        static GraphicsPath IBeamPath()
        {
            GraphicsPath p = new GraphicsPath();
            p.AddLine(12, 4.5f, 20, 4.5f);      // 顶部衬线
            p.StartFigure();
            p.AddLine(16, 4.5f, 16, 27.5f);     // 竖杆
            p.StartFigure();
            p.AddLine(12, 27.5f, 20, 27.5f);    // 底部衬线
            return p;
        }

        static GraphicsPath CrossPath()
        {
            GraphicsPath p = new GraphicsPath();
            p.AddLine(16, 3.5f, 16, 28.5f);
            p.StartFigure();
            p.AddLine(3.5f, 16, 28.5f, 16);
            return p;
        }

        static GraphicsPath NoPath()
        {
            GraphicsPath p = new GraphicsPath();
            p.AddEllipse(5.5f, 5.5f, 21, 21);
            p.StartFigure();
            p.AddLine(9.5f, 9.5f, 22.5f, 22.5f);
            return p;
        }

        // 双向箭头（先按垂直方向构造，再绕中心旋转）
        static GraphicsPath SizeArrowPath(float angleDeg)
        {
            PointF[] pts = new PointF[] {
                new PointF(16,2.5f), new PointF(10,10), new PointF(14.2f,10), new PointF(14.2f,22),
                new PointF(10,22), new PointF(16,29.5f), new PointF(22,22), new PointF(17.8f,22),
                new PointF(17.8f,10), new PointF(22,10)
            };
            GraphicsPath p = Transformed(pts, 1f, 0f, 0f, true);
            using (Matrix m = new Matrix()) { m.RotateAt(angleDeg, new PointF(16, 16)); p.Transform(m); }
            return p;
        }

        static GraphicsPath Transformed(PointF[] pts, float scale, float dx, float dy, bool close)
        {
            GraphicsPath p = new GraphicsPath();
            if (scale != 1f || dx != 0f || dy != 0f)
            {
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new PointF(pts[i].X * scale + dx, pts[i].Y * scale + dy);
            }
            p.AddLines(pts);
            if (close) p.CloseFigure();
            return p;
        }

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(x, y, r * 2, r * 2, 180, 180);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 180);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 180);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 180);
            p.CloseFigure();
            return p;
        }

        static void DrawHand(Graphics g, Scheme sc)
        {
            GraphicsPath finger = RoundedRect(12.2f, 2.5f, 5.4f, 13f, 2.7f);   // 食指
            GraphicsPath palm = RoundedRect(10.6f, 11.6f, 12.6f, 15f, 4.5f);   // 手掌
            GraphicsPath thumb = new GraphicsPath(); thumb.AddEllipse(8.2f, 13.2f, 5f, 8.5f);
            GraphicsPath[] ps = new GraphicsPath[] { finger, palm, thumb };

            using (Pen w = new Pen(Color.White, 3.4f))
            {
                w.LineJoin = LineJoin.Round;
                foreach (GraphicsPath p in ps) { g.DrawPath(w, p); g.FillPath(Brushes.White, p); }
            }
            using (SolidBrush b = new SolidBrush(sc.Fill))
                foreach (GraphicsPath p in ps) g.FillPath(b, p);

            using (Pen d = new Pen(sc.Dark, 1.1f))   // 指缝细节
            {
                g.DrawLine(d, 15.3f, 20f, 15.3f, 24.4f);
                g.DrawLine(d, 18.1f, 20f, 18.1f, 24.4f);
            }
            foreach (GraphicsPath p in ps) p.Dispose();
        }

        static void DrawHelp(Graphics g, Scheme sc)
        {
            Outline(g, ArrowPath(0.85f, 0.6f, 0.6f), sc, 3.0f, 1.2f);
            RectangleF badge = new RectangleF(18.6f, 1.4f, 11.2f, 11.2f);
            using (Pen w = new Pen(Color.White, 4.2f)) g.DrawEllipse(w, badge);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, badge);
            using (Pen d = new Pen(sc.Dark, 1.3f)) g.DrawEllipse(d, badge);
            using (Font f = new Font("Segoe UI", 7.6f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(sc.Dark))
                    g.DrawString("?", f, b, new RectangleF(18.6f, 1.9f, 11.2f, 11.2f), sf);
            }
        }

        static void DrawWait(Graphics g, Scheme sc, int frame)
        {
            RectangleF ring = new RectangleF(6.6f, 6.6f, 18.8f, 18.8f);
            using (Pen basePen = new Pen(Color.FromArgb(70, sc.Dark), 3.4f)) g.DrawArc(basePen, ring, 0, 360);
            using (Pen hl = new Pen(sc.Spin, 3.4f))
            {
                hl.StartCap = LineCap.Round; hl.EndCap = LineCap.Round;
                g.DrawArc(hl, ring, -90f + frame * 15f, 100f);
            }
        }

        static void DrawAppStarting(Graphics g, Scheme sc, int frame)
        {
            Outline(g, ArrowPath(0.75f, 0.25f, 0.25f), sc, 2.8f, 1.1f);
            RectangleF ring = new RectangleF(18.6f, 18.6f, 9.8f, 9.8f);
            using (Pen basePen = new Pen(Color.FromArgb(80, sc.Dark), 2.6f)) g.DrawArc(basePen, ring, 0, 360);
            using (Pen hl = new Pen(sc.Spin, 2.6f))
            {
                hl.StartCap = LineCap.Round; hl.EndCap = LineCap.Round;
                g.DrawArc(hl, ring, -90f + frame * 15f, 100f);
            }
        }

        // ---------------- 文件格式 ----------------

        // Bitmap -> .cur（ICONDIR + ICONDIRENTRY(hotspot) + BITMAPINFOHEADER + XOR(BGRA) + AND 掩码）
        static byte[] BuildCur(Bitmap b, int hotX, int hotY)
        {
            int w = b.Width, h = b.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData d = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int xorStride = w * 4;
            byte[] xor = new byte[xorStride * h];
            byte[] row = new byte[xorStride];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + y * d.Stride), row, 0, xorStride);
                    Array.Copy(row, 0, xor, (h - 1 - y) * xorStride, xorStride);   // 自底向上
                }
            }
            finally { b.UnlockBits(d); }

            int andStride = ((w + 31) / 32) * 4;
            byte[] and = new byte[andStride * h];   // 全 0：透明度交给 alpha 通道

            byte[] img = new byte[40 + xor.Length + and.Length];
            WriteU32(img, 0, 40); WriteU32(img, 4, (uint)w); WriteU32(img, 8, (uint)(h * 2));
            img[12] = 1; img[14] = 32;            // planes=1, bitCount=32
            WriteU32(img, 20, (uint)(xor.Length + and.Length));
            Array.Copy(xor, 0, img, 40, xor.Length);
            Array.Copy(and, 0, img, 40 + xor.Length, and.Length);

            byte[] cur = new byte[6 + 16 + img.Length];
            WriteU16(cur, 2, 2); WriteU16(cur, 4, 1);   // type=2 (cursor), count=1
            cur[6] = (byte)(w >= 256 ? 0 : w); cur[7] = (byte)(h >= 256 ? 0 : h);
            WriteU16(cur, 10, (ushort)hotX); WriteU16(cur, 12, (ushort)hotY);  // .cur 中这两字段是热点
            WriteU32(cur, 14, (uint)img.Length); WriteU32(cur, 18, 22);
            Array.Copy(img, 0, cur, 22, img.Length);
            return cur;
        }

        // .ani：与 aero_busy.ani 相同布局（无 rate/seq 块，jifRate 单位 1/60 秒）
        static byte[] BuildAni(Func<int, Bitmap> frameRenderer, int frames, int hotX, int hotY)
        {
            List<byte[]> curs = new List<byte[]>();
            for (int i = 0; i < frames; i++)
            {
                using (Bitmap b = frameRenderer(i))
                    curs.Add(BuildCur(b, hotX, hotY));
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                long bodyStart = 12;
                w.Write(Asc("RIFF")); w.Write(0u); w.Write(Asc("ACON"));

                byte[] anih = new byte[36];
                WriteU32(anih, 0, 36); WriteU32(anih, 4, (uint)frames); WriteU32(anih, 8, (uint)frames);
                WriteU32(anih, 12, 0); WriteU32(anih, 16, 0); WriteU32(anih, 20, 32);
                WriteU32(anih, 24, 1); WriteU32(anih, 28, 3); WriteU32(anih, 32, 1);   // jifRate=3 jiffy≈50ms, fl=AF_ICON
                WriteChunk(w, "anih", anih);

                long framHeaderPos = ms.Length;
                w.Write(Asc("LIST")); w.Write(0u); w.Write(Asc("fram"));
                foreach (byte[] c in curs) WriteChunk(w, "icon", c);
                long afterFram = ms.Length;
                uint framSize = (uint)(afterFram - framHeaderPos - 8);
                Patch(ms, framHeaderPos + 4, framSize);

                uint riffSize = (uint)(ms.Length - bodyStart);
                Patch(ms, 4, riffSize);
                return ms.ToArray();
            }
        }

        static byte[] BuildIco(List<Bitmap> frames)
        {
            List<byte[]> imgs = new List<byte[]>();
            foreach (Bitmap b in frames) imgs.Add(BuildCur(b, 0, 0));   // 复用 CUR 编码（ico 中热点字段无效）

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)imgs.Count);   // type=1 icon
                int offset = 6 + 16 * imgs.Count;
                for (int i = 0; i < imgs.Count; i++)
                {
                    Bitmap b = frames[i]; byte[] data = imgs[i];
                    w.Write((byte)(b.Width >= 256 ? 0 : b.Width));
                    w.Write((byte)(b.Height >= 256 ? 0 : b.Height));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);    // ico：planes/bitcount 是真实值
                    w.Write((uint)data.Length); w.Write((uint)offset);
                    offset += data.Length;
                }
                foreach (byte[] data in imgs) w.Write(data);
                return ms.ToArray();
            }
        }

        static void WriteChunk(BinaryWriter w, string fourcc, byte[] data)
        {
            w.Write(Asc(fourcc)); w.Write((uint)data.Length); w.Write(data);
            if (data.Length % 2 == 1) w.Write((byte)0);
        }

        static void Patch(MemoryStream ms, long pos, uint value)
        {
            byte[] b = ms.GetBuffer();
            b[pos] = (byte)value; b[pos + 1] = (byte)(value >> 8); b[pos + 2] = (byte)(value >> 16); b[pos + 3] = (byte)(value >> 24);
        }

        static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static byte[] Asc(string s) { return System.Text.Encoding.ASCII.GetBytes(s); }
    }
}
