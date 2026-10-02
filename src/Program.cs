// Program.cs - 入口：无参数启动 GUI，带参数走命令行模式
using System;
using System.Threading;
using System.Windows.Forms;

namespace CursorStudio
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                return CursorCore.CommandLine(args);
            }

            bool createdNew;
            using (Mutex m = new Mutex(true, "CursorStudio.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("光标工坊已在运行中（请查看系统托盘图标）。", "光标工坊",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            return 0;
        }
    }
}
