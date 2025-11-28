using System;
using System.IO;
using System.Windows.Forms;

namespace TbEinkSuperFlushTurbo
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 创建调试输出文件
            string debugFile = Path.Combine(AppContext.BaseDirectory, "debug_output.txt");
            try
            {
                // 全局异常捕获
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    var ex = (Exception)e.ExceptionObject;
                    File.AppendAllText(debugFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FATAL UnhandledException: {ex}{Environment.NewLine}");
                };
                Application.ThreadException += (s, e) =>
                {
                    File.AppendAllText(debugFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FATAL ThreadException: {e.Exception}{Environment.NewLine}");
                };

                File.AppendAllText(debugFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor started{Environment.NewLine}");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                File.AppendAllText(debugFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Fatal exception in Main: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
                File.AppendAllText(debugFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] StackTrace: {ex.StackTrace}{Environment.NewLine}");
                MessageBox.Show($"Fatal error: {ex.Message}\n\nHRESULT: 0x{ex.HResult:X8}\n\n{ex.StackTrace}", "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}