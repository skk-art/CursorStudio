// DogCursorGen.cs - 把小狗角色设定图变成光标
// 流程（对应"照片→角色表→主姿态→透明化→缩放→Cursor"管线）：
//   1. 从设定图按归一化坐标裁出各姿态
//   2. 洪水填充从边缘抠掉米色背景与浅色投影（狗有完整深色轮廓线，填充不会漏进体内）
//   3. 只保留最大不透明连通域（去掉 zZ、问号、爱心等漂浮装饰）
//   4. 1px 羽化边缘，裁切到内容包围盒
//   5. 绘制到 48x48 画布（图案为 32px 适配尺寸的 1.5 倍，完整显示）
//   6. 热点位于图案右上角，并绘制高光标记（白核+深棕环+柔光）标示点击区域
// 用法: DogCursorGen.exe <项目根目录>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using CursorStudio;

static class DogCursorGen
{
    class Region
    {
        public string Name, Role;
        public RectangleF Norm;     // 在设定图上的归一化裁切区域
        public float HotFx, HotFy;  // 热点（点击区域）在内容包围盒中的比例：头部右上角
        public bool FillCrop;       // true = 1.5 倍放大、顶部锚定裁掉下部（头部充满光标）
        public Region(string name, string role, float x, float y, float w, float h, float fx, float fy, bool crop)
        { Name = name; Role = role; Norm = new RectangleF(x, y, w, h); HotFx = fx; HotFy = fy; FillCrop = crop; }
    }

    // 坐标基于 1536x1024 设定图目测，QA 网格中校验后微调
    // 注：Windows 把所有光标统一渲染为系统指针尺寸（150% DPI 下 48px），
    //     文件尺寸无法突破；FillCrop 通过 1.5 倍绘制+裁切让主体真正变大。
    //     头像类角色放大 1.5 倍会切掉耳朵，故仅 Arrow 使用 FillCrop。
    static readonly Region[] Regions = {
        new Region("arrow_front", "Arrow",       0.010f, 0.075f, 0.200f, 0.490f, 0.72f, 0.13f, true),
        new Region("help_dimu",   "Help",        0.018f, 0.595f, 0.150f, 0.240f, 0.74f, 0.13f, false),
        new Region("hand_happy",  "Hand",        0.163f, 0.595f, 0.140f, 0.240f, 0.74f, 0.13f, false),
        new Region("app_curious", "AppStarting", 0.287f, 0.575f, 0.130f, 0.255f, 0.74f, 0.13f, false),
        new Region("wait_sleep",  "Wait",        0.425f, 0.635f, 0.150f, 0.215f, 0.70f, 0.22f, false),
    };

    const int BgTol = 48;       // 与参考色的 RGB 欧氏距离阈值
    const int Canvas = 48;      // 角色光标画布
    const double Zoom = 1.5;    // 图案相对 32px 适配尺寸的放大倍数

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : ".";
        string sheetPath = Path.Combine(root, "tools", "dog", "sheet.png");
        string outDir = Path.Combine(root, "cursors", "puppy");
        string srcDir = Path.Combine(root, "cursors", "puppy_src");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(srcDir);

        List<Bitmap> finals = new List<Bitmap>();
        List<Point> hotSpots = new List<Point>();
        List<string> labels = new List<string>();

        using (Bitmap sheet = new Bitmap(sheetPath))
        {
            foreach (Region r in Regions)
            {
                Point hot;
                using (Bitmap k = CropKey(sheet, r, out hot))
                {
                    k.Save(Path.Combine(srcDir, r.Name + ".png"), ImageFormat.Png);

                    // 48x48 画布。FillCrop：1.5 倍放大、顶部锚定（头部完整、裁掉下部身体）；
                    // 其余：完整居中显示
                    double s = (r.FillCrop ? Zoom : 1.0) * Canvas / (double)Math.Max(k.Width, k.Height);
                    int dw = Math.Max(1, (int)Math.Round(k.Width * s));
                    int dh = Math.Max(1, (int)Math.Round(k.Height * s));
                    int ox = (Canvas - dw) / 2;
                    int oy = r.FillCrop ? 0 : (Canvas - dh) / 2;

                    Bitmap final = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(final))
                    {
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        DrawScaled(g, k, new Rectangle(ox, oy, dw, dh));

                        // 热点 = 图案右上角（点击区域），画高光标记
                        int hx = Math.Max(1, Math.Min(Canvas - 2, ox + (int)Math.Round(r.HotFx * dw)));
                        int hy = Math.Max(1, Math.Min(Canvas - 2, oy + (int)Math.Round(r.HotFy * dh)));
                        DrawHotspotMarker(g, hx, hy);
                        hotSpots.Add(new Point(hx, hy));
                    }
                    final.Save(Path.Combine(srcDir, r.Name + "_48.png"), ImageFormat.Png);
                    File.WriteAllBytes(Path.Combine(outDir, r.Role + ".cur"),
                        CursorCore.BuildCurBytes(final, hotSpots[hotSpots.Count - 1].X, hotSpots[hotSpots.Count - 1].Y));
                    finals.Add(final);
                    labels.Add(r.Role);
                    Console.WriteLine(r.Name + " -> " + r.Role + ".cur  内容 " + dw + "x" + dh + "  热点(" +
                        hotSpots[hotSpots.Count - 1].X + "," + hotSpots[hotSpots.Count - 1].Y + ")");
                }
            }

