// DogCursorGen.cs - 把小狗角色设定图变成光标
// 流程（对应"照片→角色表→主姿态→透明化→缩放→Cursor"管线）：
//   1. 从设定图按归一化坐标裁出各姿态
//   2. 洪水填充从边缘抠掉米色背景与浅色投影（狗有完整深色轮廓线，填充不会漏进体内）
//   3. 只保留最大不透明连通域（去掉 zZ、问号、爱心等漂浮装饰）
//   4. 1px 羽化边缘，裁切到内容包围盒
//   5. 高质量缩放到 32x32，按角色热点生成 .cur（复用 CursorCore.BuildCurBytes）
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
        public float HotFx, HotFy;  // 热点在内容包围盒中的比例（鼻尖）
        public Region(string name, string role, float x, float y, float w, float h, float fx, float fy)
        { Name = name; Role = role; Norm = new RectangleF(x, y, w, h); HotFx = fx; HotFy = fy; }
    }

    // 坐标基于 1536x1024 设定图目测，QA 网格中校验后微调
    static readonly Region[] Regions = {
        new Region("arrow_front", "Arrow",       0.010f, 0.075f, 0.200f, 0.490f, 0.50f, 0.35f),
        new Region("help_dimu",   "Help",        0.018f, 0.595f, 0.150f, 0.240f, 0.52f, 0.63f),
        new Region("hand_happy",  "Hand",        0.163f, 0.595f, 0.140f, 0.240f, 0.50f, 0.58f),
        new Region("app_curious", "AppStarting", 0.287f, 0.575f, 0.130f, 0.255f, 0.47f, 0.58f),
        new Region("wait_sleep",  "Wait",        0.425f, 0.635f, 0.150f, 0.215f, 0.52f, 0.72f),
    };

    const int BgTol = 48;   // 与参考色的 RGB 欧氏距离阈值

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : ".";
        string sheetPath = Path.Combine(root, "tools", "dog", "sheet.png");
        string outDir = Path.Combine(root, "cursors", "puppy");
        string srcDir = Path.Combine(root, "cursors", "puppy_src");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(srcDir);

        using (Bitmap sheet = new Bitmap(sheetPath))
        {
            List<Bitmap> keyed = new List<Bitmap>();
            List<Point> hots = new List<Point>();
            foreach (Region r in Regions)
            {
                Point hot;
                Bitmap k = CropKey(sheet, r, out hot);
                k.Save(Path.Combine(srcDir, r.Name + ".png"), ImageFormat.Png);
                keyed.Add(k); hots.Add(hot);

                // 缩放到 32x32 并生成 .cur
                using (Bitmap cur32 = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(cur32))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawScaled(g, k, new Rectangle(0, 0, 32, 32));
                    int hx = Math.Max(0, Math.Min(31, (int)Math.Round(hot.X * 32.0 / k.Width)));
                    int hy = Math.Max(0, Math.Min(31, (int)Math.Round(hot.Y * 32.0 / k.Height)));
                    File.WriteAllBytes(Path.Combine(outDir, r.Role + ".cur"),
                        CursorCore.BuildCurBytes(cur32, hx, hy));
                    cur32.Save(Path.Combine(srcDir, r.Name + "_32.png"), ImageFormat.Png);
                    Console.WriteLine(r.Name + " -> " + r.Role + ".cur  content " + k.Width + "x" + k.Height + "  hot(" + hx + "," + hy + ")");
                }
            }

            // 方案预览图：正面全身小狗 128px，米色背景
            using (Bitmap pv = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(pv))
            {
                g.Clear(Color.FromArgb(245, 239, 228));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawScaled(g, keyed[0], new Rectangle(10, 4, 108, 120));
                pv.Save(Path.Combine(outDir, "preview.png"), ImageFormat.Png);
            }

            // QA 网格：各裁切 + 热点十字
            using (Bitmap qa = new Bitmap(5 * 150, 180, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(qa))
            {
                g.Clear(Color.White);
                using (Font f = new Font("Segoe UI", 9f))
                    for (int i = 0; i < keyed.Count; i++)
                    {
                        int ox = i * 150 + 5;
                        DrawScaled(g, keyed[i], new Rectangle(ox, 5, 140, 140));
                        int cx = ox + (int)(140.0 * hots[i].X / keyed[i].Width);
                        int cy = 5 + (int)(140.0 * hots[i].Y / keyed[i].Height);
                        using (Pen p = new Pen(Color.Red, 1.5f))
                        {
                            g.DrawLine(p, cx - 8, cy, cx + 8, cy);
                            g.DrawLine(p, cx, cy - 8, cx, cy + 8);
                        }
                        g.DrawString(Regions[i].Role, f, Brushes.Black, ox, 148);
                    }
                qa.Save(Path.Combine(root, "tools", "dog", "qa.png"), ImageFormat.Png);
            }
        }
        Console.WriteLine("OK");
        return 0;
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
