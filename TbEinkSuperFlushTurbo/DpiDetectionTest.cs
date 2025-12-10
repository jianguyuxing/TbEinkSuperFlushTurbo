using System;
using System.Drawing;
using System.Windows.Forms;
using System.Configuration;

namespace TbEinkSuperFlushTurbo
{
    public class DpiDetectionTest
    {
        public static void TestDpiDetection()
        {
            Console.WriteLine("=== DPI检测测试开始 ===");
            
            try
            {
                // 读取配置文件设置
                string dpiMode = ConfigurationManager.AppSettings["DpiDetectionMode"] ?? "Auto";
                int selectedOutput = 0;
                float forceDpiScale = 0f;
                
                try
                {
                    if (ConfigurationManager.AppSettings["SelectedOutputIndex"] != null)
                        int.TryParse(ConfigurationManager.AppSettings["SelectedOutputIndex"], out selectedOutput);
                    
                    if (ConfigurationManager.AppSettings["ForceDpiScale"] != null)
                        float.TryParse(ConfigurationManager.AppSettings["ForceDpiScale"], out forceDpiScale);
                }
                catch { }
                
                Console.WriteLine($"DPI检测模式: {dpiMode}");
                Console.WriteLine($"选择的显示器索引: {selectedOutput}");
                Console.WriteLine($"强制DPI缩放: {forceDpiScale}");
                Console.WriteLine();
                
                // 获取所有显示器信息
                var allScreens = Screen.AllScreens;
                Console.WriteLine($"检测到 {allScreens.Length} 个显示器:");
                
                for (int i = 0; i < allScreens.Length; i++)
                {
                    var screen = allScreens[i];
                    Console.WriteLine($"\n显示器 {i}:");
                    Console.WriteLine($"  设备名称: {screen.DeviceName}");
                    Console.WriteLine($"  主显示器: {screen.Primary}");
                    Console.WriteLine($"  边界: {screen.Bounds}");
                    Console.WriteLine($"  工作区: {screen.WorkingArea}");
                    
                    // 尝试获取该显示器的DPI
                    try
                    {
                        var dpi = GetMonitorDpiByIndex(i);
                        Console.WriteLine($"  DPI: {dpi.dpiX}x{dpi.dpiY} (缩放: {dpi.dpiX/96:F2})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  DPI检测失败: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"\n系统DPI (主显示器):");
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    Console.WriteLine($"  DPI: {graphics.DpiX}x{graphics.DpiY} (缩放: {graphics.DpiX/96:F2})");
                }
                
                // 根据配置模式显示信息
                Console.WriteLine($"\n当前配置模式分析:");
                switch (dpiMode)
                {
                    case "All":
                        Console.WriteLine("- 将检测所有显示器的DPI");
                        Console.WriteLine("- 但仍使用主显示器的DPI作为系统DPI");
                        Console.WriteLine("- 适用于多显示器环境调试");
                        break;
                    case "Primary":
                        Console.WriteLine("- 只检测主显示器的DPI");
                        Console.WriteLine("- 适用于单显示器或主显示器优先的场景");
                        break;
                    case "Auto":
                    default:
                        Console.WriteLine("- 自动检测模式");
                        Console.WriteLine("- 使用主显示器的DPI作为系统DPI");
                        Console.WriteLine("- 适用于大多数场景");
                        break;
                }
                
                if (forceDpiScale > 0)
                {
                    Console.WriteLine($"\n注意: 强制DPI缩放已设置为 {forceDpiScale}，将覆盖系统检测值");
                }
                
                Console.WriteLine("\n=== DPI检测测试完成 ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DPI检测测试失败: {ex.Message}");
                Console.WriteLine($"错误详情: {ex}");
            }
        }
        
        private static (float dpiX, float dpiY) GetMonitorDpiByIndex(int monitorIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (monitorIndex >= 0 && monitorIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[monitorIndex];
                    
                    // 尝试为特定显示器创建Graphics对象以获取其DPI
                    try
                    {
                        // 获取显示器的边界矩形
                        var bounds = targetScreen.Bounds;
                        
                        // 创建临时窗口句柄来获取该显示器的DPI
                        var tempHwnd = NativeMethods.CreateWindowEx(
                            0, "STATIC", "", 0,
                            bounds.Left, bounds.Top, 1, 1,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        
                        if (tempHwnd != IntPtr.Zero)
                        {
                            try
                            {
                                using (var graphics = Graphics.FromHwnd(tempHwnd))
                                {
                                    float dpiX = graphics.DpiX;
                                    float dpiY = graphics.DpiY;
                                    return (dpiX, dpiY);
                                }
                            }
                            finally
                            {
                                NativeMethods.DestroyWindow(tempHwnd);
                            }
                        }
                    }
                    catch
                    {
                        // 如果无法获取特定DPI，使用系统DPI
                    }
                    
                    // 使用系统DPI作为后备
                    using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        return (graphics.DpiX, graphics.DpiY);
                    }
                }
                
                return (96f, 96f); // 默认值
            }
            catch
            {
                return (96f, 96f); // 默认值
            }
        }
    }
}