            // 方案预览图：最终光标（含高光标记）放大，米色背景
            using (Bitmap pv = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(pv))
            {
                g.Clear(Color.FromArgb(245, 239, 228));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawScaled(g, finals[0], new Rectangle(14, 4, 100, 120));
                pv.Save(Path.Combine(outDir, "preview.png"), ImageFormat.Png);
            }

            // QA 网格：最终 48px 光标 3 倍放大 + 热点红叉，深色背景检查可见性
            using (Bitmap qa = new Bitmap(5 * 160, 200, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(qa))
            {
                g.Clear(Color.FromArgb(70, 70, 80));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (Font f = new Font("Segoe UI", 9f))
                    for (int i = 0; i < finals.Count; i++)
                    {
                        int bx = i * 160 + 8;
                        g.DrawImage(finals[i], new Rectangle(bx, 8, Canvas * 3, Canvas * 3),
                            0, 0, Canvas, Canvas, GraphicsUnit.Pixel);
                        int cx = bx + hotSpots[i].X * 3, cy = 8 + hotSpots[i].Y * 3;
                        using (Pen p = new Pen(Color.Red, 1.5f))
                        {
                            g.DrawLine(p, cx - 9, cy, cx + 9, cy);
                            g.DrawLine(p, cx, cy - 9, cx, cy + 9);
                        }
                        g.DrawString(labels[i], f, Brushes.White, bx, 156);
                    }
                qa.Save(Path.Combine(root, "tools", "dog", "qa48.png"), ImageFormat.Png);
            }
        }
        foreach (Bitmap b in finals) b.Dispose();
        Console.WriteLine("OK");
        return 0;
    }

    // 点击区域高光标记：柔光 + 深棕环 + 白核（浅色毛发和深色背景上都可见）
    static void DrawHotspotMarker(Graphics g, int hx, int hy)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
            g.FillEllipse(b, hx - 5.5f, hy - 5.5f, 11f, 11f);
        using (Pen p = new Pen(Color.FromArgb(230, 74, 54, 38), 1.6f))
            g.DrawEllipse(p, hx - 3.1f, hy - 3.1f, 6.2f, 6.2f);
        using (SolidBrush b = new SolidBrush(Color.White))
            g.FillEllipse(b, hx - 2.1f, hy - 2.1f, 4.2f, 4.2f);
    }

    // 高质量缩放（边缘镜像采样避免光晕）
    static void DrawScaled(Graphics g, Image src, Rectangle dst)
    {
        using (ImageAttributes ia = new ImageAttributes())
        {
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, dst, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
    }

    // 裁切 + 去背景 + 最大连通域 + 羽化 + 内容裁切
    static Bitmap CropKey(Bitmap sheet, Region r, out Point hot)
    {
        int sw = sheet.Width, sh = sheet.Height;
        Rectangle px = new Rectangle(
            Math.Max(0, (int)(r.Norm.X * sw)), Math.Max(0, (int)(r.Norm.Y * sh)),
            Math.Min(sw - 1, (int)(r.Norm.Width * sw)), Math.Min(sh - 1, (int)(r.Norm.Height * sh)));
        if (px.Right > sw) px.Width = sw - px.X;
        if (px.Bottom > sh) px.Height = sh - px.Y;

        using (Bitmap crop = sheet.Clone(px, PixelFormat.Format32bppArgb))
        {
            int w = crop.Width, h = crop.Height;
            byte[] data = GetPixels(crop, w, h);
            int n = w * h;

            // 背景参考色：顶部两角 + 底部两角（底部角通常是投影边缘）
            List<Color> refs = new List<Color>();
            refs.Add(AvgAt(data, w, h, 2, 2)); refs.Add(AvgAt(data, w, h, w - 3, 2));
            refs.Add(AvgAt(data, w, h, 2, h - 3)); refs.Add(AvgAt(data, w, h, w - 3, h - 3));

            // 洪水填充：从四条边出发，把所有"像背景/投影"的连通像素标记为透明
            bool[] bg = new bool[n];
            Queue<int> queue = new Queue<int>();
            Action<int> seed = delegate(int i)
            {
                if (!bg[i] && IsBg(data, i, refs)) { bg[i] = true; queue.Enqueue(i); }
            };
            for (int x = 0; x < w; x++) { seed(x); seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { seed(y * w); seed(y * w + w - 1); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;
                if (x > 0) seed(i - 1);
                if (x < w - 1) seed(i + 1);
                if (y > 0) seed(i - w);
                if (y < h - 1) seed(i + w);
            }

            // 二值 alpha，然后只保留最大连通域（去掉 zZ/问号/爱心等漂浮装饰）
            byte[] alpha = new byte[n];
            for (int i = 0; i < n; i++) alpha[i] = bg[i] ? (byte)0 : (byte)255;
            KeepLargestComponent(alpha, w, h);

            // 1px 羽化：3x3 均值
            byte[] soft = new byte[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Max(0, Math.Min(w - 1, x + dx));
                            int yy = Math.Max(0, Math.Min(h - 1, y + dy));
                            sum += alpha[yy * w + xx];
                        }
                    soft[y * w + x] = (byte)(sum / 9);
                }

            // 内容包围盒（alpha >= 16）+ 2px 边距
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (soft[y * w + x] >= 16)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) { minX = 0; minY = 0; maxX = w - 1; maxY = h - 1; }
            minX = Math.Max(0, minX - 2); minY = Math.Max(0, minY - 2);
            maxX = Math.Min(w - 1, maxX + 2); maxY = Math.Min(h - 1, maxY + 2);
            int cw = maxX - minX + 1, ch = maxY - minY + 1;

            Bitmap keyed = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            BitmapData dst = keyed.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < ch; y++)
            {
                IntPtr dstRow = new IntPtr(dst.Scan0.ToInt64() + y * dst.Stride);
                byte[] row = new byte[cw * 4];
                for (int x = 0; x < cw; x++)
                {
                    int si = (minY + y) * w + (minX + x);
                    row[x * 4] = data[si * 4]; row[x * 4 + 1] = data[si * 4 + 1];
                    row[x * 4 + 2] = data[si * 4 + 2]; row[x * 4 + 3] = soft[si];
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, dstRow, row.Length);
            }
            keyed.UnlockBits(dst);

            hot = new Point(
                (int)Math.Round(r.HotFx * cw),
                (int)Math.Round(r.HotFy * ch));
            return keyed;
        }
    }

    static byte[] GetPixels(Bitmap b, int w, int h)
    {
        BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] data = new byte[w * h * 4];
        byte[] row = new byte[Math.Abs(d.Stride)];
        for (int y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + y * d.Stride), row, 0, row.Length);
            Array.Copy(row, 0, data, y * w * 4, w * 4);
        }
        b.UnlockBits(d);
        return data;
    }

    static Color AvgAt(byte[] data, int w, int h, int x, int y)
    {
        int r = 0, g = 0, b = 0, c = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = Math.Max(0, Math.Min(w - 1, x + dx));
                int yy = Math.Max(0, Math.Min(h - 1, y + dy));
                int i = (yy * w + xx) * 4;
                r += data[i]; g += data[i + 1]; b += data[i + 2]; c++;
            }
        return Color.FromArgb(r / c, g / c, b / c);
    }

    static bool IsBg(byte[] data, int i, List<Color> refs)
    {
        int r = data[i * 4], g = data[i * 4 + 1], b = data[i * 4 + 2];
        foreach (Color c in refs)
        {
            int dr = r - c.R, dg = g - c.G, db = b - c.B;
            if (dr * dr + dg * dg + db * db < BgTol * BgTol) return true;
        }
        return false;
    }

    // 保留最大不透明连通域（4 邻接）
    static void KeepLargestComponent(byte[] alpha, int w, int h)
    {
        int n = w * h;
        int[] label = new int[n];
        int best = 0, bestSize = 0, next = 0;
        Queue<int> q = new Queue<int>();
        for (int start = 0; start < n; start++)
        {
            if (alpha[start] == 0 || label[start] != 0) continue;
            int id = ++next, size = 0;
            q.Enqueue(start); label[start] = id;
            while (q.Count > 0)
            {
                int i = q.Dequeue(); size++;
                int x = i % w, y = i / w;
                if (x > 0 && alpha[i - 1] != 0 && label[i - 1] == 0) { label[i - 1] = id; q.Enqueue(i - 1); }
                if (x < w - 1 && alpha[i + 1] != 0 && label[i + 1] == 0) { label[i + 1] = id; q.Enqueue(i + 1); }
                if (y > 0 && alpha[i - w] != 0 && label[i - w] == 0) { label[i - w] = id; q.Enqueue(i - w); }
                if (y < h - 1 && alpha[i + w] != 0 && label[i + w] == 0) { label[i + w] = id; q.Enqueue(i + w); }
            }
            if (size > bestSize) { bestSize = size; best = id; }
        }
        for (int i = 0; i < n; i++) if (alpha[i] != 0 && label[i] != best) alpha[i] = 0;
    }
}
