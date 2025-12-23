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
            Console.WriteLine("=== DPI Detection Test Started ===");
            
            try
            {
                // Read configuration file settings
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
                
                Console.WriteLine($"DPI Detection Mode: {dpiMode}");
                Console.WriteLine($"Selected Display Index: {selectedOutput}");
                Console.WriteLine($"Forced DPI Scale: {forceDpiScale}");
                Console.WriteLine();
                
                // Get all display information
                var allScreens = Screen.AllScreens;
                Console.WriteLine($"Detected {allScreens.Length} displays:");
                
                for (int i = 0; i < allScreens.Length; i++)
                {
                    var screen = allScreens[i];
                    Console.WriteLine($"\nDisplay {i}:");
                    Console.WriteLine($"  Device Name: {screen.DeviceName}");
                    Console.WriteLine($"  Primary: {screen.Primary}");
                    Console.WriteLine($"  Bounds: {screen.Bounds}");
                    Console.WriteLine($"  Working Area: {screen.WorkingArea}");
                    
                    // Try to get DPI for this display
                    try
                    {
                        var dpi = GetMonitorDpiByIndex(i);
                        Console.WriteLine($"  DPI: {dpi.dpiX}x{dpi.dpiY} (Scale: {dpi.dpiX/96:F2})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  DPI Detection Failed: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"\nSystem DPI (Primary Display):");
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    Console.WriteLine($"  DPI: {graphics.DpiX}x{graphics.DpiY} (Scale: {graphics.DpiX/96:F2})");
                }
                
                // Show information based on configuration mode
                Console.WriteLine($"\nCurrent Configuration Mode Analysis:");
                switch (dpiMode)
                {
                    case "All":
                        Console.WriteLine("- Will detect DPI for all displays");
                        Console.WriteLine("- But still use primary display DPI as system DPI");
                        Console.WriteLine("- Suitable for multi-display environment debugging");
                        break;
                    case "Primary":
                        Console.WriteLine("- Only detect primary display DPI");
                        Console.WriteLine("- Suitable for single display or primary display priority scenarios");
                        break;
                    case "Auto":
                    default:
                        Console.WriteLine("- Auto detection mode");
                        Console.WriteLine("- Use primary display DPI as system DPI");
                        Console.WriteLine("- Suitable for most scenarios");
                        break;
                }
                
                if (forceDpiScale > 0)
                {
                    Console.WriteLine($"\nNote: Forced DPI Scale is set to {forceDpiScale}, will override system detected value");
                }
                
                Console.WriteLine("\n=== DPI Detection Test Completed ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DPI Detection Test Failed: {ex.Message}");
                Console.WriteLine($"Error Details: {ex}");
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
                    
                    // Try to create Graphics object for specific display to get its DPI
                    try
                    {
                        // Get display bounds rectangle
                        var bounds = targetScreen.Bounds;
                        
                        // Create temporary window handle to get display DPI
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
                        // If cannot get specific DPI, use system DPI
                    }
                    
                    // Use system DPI as fallback
                    using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        return (graphics.DpiX, graphics.DpiY);
                    }
                }
                
                return (96f, 96f); // Default value
            }
            catch
            {
                return (96f, 96f); // Default value
            }
        }
    }
}
