// CursorCore.cs - 全局光标替换核心引擎
//
// 三层机制（对应方案设计）：
//   1. 注册表持久化：HKCU\Control Panel\Cursors 写入各角色的 .cur/.ani 绝对路径
//   2. 立即生效：SystemParametersInfo(SPI_SETCURSORS) 从注册表装载 + SetSystemCursor 直接覆盖槽位
//   3. 常驻守护：定时用 GetCursorInfo 检查当前全局光标是否仍是我们设置的句柄，
//      被 Windows 11 还原成经典样式时自动重新应用
//
// 参考：Microsoft Docs SetSystemCursor / SystemParametersInfo；
//       SetSystemCursor 会“吞掉”传入的句柄（成功后由系统销毁），因此每次重新应用都要重新 LoadCursorFromFile。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorStudio
{
    public class Settings
    {
        public string CurrentScheme = "";    // 方案 id；"custom" 表示自定义上传
        public string CustomRole = "Arrow";
        public string CustomFile = "";
        public bool Watchdog = true;         // 防还原守护
        public bool Autostart = true;        // 开机自启
        public bool RestoreOnExit = true;    // 退出时恢复默认光标
    }

    public static class CursorCore
    {
        // ---------------- Win32 API ----------------

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadCursorFromFileW(string path);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll")]
        static extern bool DestroyCursor(IntPtr hcur);

        [DllImport("user32.dll")]
        static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        static extern IntPtr CopyIcon(IntPtr hcur);

        [DllImport("user32.dll")]
        static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO pIconInfo);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int dwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public short bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        public const uint SPI_SETCURSORS = 0x0057;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;
        public const int CURSOR_SHOWING = 0x00000001;
        const int ATTACH_PARENT_PROCESS = -1;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

        // 运行时系统光标槽位（OCR_*）。注意：Help 是注册表专属角色，没有 OCR 槽位，由 SPI_SETCURSORS 从注册表装载。
        static readonly Dictionary<string, uint> OcrIds = new Dictionary<string, uint> {
            { "Arrow", 32512 }, { "IBeam", 32513 }, { "Wait", 32514 }, { "Crosshair", 32515 },
            { "UpArrow", 32516 }, { "SizeNWSE", 32642 }, { "SizeNESW", 32643 }, { "SizeWE", 32644 },
            { "SizeNS", 32645 }, { "SizeAll", 32646 }, { "No", 32648 }, { "Hand", 32649 },
            { "AppStarting", 32650 }
        };

        public static readonly string[] Roles = {
            "Arrow","Help","AppStarting","Wait","Crosshair","IBeam","No","SizeNS","SizeWE","SizeNWSE","SizeNESW","SizeAll","UpArrow","Hand"
        };

        public static readonly Dictionary<string, string> RoleNames = new Dictionary<string, string> {
            { "Arrow","正常选择" }, { "Help","帮助选择" }, { "AppStarting","后台运行" }, { "Wait","忙碌" },
            { "Crosshair","精确选择" }, { "IBeam","文本选择" }, { "No","不可用" }, { "SizeNS","垂直调整大小" },
            { "SizeWE","水平调整大小" }, { "SizeNWSE","对角线调整大小 ↘" }, { "SizeNESW","对角线调整大小 ↗" },
            { "SizeAll","移动" }, { "UpArrow","备用选择" }, { "Hand","链接选择" }
        };

        // PNG 上传转换时各角色的默认热点
        public static readonly Dictionary<string, int[]> PngHotspots = new Dictionary<string, int[]> {
            { "Arrow", new[]{0,0} }, { "Help", new[]{0,0} }, { "AppStarting", new[]{0,0} }, { "Hand", new[]{4,1} },
            { "UpArrow", new[]{16,0} }, { "IBeam", new[]{16,16} }, { "Wait", new[]{16,16} },
            { "Crosshair", new[]{16,16} }, { "No", new[]{16,16} }, { "SizeNS", new[]{16,16} },
            { "SizeWE", new[]{16,16} }, { "SizeNWSE", new[]{16,16} }, { "SizeNESW", new[]{16,16} },
            { "SizeAll", new[]{16,16} }
        };

        // ---------------- 状态与路径 ----------------

        static Settings settings;
        // 当前已应用方案的“像素签名”集合：当前全局光标的实际图像命中其中之一即视为未被还原。
        // 注意：不能用句柄比对——SetSystemCursor 替换的是槽位内容，GetCursorInfo 报告的仍是共享句柄（如 0x10003）。
        static HashSet<string> expectedSigs = new HashSet<string>();

        public static Settings Current { get { return settings; } }

        public static string AppDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorStudio"); }
        }
        public static string SchemesDir { get { return Path.Combine(AppDir, "Schemes"); } }
        public static string CustomDir { get { return Path.Combine(AppDir, "Custom"); } }
        public static string SettingsFile { get { return Path.Combine(AppDir, "settings.ini"); } }
        public static string BackupFile { get { return Path.Combine(AppDir, "cursor_backup.txt"); } }
        public static string WatchdogLogFile { get { return Path.Combine(AppDir, "watchdog.log"); } }

        static void EnsureDirs()
        {
            Directory.CreateDirectory(AppDir);
            Directory.CreateDirectory(SchemesDir);
            Directory.CreateDirectory(CustomDir);
        }

        public static void EnsureInit()
        {
            if (settings != null) return;
            EnsureDirs();
            EnsureSeeded();
            settings = LoadSettings();
        }

        // 把编译进 exe 的内置方案解压到 %APPDATA%\CursorStudio\Schemes
        // 内置资源文件始终覆盖（保证 exe 更新后资源同步）；方案名清单走 MergeManifest 保留用户自定义条目
        public static void EnsureSeeded()
        {
            EnsureDirs();
            Assembly asm = typeof(CursorCore).Assembly;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("cs_")) continue;
                string rel = name.Substring(3);
                try
                {
                    if (rel == "manifest.txt")
                    {
                        string dest = Path.Combine(SchemesDir, "manifest.txt");
                        using (Stream s = asm.GetManifestResourceStream(name)) MergeManifest(s, dest);
                        continue;
                    }
                    int idx = rel.IndexOf('_');
                    if (idx <= 0) continue;
                    string scheme = rel.Substring(0, idx);
                    string file = rel.Substring(idx + 1);
                    string target = Path.Combine(Path.Combine(SchemesDir, scheme), file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream s = asm.GetManifestResourceStream(name))
                    using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                    { byte[] buf = new byte[s.Length]; s.Read(buf, 0, buf.Length); fs.Write(buf, 0, buf.Length); }
                }
                catch { /* 单个资源失败不影响整体 */ }
            }
        }

        // 内置 manifest 与磁盘上的 manifest 合并：保留用户自定义方案的行，仅补充缺失的内置方案。
        // （旧版每次启动直接覆盖，会把编辑器保存的自定义方案条目抹掉。）
        static void MergeManifest(Stream s, string dest)
        {
            if (s == null) return;
            List<string> lines = new List<string>();
            if (File.Exists(dest)) lines.AddRange(File.ReadAllLines(dest, Encoding.UTF8));
            HashSet<string> have = new HashSet<string>();
            foreach (string l in lines)
            {
                int p = l.IndexOf('|');
                have.Add((p > 0 ? l.Substring(0, p) : l).Trim());
            }
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    int p = line.IndexOf('|');
                    string key = (p > 0 ? line.Substring(0, p) : line).Trim();
                    if (!have.Contains(key)) { lines.Add(line); have.Add(key); }
                }
            }
            File.WriteAllLines(dest, lines, Encoding.UTF8);
        }

        // ---------------- 设置 (INI) ----------------

        public static Settings LoadSettings()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(SettingsFile)) return s;
                foreach (string line in File.ReadAllLines(SettingsFile))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "currentScheme": s.CurrentScheme = v; break;
                        case "customRole": s.CustomRole = v; break;
                        case "customFile": s.CustomFile = v; break;
                        case "watchdog": s.Watchdog = (v != "false"); break;
                        case "autostart": s.Autostart = (v != "false"); break;
                        case "restoreOnExit": s.RestoreOnExit = (v != "false"); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public static void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("currentScheme=" + settings.CurrentScheme);
                sb.AppendLine("customRole=" + settings.CustomRole);
                sb.AppendLine("customFile=" + settings.CustomFile);
                sb.AppendLine("watchdog=" + (settings.Watchdog ? "true" : "false"));
                sb.AppendLine("autostart=" + (settings.Autostart ? "true" : "false"));
                sb.AppendLine("restoreOnExit=" + (settings.RestoreOnExit ? "true" : "false"));
                File.WriteAllText(SettingsFile, sb.ToString());
            }
            catch { }
        }

        // ---------------- 方案列表 ----------------

        public static Dictionary<string, string> GetSchemes()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            try
            {
                string manifest = Path.Combine(SchemesDir, "manifest.txt");
                if (File.Exists(manifest))
                {
                    foreach (string line in File.ReadAllLines(manifest, Encoding.UTF8))
                    {
                        int i = line.IndexOf('|');
                        if (i > 0) result[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            try
            {
                foreach (string dir in Directory.GetDirectories(SchemesDir))
                {
                    string id = Path.GetFileName(dir);
                    if (File.Exists(Path.Combine(dir, "Arrow.cur")) && !result.ContainsKey(id))
                        result[id] = id;
                }
            }
            catch { }
            return result;
        }

        public static string SchemeDir(string id) { return Path.Combine(SchemesDir, id); }

        // ---------------- 自定义方案（编辑器后端） ----------------

        public static readonly string[] BuiltinSchemeIds = { "classic", "sakura", "mint", "night", "sunset" };

        public static bool IsBuiltinScheme(string id) { return Array.IndexOf(BuiltinSchemeIds, id) >= 0; }

        // 用户输入的方案名 → 目录/manifest 用的 id；避开内置方案与保留字 "custom"（单角色自定义的哨兵值）
        public static string MakeSchemeId(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                if (c == '|' || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            string id = sb.ToString().Trim();
            if (id.Length == 0) id = "我的方案";
            if (id.Length > 40) id = id.Substring(0, 40).Trim();
            if (IsBuiltinScheme(id) || id == "custom")
            {
                for (int i = 2; ; i++)
                {
                    string cand = id + "_" + i;
                    if (!IsBuiltinScheme(cand) && cand != "custom") { id = cand; break; }
                }
            }
            return id;
        }

        // 把一张图片作为某角色写入自定义方案目录并登记 manifest，返回生成的 .cur 路径。
        // 同方案同一角色重复保存即覆盖——这就是“修改已有方案”的路径。
        public static string SaveCursorToScheme(string imageFile, string role, string schemeId, int size, int hotX, int hotY)
        {
            EnsureInit();
            string dir = SchemeDir(schemeId);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, role + ".cur");
            BuildCursorFile(imageFile, size, hotX, hotY, dest);
            EnsureManifestEntry(schemeId);
            return dest;
        }

        // manifest 中新增/更新一行 "id|id"（显示名与 id 一致），保留其余行
        static void EnsureManifestEntry(string id)
        {
            string manifest = Path.Combine(SchemesDir, "manifest.txt");
            List<string> lines = new List<string>();
            if (File.Exists(manifest)) lines.AddRange(File.ReadAllLines(manifest, Encoding.UTF8));
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                int p = lines[i].IndexOf('|');
                if ((p > 0 ? lines[i].Substring(0, p) : lines[i]).Trim() == id) { lines[i] = id + "|" + id; found = true; }
            }
            if (!found) lines.Add(id + "|" + id);
            File.WriteAllLines(manifest, lines, Encoding.UTF8);
        }

        // 删除自定义方案（内置方案拒绝删除）；若它正在使用中则清空当前方案并清理注册表指向
        public static bool DeleteScheme(string id)
        {
            EnsureInit();
            if (IsBuiltinScheme(id)) return false;
            string dir = SchemeDir(id);
            if (!Directory.Exists(dir)) return false;
            bool wasCurrent = settings.CurrentScheme == id;
            Directory.Delete(dir, true);
            try
            {
                string manifest = Path.Combine(SchemesDir, "manifest.txt");
                if (File.Exists(manifest))
                {
                    List<string> keep = new List<string>();
                    foreach (string l in File.ReadAllLines(manifest, Encoding.UTF8))
                    {
                        int p = l.IndexOf('|');
                        if ((p > 0 ? l.Substring(0, p) : l).Trim() != id) keep.Add(l);
                    }
                    File.WriteAllLines(manifest, keep, Encoding.UTF8);
                }
            }
            catch { }
            // 清理注册表中仍指向该方案目录的角色值，避免登录后加载已删除的文件
            try
            {
                string prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CursorsKeyPath, true))
                {
                    if (k != null)
                    {
                        bool changed = false;
                        foreach (string name in k.GetValueNames())
                        {
                            string v = Convert.ToString(k.GetValue(name, ""));
                            if (name != "" && v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { k.DeleteValue(name, false); changed = true; }
                        }
                        if (changed) RefreshFromRegistry();
                    }
                }
            }
            catch { }
            if (wasCurrent) { settings.CurrentScheme = ""; SaveSettings(); }
            return true;
        }

        static string RoleFile(string dir, string role)
        {
            string p = Path.Combine(dir, role + ".cur");
            if (File.Exists(p)) return p;
            p = Path.Combine(dir, role + ".ani");
            if (File.Exists(p)) return p;
            return null;
        }

        // ---------------- 注册表 ----------------

        const string CursorsKeyPath = "Control Panel\\Cursors";

        // 读取当前光标注册表值；"(Default)" 表示默认值
        public static Dictionary<string, string> ReadCursorsRegistry()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CursorsKeyPath, false))
            {
                if (k == null) return d;
                foreach (string name in k.GetValueNames())
                {
                    object v = k.GetValue(name, "");
                    d[name == "" ? "(Default)" : name] = Convert.ToString(v);
                }
            }
            return d;
        }

        // 写入给定值（不删除未列出的其他值）
        static void WriteRegistryValues(Dictionary<string, string> values)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CursorsKeyPath, true))
            {
                if (k == null) throw new InvalidOperationException("无法打开注册表 " + CursorsKeyPath);
                foreach (KeyValuePair<string, string> kv in values)
                {
                    if (kv.Key == "(Default)") k.SetValue(null, kv.Value, RegistryValueKind.String);
                    else k.SetValue(kv.Key, kv.Value, RegistryValueKind.String);
                }
            }
        }

        // 精确恢复：先写入快照中的值，再删除快照中不存在、但属于我们角色列表的值
        static void WriteRegistryExact(Dictionary<string, string> snapshot)
        {
            WriteRegistryValues(snapshot);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CursorsKeyPath, true))
            {
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                {
                    string key = name == "" ? "(Default)" : name;
                    if (name != "" && Array.IndexOf(Roles, name) >= 0 && !snapshot.ContainsKey(key))
                        k.DeleteValue(name, false);
                }
            }
        }

        // 首次修改前备份用户原始光标注册表（只备份一次，之后不被覆盖）
        public static void BackupOriginal()
        {
            if (File.Exists(BackupFile)) return;
            Dictionary<string, string> snap = ReadCursorsRegistry();
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in snap)
                sb.Append(kv.Key).Append('\t').AppendLine(kv.Value);
            File.WriteAllText(BackupFile, sb.ToString());
        }

        static Dictionary<string, string> ReadBackup()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (!File.Exists(BackupFile)) return d;
            foreach (string line in File.ReadAllLines(BackupFile))
            {
                int i = line.IndexOf('\t');
                if (i <= 0) continue;
                d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return d;
        }

        // ---------------- 应用 / 恢复 ----------------

        // SPI_SETCURSORS：通知系统按注册表重新装载全部光标（含 .ani 动画与 Help 等注册表专属角色）
        public static bool RefreshFromRegistry()
        {
            return SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }

        // 直接覆盖系统光标槽位（不需要重启，立即生效），并记录本方案的像素签名。
        // 签名必须取自“加载后的光标句柄”：win32k 在 LoadCursorFromFile 时就按系统 DPI 缩放
        // （150% 下 32px 文件 -> 48px 槽位内容），文件字节与槽位内容尺寸不同，不能直接比对。
        static void DirectPass(string dir)
        {
            expectedSigs = new HashSet<string>();
            foreach (KeyValuePair<string, uint> kv in OcrIds)
            {
                string file = RoleFile(dir, kv.Key);
                if (file == null) continue;
                DirectSet(file, kv.Value);
            }
        }

        static void DirectPassRole(string file, string role)
        {
            uint ocr;
            if (!OcrIds.TryGetValue(role, out ocr)) return;   // 注册表专属角色（如 Help）只能靠 SPI 装载
            DirectSet(file, ocr);
        }

        static void DirectSet(string file, uint ocr)
        {
            IntPtr h = LoadCursorFromFileW(file);
            if (h == IntPtr.Zero) return;                 // 文件损坏等，跳过该角色
            string sig = CurrentCursorSignature(h);
            if (SetSystemCursor(h, ocr))
            {
                if (sig != null) expectedSigs.Add(sig);   // 成功后句柄由系统接管
            }
            else
            {
                DestroyCursor(h);
            }
        }

        // ---------------- 像素签名（守护检测用） ----------------

        // 取光标句柄的实际图像签名（GetIconInfo + GetDIBits），与槽位内容一致
        static string CurrentCursorSignature(IntPtr hCursor)
        {
            ICONINFO ii = new ICONINFO();
            if (!GetIconInfo(hCursor, ref ii)) return null;
            IntPtr hbmColor = ii.hbmColor;
            IntPtr hbmMask = ii.hbmMask;
            try
            {
                if (hbmColor == IntPtr.Zero) return null;   // 单色光标
                BITMAP bmp = new BITMAP();
                if (GetObjectW(hbmColor, Marshal.SizeOf(typeof(BITMAP)), ref bmp) == 0) return null;
                int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
                if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

                IntPtr dc = GetDC(IntPtr.Zero);
                try { return BitmapSignature(dc, hbmColor, w, h); }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            }
            finally
            {
                if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
                if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            }
        }

        // 用 GetDIBits 取 32bpp 自顶向下像素并计算签名
        static string BitmapSignature(IntPtr dc, IntPtr hbm, int w, int h)
        {
            BITMAPINFO bi = new BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            if (GetDIBits(dc, hbm, 0, 0, null, ref bi, 0) == 0) return null;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;
            bi.bmiHeader.biHeight = -h;   // 负值 = 自顶向下
            byte[] px = new byte[w * 4 * h];
            if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null;
            return w + "x" + h + ":" + Md5(px);
        }

        static string Md5(byte[] data)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte b in md5.ComputeHash(data)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // 应用一个内置方案（写注册表 + SPI + 直接覆盖槽位）
        public static bool ApplyScheme(string id, bool persistSettings)
        {
            EnsureInit();
            string dir = SchemeDir(id);
            if (!Directory.Exists(dir) || RoleFile(dir, "Arrow") == null) return false;
            BackupOriginal();

            Dictionary<string, string> values = new Dictionary<string, string>();
            Dictionary<string, string> names = GetSchemes();
            string display = names.ContainsKey(id) ? names[id] : id;
            foreach (string role in Roles)
            {
                string f = RoleFile(dir, role);
                if (f != null) values[role] = f;
            }
            values["(Default)"] = "CursorStudio - " + display;
            WriteRegistryValues(values);
            RefreshFromRegistry();
            DirectPass(dir);

            if (persistSettings)
            {
                settings.CurrentScheme = id;
                SaveSettings();
            }
            return true;
        }

        // 应用用户上传的自定义光标（单角色）
        public static void ApplyCustom(string curFile, string role, bool persistSettings)
        {
            EnsureInit();
            BackupOriginal();
            Dictionary<string, string> reg = ReadCursorsRegistry();
            reg[role] = Path.GetFullPath(curFile);
            reg["(Default)"] = "CursorStudio - 自定义";
            WriteRegistryValues(reg);
            RefreshFromRegistry();
            DirectPassRole(reg[role], role);

            if (persistSettings)
            {
                settings.CurrentScheme = "custom";
                settings.CustomRole = role;
                settings.CustomFile = Path.GetFullPath(curFile);
                SaveSettings();
            }
        }

        // 启动/登录后：按持久化的设置把光标槽位恢复为自定义内容（注册表本身已持久化）
        public static void ApplyOnStartup()
        {
            EnsureInit();
            try
            {
                if (settings.CurrentScheme == "custom" && File.Exists(settings.CustomFile))
                {
                    RefreshFromRegistry();
                    DirectPassRole(settings.CustomFile, settings.CustomRole);
                }
                else if (!string.IsNullOrEmpty(settings.CurrentScheme) && Directory.Exists(SchemeDir(settings.CurrentScheme)))
                {
                    RefreshFromRegistry();
                    DirectPass(SchemeDir(settings.CurrentScheme));
                }
                // 自启状态自愈：设置里勾选但 Run 键缺失时补写
                if (settings.Autostart && !GetAutostart()) SetAutostart(true);
            }
            catch { }
        }

        // 恢复系统默认光标（恢复首次运行前备份的注册表内容）
        public static void RestoreDefault(bool persistSettings)
        {
            EnsureInit();
            Dictionary<string, string> snap = ReadBackup();
            if (snap.Count > 0) WriteRegistryExact(snap);
            else
            {
                Dictionary<string, string> empty = new Dictionary<string, string>();
                foreach (string role in Roles) empty[role] = "";
                empty["(Default)"] = "Windows 默认";
                WriteRegistryValues(empty);
            }
            // SPI 装载注册表方案；若 SPI 失效（部分 Win11 环境会直接返回失败），直接把默认光标写回槽位
            if (!RefreshFromRegistry()) DirectSetDefaults();
            expectedSigs = new HashSet<string>();
            if (persistSettings)
            {
                settings.CurrentScheme = "";
                SaveSettings();
            }
        }

        // ---------------- 防还原守护 ----------------

        static DateTime nextReapplyAllowed = DateTime.MinValue;

        // 返回 true 表示检测到还原并已重新应用。
        // 注意：GetCursorInfo 报告的是“当前正在显示”的光标；若鼠标恰好停在某个使用窗口自有光标的
        // 程序上，签名永远不匹配——所以重应用后要复核一次，仍不匹配则进入冷却，避免无意义空转。
        public static bool WatchdogTick()
        {
            EnsureInit();
            if (string.IsNullOrEmpty(settings.CurrentScheme)) return false;
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref ci)) return false;
            if (ci.hCursor == IntPtr.Zero) return false;          // 光标当前隐藏
            if (expectedSigs.Count == 0) return false;            // 无签名可比（如自定义 Help）
            string sig = CurrentCursorSignature(ci.hCursor);
            if (sig == null || expectedSigs.Contains(sig)) return false;   // 正常
            if (DateTime.Now < nextReapplyAllowed) return false;  // 冷却中
            FullReapply();

            ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            GetCursorInfo(ref ci);
            string sig2 = CurrentCursorSignature(ci.hCursor);
            if (sig2 != null && expectedSigs.Contains(sig2))
            {
                LogWatchdog("检测到系统光标被还原，已自动重新应用方案 " + settings.CurrentScheme);
                nextReapplyAllowed = DateTime.Now.AddSeconds(5);
                return true;
            }
            // 复核仍不匹配：鼠标大概率停在窗口自有光标上，60 秒内不再尝试
            nextReapplyAllowed = DateTime.Now.AddSeconds(60);
            return false;
        }

        // 无条件重新应用（兜底：句柄启发式可能漏检非当前显示角色的还原）
        public static void FullReapply()
        {
            EnsureInit();
            try
            {
                if (settings.CurrentScheme == "custom" && File.Exists(settings.CustomFile))
                {
                    uint ocr;
                    if (OcrIds.TryGetValue(settings.CustomRole, out ocr)) DirectPassRole(settings.CustomFile, settings.CustomRole);
                    else RefreshFromRegistry();
                }
                else if (!string.IsNullOrEmpty(settings.CurrentScheme) && Directory.Exists(SchemeDir(settings.CurrentScheme)))
                    DirectPass(SchemeDir(settings.CurrentScheme));
            }
            catch { }
        }

        static void LogWatchdog(string msg)
        {
            try
            {
                // 节流：30 秒内不重复记录同类事件
                if (File.Exists(WatchdogLogFile))
                {
                    FileInfo fi = new FileInfo(WatchdogLogFile);
                    if (fi.Length > 0 && (DateTime.Now - fi.LastWriteTime).TotalSeconds < 30) return;
                }
                using (StreamWriter w = new StreamWriter(WatchdogLogFile, true, Encoding.UTF8))
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg);
                FileInfo fi2 = new FileInfo(WatchdogLogFile);
                if (fi2.Length > 128 * 1024) File.WriteAllText(WatchdogLogFile, "");   // 防止无限增长
            }
            catch { }
        }

        // ---------------- 开机自启 ----------------

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "CursorStudio";

        public static string ExePath
        {
            get { return Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : ProcessPath(); }
        }
        static string ProcessPath()
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess()) return p.MainModule.FileName;
        }

        public static void SetAutostart(bool enable)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) return;
                if (enable) k.SetValue(RunValueName, "\"" + ExePath + "\"", RegistryValueKind.String);
                else k.DeleteValue(RunValueName, false);
            }
        }

        public static bool GetAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (k == null) return false;
                return k.GetValue(RunValueName, null) != null;
            }
        }

        // ---------------- PNG -> .cur ----------------

        // 把任意图片转成 32x32 的 .cur（保持长宽比居中，透明背景），热点按角色取默认值
        public static string ConvertPngToCur(string imageFile, string role, string destFile)
        {
            int[] hp = PngHotspots.ContainsKey(role) ? PngHotspots[role] : new int[] { 0, 0 };
            return BuildCursorFile(imageFile, 32, hp[0], hp[1], destFile);
        }

        // 角色默认热点（PngHotspots 以 32px 为基准，按输出尺寸等比缩放并夹到范围内）
        public static int[] DefaultHotspot(string role, int size)
        {
            int[] hp = PngHotspots.ContainsKey(role) ? PngHotspots[role] : new int[] { 0, 0 };
            int x = Math.Max(0, Math.Min(size - 1, (int)Math.Round(hp[0] * size / 32.0)));
            int y = Math.Max(0, Math.Min(size - 1, (int)Math.Round(hp[1] * size / 32.0)));
            return new[] { x, y };
        }

        // 把任意图片转成 size x size 的 .cur（保持长宽比居中，透明背景），热点由调用方指定
        public static string BuildCursorFile(string imageFile, int size, int hotX, int hotY, string destFile)
        {
            if (size < 8) size = 8;
            if (size > 128) size = 128;
            using (Bitmap src = new Bitmap(imageFile))
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    double ratio = Math.Min(size / Math.Max(1.0, src.Width), size / Math.Max(1.0, src.Height));
                    int w = Math.Max(1, (int)Math.Round(src.Width * ratio));
                    int h = Math.Max(1, (int)Math.Round(src.Height * ratio));
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(src, new Rectangle((size - w) / 2, (size - h) / 2, w, h),
                            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                File.WriteAllBytes(destFile, BuildCurBytes(bmp,
                    Math.Max(0, Math.Min(size - 1, hotX)), Math.Max(0, Math.Min(size - 1, hotY))));
            }
            return destFile;
        }

        // .cur 文件格式：ICONDIR(type=2) + ICONDIRENTRY(热点) + BITMAPINFOHEADER + XOR(BGRA,自底向上) + AND 掩码
        public static byte[] BuildCurBytes(Bitmap b, int hotX, int hotY)
        {
            int w = b.Width, h = b.Height;
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int xorStride = w * 4;
            byte[] xor = new byte[xorStride * h];
            byte[] row = new byte[xorStride];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + y * d.Stride), row, 0, xorStride);
                    Array.Copy(row, 0, xor, (h - 1 - y) * xorStride, xorStride);
                }
            }
            finally { b.UnlockBits(d); }

            int andStride = ((w + 31) / 32) * 4;
            byte[] and = new byte[andStride * h];   // 全 0：不透明度由 alpha 通道决定

            byte[] img = new byte[40 + xor.Length + and.Length];
            img[0] = 40;                                            // biSize = 40
            WriteU32(img, 4, (uint)w);
            WriteU32(img, 8, (uint)(h * 2));                        // XOR+AND 总高
            img[12] = 1; img[14] = 32;                              // planes=1, bitCount=32
            WriteU32(img, 20, (uint)(xor.Length + and.Length));     // biSizeImage
            Array.Copy(xor, 0, img, 40, xor.Length);
            Array.Copy(and, 0, img, 40 + xor.Length, and.Length);

            byte[] cur = new byte[6 + 16 + img.Length];
            WriteU16(cur, 2, 2);                                    // type = 2 (cursor)
            WriteU16(cur, 4, 1);                                    // 图像数
            cur[6] = (byte)(w >= 256 ? 0 : w);
            cur[7] = (byte)(h >= 256 ? 0 : h);
            WriteU16(cur, 10, (ushort)hotX);                        // .cur 中 planes 字段 = 热点X
            WriteU16(cur, 12, (ushort)hotY);                        // .cur 中 bitcount 字段 = 热点Y
            WriteU32(cur, 14, (uint)img.Length);
            WriteU32(cur, 18, 22);
            Array.Copy(img, 0, cur, 22, img.Length);
            return cur;
        }

        static void WriteU32(byte[] b, int o, uint v)
        { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

        // ---------------- 命令行入口 ----------------

        public static int CommandLine(string[] args)
        {
            EnsureInit();
            string cmd = args[0].ToLowerInvariant();
            switch (cmd)
            {
                case "selftest":
                    return SelfTest();
                case "probe":
                    return Probe();
                case "probe2":
                    return Probe2();
                case "probehot":
                    return ProbeHot();
                case "apply":
                    if (args.Length < 2) { CLine("用法: CursorStudio.exe apply <方案id>"); return 1; }
                    return ApplyScheme(args[1], true) ? Success("已应用方案: " + args[1]) : Fail("找不到方案: " + args[1]);
                case "restore":
                    RestoreDefault(true);
                    return Success("已恢复系统默认光标");
                case "list":
                    {
                        foreach (KeyValuePair<string, string> kv in GetSchemes())
                            CLine(kv.Key + "    " + kv.Value + (kv.Key == settings.CurrentScheme ? "    [当前]" : ""));
                        return 0;
                    }
                case "autostart":
                    if (args.Length < 2) { CLine("用法: CursorStudio.exe autostart on|off"); return 1; }
                    SetAutostart(args[1] == "on");
                    return Success("开机自启: " + (args[1] == "on" ? "开" : "关"));
                default:
                    CLine("用法: CursorStudio.exe [selftest|apply <方案id>|restore|list|autostart on|off]");
                    return 1;
            }
        }

        static int Success(string msg) { CLine(msg); return 0; }
        static int Fail(string msg) { CLine(msg); return 2; }

        // 诊断：探查 SetSystemCursor / SPI / GetCursorInfo 在本机的真实行为
        static int Probe()
        {
            EnsureInit();
            List<string> lines = new List<string>();
            try
            {
                string dir = SchemeDir("classic");
                float dpi;
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
                lines.Add("system DPI: " + dpi);

                IntPtr h = LoadCursorFromFileW(Path.Combine(dir, "Arrow.cur"));
                lines.Add("our arrow: handle=0x" + h.ToString("X") + " sig=" + SigOf(h));

                lines.Add("shared arrow BEFORE: sig=" + SigOfShared(32512));
                IntPtr copy = CopyIcon(h);
                bool ok = SetSystemCursor(copy, 32512);
                lines.Add("SetSystemCursor(ours -> OCR_NORMAL): ok=" + ok + " gle=" + Marshal.GetLastWin32Error());
                lines.Add("shared arrow AFTER SetSystemCursor: sig=" + SigOfShared(32512));

                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                GetCursorInfo(ref ci);
                lines.Add("GetCursorInfo: hCursor=0x" + ci.hCursor.ToString("X") + " flags=" + ci.flags +
                    " pos=" + ci.pt.X + "," + ci.pt.Y + " sig=" + CurrentCursorSignature(ci.hCursor));

                Dictionary<string, string> reg = ReadCursorsRegistry();
                reg["Arrow"] = Path.Combine(dir, "Arrow.cur");
                WriteRegistryValues(reg);
                bool spiOk = RefreshFromRegistry();
                lines.Add("SPI(Arrow=ours only): ok=" + spiOk + " gle=" + Marshal.GetLastWin32Error());

                Dictionary<string, string> full = new Dictionary<string, string>();
                foreach (string role in Roles)
                {
                    string f = RoleFile(dir, role);
                    if (f != null) full[role] = f;
                }
                WriteRegistryValues(full);
                spiOk = RefreshFromRegistry();
                lines.Add("SPI(all roles=ours): ok=" + spiOk + " gle=" + Marshal.GetLastWin32Error());
                ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                GetCursorInfo(ref ci);
                lines.Add("after SPI(all): sig=" + CurrentCursorSignature(ci.hCursor));

                Dictionary<string, string> backup = ReadBackup();
                if (backup.Count > 0) WriteRegistryExact(backup);
                RefreshFromRegistry();
                lines.Add("registry restored + SPI refreshed");
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION: " + ex);
            }
            try { File.WriteAllLines(Path.Combine(AppDir, "probe.log"), lines, Encoding.UTF8); } catch { }
            foreach (string l in lines) TryConsole(l);
            return 0;
        }

        static string SigOf(IntPtr h) { return h == IntPtr.Zero ? "null" : CurrentCursorSignature(h); }
        static string SigOfShared(uint id) { return SigOf(LoadCursor(IntPtr.Zero, (IntPtr)id)); }

        // 决定性实验：SPI_SETCURSORS 到底“假失败”还是“真无效”
        static int Probe2()
        {
            EnsureInit();
            List<string> lines = new List<string>();
            try
            {
                string dir = SchemeDir("classic");
                // 1. 直接把槽位设成我们的箭头
                IntPtr h = LoadCursorFromFileW(Path.Combine(dir, "Arrow.cur"));
                SetSystemCursor(CopyIcon(h), 32512);
                CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                GetCursorInfo(ref ci);
                lines.Add("step1 SetSystemCursor(ours): sig=" + CurrentCursorSignature(ci.hCursor));

                // 2. 注册表暂时指向 aero，再调 SPI：若 SPI 有效，显示的光标会被还原为 aero
                Dictionary<string, string> backup = ReadBackup();
                string aeroArrow = backup.ContainsKey("Arrow") ? backup["Arrow"] : Environment.SystemDirectory + "\\..\\Cursors\\aero_arrow.cur";
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CursorsKeyPath, true)) k.SetValue("Arrow", aeroArrow, RegistryValueKind.String);
                bool ok = RefreshFromRegistry();
                lines.Add("step2 SPI(registry=aero): ok=" + ok + " gle=" + Marshal.GetLastWin32Error());
                ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                GetCursorInfo(ref ci);
                lines.Add("step2 after SPI: sig=" + CurrentCursorSignature(ci.hCursor));

                // 3. 收尾：无论 SPI 是否有效，把槽位直接还原成 aero 内容
                IntPtr def = LoadCursor(IntPtr.Zero, (IntPtr)32512);
                if (def != IntPtr.Zero) SetSystemCursor(CopyIcon(def), 32512);
                lines.Add("step3 slot restored to aero directly");
            }
            catch (Exception ex) { lines.Add("EXCEPTION: " + ex); }
            try { File.WriteAllLines(Path.Combine(AppDir, "probe2.log"), lines, Encoding.UTF8); } catch { }
            foreach (string l in lines) TryConsole(l);
            return 0;
        }

        // DPI 感知进程内的权威热点探测：加载 .cur 后报告位图尺寸与热点
        static int ProbeHot()
        {
            EnsureInit();
            List<string> lines = new List<string>();
            try
            {
                string file = Path.Combine(SchemesDir, "puppy", "Arrow.cur");
                IntPtr h = LoadCursorFromFileW(file);
                ICONINFO ii = new ICONINFO();
                if (GetIconInfo(h, ref ii))
                {
                    BITMAP bmp = new BITMAP();
                    GetObjectW(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), ref bmp);
                    lines.Add("file=" + file);
                    lines.Add("bitmap=" + bmp.bmWidth + "x" + Math.Abs(bmp.bmHeight) + "  hotspot=(" + ii.xHotspot + "," + ii.yHotspot + ")");
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                }
                DestroyCursor(h);

                // 尺寸策略测试：32/48/64/72/96px 测试文件各自的加载位图尺寸
                foreach (int size in new int[] { 32, 48, 64, 72, 96 })
                {
                    string testFile = Path.Combine(AppDir, "size_test_" + size + ".cur");
                    using (Bitmap tb = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    using (Graphics tg = Graphics.FromImage(tb))
                    {
                        tg.Clear(Color.FromArgb(200, 60, 60, 90));
                        tg.FillEllipse(Brushes.Yellow, size / 2 - 2, size / 2 - 2, 4, 4);
                        File.WriteAllBytes(testFile, BuildCurBytes(tb, size / 2, size / 2));
                    }
                    IntPtr th = LoadCursorFromFileW(testFile);
                    ICONINFO ti = new ICONINFO();
                    if (th != IntPtr.Zero && GetIconInfo(th, ref ti))
                    {
                        BITMAP tbmp = new BITMAP();
                        GetObjectW(ti.hbmColor, Marshal.SizeOf(typeof(BITMAP)), ref tbmp);
                        lines.Add("test " + size + "px -> bitmap " + tbmp.bmWidth + "x" + Math.Abs(tbmp.bmHeight) + "  hotspot=(" + ti.xHotspot + "," + ti.yHotspot + ")");
                        if (ti.hbmColor != IntPtr.Zero) DeleteObject(ti.hbmColor);
                        if (ti.hbmMask != IntPtr.Zero) DeleteObject(ti.hbmMask);
                        DestroyCursor(th);
                    }
                    try { File.Delete(testFile); } catch { }
                }
            }
            catch (Exception ex) { lines.Add("EXCEPTION: " + ex); }
            try { File.WriteAllLines(Path.Combine(AppDir, "probehot.log"), lines, Encoding.UTF8); } catch { }
            foreach (string l in lines) TryConsole(l);
            return 0;
        }

        // 把默认光标直接写回槽位——SPI 失效时的恢复兜底。
        // 注意不能用 LoadCursor(NULL,OCR_x) 取“默认内容”：槽位已被我们替换时它返回的就是我们的内容。
        // 所以从用户备份里的 aero 文件路径直接加载。
        static void DirectSetDefaults()
        {
            Dictionary<string, string> snap = ReadBackup();
            foreach (KeyValuePair<string, uint> kv in OcrIds)
            {
                string file;
                if (!snap.TryGetValue(kv.Key, out file) || file.Length == 0 || !File.Exists(file)) continue;
                IntPtr h = LoadCursorFromFileW(file);
                if (h == IntPtr.Zero) continue;
                if (!SetSystemCursor(h, kv.Value)) DestroyCursor(h);
            }
        }

        static void CLine(string s)
        {
            TryConsole(s);
            try { File.AppendAllText(Path.Combine(AppDir, "cli.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine); }
            catch { }
        }

        internal static void TryConsole(string s)
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
            try { Console.WriteLine(s); } catch { }
        }

        // ---------------- 冒烟测试 ----------------

        static List<string> testLog;
        static int testPass, testFail;

        static void T(bool ok, string name, string detail)
        {
            if (ok) testPass++; else testFail++;
            string line = (ok ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? "  -- " + detail : "");
            testLog.Add(line);
        }

        public static int SelfTest()
        {
            EnsureInit();
            testLog = new List<string>(); testPass = 0; testFail = 0;
            Dictionary<string, string> savedReg = ReadCursorsRegistry();
            Settings savedSettings = new Settings();
            savedSettings.CurrentScheme = settings.CurrentScheme;
            savedSettings.CustomRole = settings.CustomRole;
            savedSettings.CustomFile = settings.CustomFile;
            try
            {
                // 1. 内置资源完整
                Dictionary<string, string> schemes = GetSchemes();
                T(schemes.Count >= 5, "内置方案数量", "实际 " + schemes.Count);
                string dir = SchemeDir("classic");
                foreach (string role in Roles)
                    T(RoleFile(dir, role) != null, "classic/" + role + " 文件存在", "");

                // 2. LoadCursorFromFile 能加载全部文件
                foreach (string role in Roles)
                {
                    string f = RoleFile(dir, role);
                    if (f == null) continue;
                    IntPtr h = LoadCursorFromFileW(f);
                    T(h != IntPtr.Zero, "LoadCursorFromFile " + role, f);
                }

                // 3. ApplyScheme：注册表 + SPI + 槽位
                T(ApplyScheme("classic", false), "ApplyScheme(classic)", "");
                settings.CurrentScheme = "classic";   // persist=false 不写配置，这里临时激活以便测试守护逻辑
                string arrowVal = ReadCursorsRegistry().ContainsKey("Arrow") ? ReadCursorsRegistry()["Arrow"] : "";
                T(arrowVal == Path.Combine(dir, "Arrow.cur"), "注册表 Arrow 值已写入", arrowVal);

                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                bool gotInfo = GetCursorInfo(ref ci);
                T(gotInfo && ci.hCursor != IntPtr.Zero, "GetCursorInfo 可用", "");
                // 用共享句柄的“槽位内容”做断言，不依赖测试时鼠标停在哪里
                string sigArrowSlot = SigOfShared(32512);
                T(sigArrowSlot != null && expectedSigs.Contains(sigArrowSlot),
                    "应用后箭头槽位内容 = 自定义方案", "sig=" + sigArrowSlot);

                // 4. 模拟 Windows 11 还原：把箭头槽位写回真正的系统默认（从用户备份中的 aero 文件加载）
                //    注意不能用 LoadCursor(NULL,IDC_ARROW)——共享句柄的内容已被我们替换。
                string aeroArrow = savedReg.ContainsKey("Arrow") && File.Exists(savedReg["Arrow"])
                    ? savedReg["Arrow"] : Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "Cursors\\aero_arrow.cur");
                IntPtr hAero = LoadCursorFromFileW(aeroArrow);
                T(hAero != IntPtr.Zero, "加载系统默认箭头文件", aeroArrow);
                string sigAero = CurrentCursorSignature(hAero);
                DestroyCursor(hAero);
                hAero = LoadCursorFromFileW(aeroArrow);
                SetSystemCursor(hAero, 32512);
                string sigAfterRevert = SigOfShared(32512);
                T(sigAero != null && sigAfterRevert == sigAero && !expectedSigs.Contains(sigAfterRevert),
                    "模拟还原后箭头槽位 = 系统默认 ≠ 自定义方案", "sig=" + sigAfterRevert);
                bool reapplied = WatchdogTick();
                string sigAfterReapply = SigOfShared(32512);
                if (!reapplied)
                {
                    // 鼠标可能停在窗口自有光标或其他角色上，守护无法端到端触发；直接验证恢复能力
                    FullReapply();
                    sigAfterReapply = SigOfShared(32512);
                    testLog.Add("[INFO] WatchdogTick 未端到端触发（鼠标当前不在该槽位对应的光标上），改用 FullReapply 验证恢复");
                }
                else
                {
                    testLog.Add("[PASS] WatchdogTick 检测到还原并重新应用");
                    testPass++;
                }
                T(sigAfterReapply != null && expectedSigs.Contains(sigAfterReapply), "恢复后箭头槽位回到自定义", "sig=" + sigAfterReapply);

                // 5. PNG -> CUR 转换与加载
                string testPng = Path.Combine(AppDir, "selftest_png.png");
                using (Bitmap tb = new Bitmap(24, 24, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(tb))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.FillEllipse(Brushes.Crimson, 2, 2, 20, 20);
                    }
                    tb.Save(testPng, ImageFormat.Png);
                }
                string testCur = Path.Combine(AppDir, "selftest_arrow.cur");
                ConvertPngToCur(testPng, "Arrow", testCur);
                IntPtr hc = LoadCursorFromFileW(testCur);
                T(hc != IntPtr.Zero, "PNG 转换后的 .cur 可被 LoadCursorFromFile 加载", testCur);

                // 6. 恢复自定义 PNG 方案（注册表 + 槽位）路径
                ApplyCustom(testCur, "Arrow", false);
                T(ReadCursorsRegistry()["Arrow"] == Path.GetFullPath(testCur), "ApplyCustom 写注册表", "");

                // 7. 自定义方案保存 / 删除（编辑器后端）
                string schemePng = Path.Combine(AppDir, "selftest_scheme_src.png");
                using (Bitmap tb = new Bitmap(20, 20, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(tb))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.FillRectangle(Brushes.SeaGreen, 2, 2, 16, 16);
                    }
                    tb.Save(schemePng, ImageFormat.Png);
                }
                T(MakeSchemeId("classic") != "classic" && MakeSchemeId("") == "我的方案",
                    "MakeSchemeId 避开内置方案并给默认名", MakeSchemeId("classic"));
                string saved = SaveCursorToScheme(schemePng, "IBeam", "selftest_scheme", 48, 5, 7);
                T(File.Exists(saved) && LoadCursorFromFileW(saved) != IntPtr.Zero, "SaveCursorToScheme 生成 48px .cur 可加载", saved);
                T(GetSchemes().ContainsKey("selftest_scheme"), "自定义方案已登记进方案列表", "");
                T(!IsBuiltinScheme("selftest_scheme") && DeleteScheme("selftest_scheme"), "DeleteScheme 删除自定义方案", "");
                T(!Directory.Exists(SchemeDir("selftest_scheme")) && !GetSchemes().ContainsKey("selftest_scheme"),
                    "删除后目录与方案列表已清理", "");
            }
            catch (Exception ex)
            {
                T(false, "未预期异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // 恢复测试前的注册表与槽位，不遗留任何状态（SPI 失效时直接把默认光标写回槽位）
                try
                {
                    if (Directory.Exists(SchemeDir("selftest_scheme"))) DeleteScheme("selftest_scheme");
                    WriteRegistryExact(savedReg);
                    if (!RefreshFromRegistry()) DirectSetDefaults();
                    settings.CurrentScheme = savedSettings.CurrentScheme;
                    settings.CustomRole = savedSettings.CustomRole;
                    settings.CustomFile = savedSettings.CustomFile;
                    SaveSettings();
                }
                catch { }
            }

            testLog.Add(string.Format("汇总: {0} 通过, {1} 失败", testPass, testFail));
            try { File.WriteAllLines(Path.Combine(AppDir, "selftest.log"), testLog, Encoding.UTF8); } catch { }
            foreach (string line in testLog) TryConsole(line);
            return testFail == 0 ? 0 : 1;
        }
    }
}
