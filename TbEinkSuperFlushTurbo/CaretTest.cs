using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TbEinkSuperFlushTurbo
{
    public class CaretTest
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCaretPos(ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
         private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
 
         [DllImport("user32.dll")]
         private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
 
         [DllImport("user32.dll")]
         private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, ref IntPtr wParam, ref IntPtr lParam);
 
         // EM_GETSEL 消息常量
         private const uint EM_GETSEL = 0x00B0;

        // EM_POSFROMCHAR 消息常量
        private const uint EM_POSFROMCHAR = 0x00D6;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        public static void RunTest()
        {
            var result = new StringBuilder();
            result.AppendLine("=== 光标检测测试开始 ===");
            
            try
            {
                // 设置DPI感知
                SetProcessDPIAware();
                
                // 输出当前状态
                var foregroundWindow = GetForegroundWindow();
                var focusedControl = GetFocus();
                
                result.AppendLine($"活动窗口句柄: {foregroundWindow}");
                result.AppendLine($"焦点控件句柄: {focusedControl}");
                
                if (foregroundWindow != IntPtr.Zero)
                {
                    result.AppendLine($"当前有活动窗口");
                }
                else
                {
                    result.AppendLine($"当前没有活动窗口");
                }
                
                if (focusedControl != IntPtr.Zero)
                {
                    result.AppendLine($"当前有焦点控件");
                }
                else
                {
                    result.AppendLine($"当前没有焦点控件");
                }
                
                // 测试方法1: 直接GetCaretPos
                result.AppendLine(TestDirectGetCaretPosString());
                
                // 测试方法2: 跨进程AttachThreadInput
                result.AppendLine(TestCrossProcessCaretString());
                
                // 测试方法3: GetGUIThreadInfo
                result.AppendLine(TestGuiThreadInfoString());
                
                // 测试方法4: EM_POSFROMCHAR消息
                result.AppendLine(TestEMPosFromCharString());
                
                // 输出DPI信息
                uint systemDpi = GetDpiForSystem();
                result.AppendLine($"系统DPI: {systemDpi}, 缩放比例: {systemDpi / 96.0f:F2}");
                
                result.AppendLine("=== 光标检测测试完成 ===");
                
                // 显示结果
                ShowTestResult(result.ToString());
            }
            catch (Exception ex)
            {
                string errorMsg = $"测试过程中出现异常: {ex.Message}\n\n堆栈跟踪: {ex.StackTrace}";
                ShowTestResult(errorMsg);
            }
        }
        
        private static void ShowTestResult(string message)
        {
            try
            {
                // 尝试使用MessageBox显示结果
                MessageBox.Show(message, "光标检测测试结果", MessageBoxButtons.OK, MessageBoxIcon.None);
            }
            catch
            {
                // 如果MessageBox失败，输出到调试器
                System.Diagnostics.Debug.WriteLine(message);
            }
        }

        private static string TestDirectGetCaretPosString()
        {
            var result = new StringBuilder();
            result.AppendLine("\n--- 测试方法1: 直接GetCaretPos ---");
            
            try
            {
                var caretPoint = new POINT();
                bool success = GetCaretPos(ref caretPoint);
                
                result.AppendLine($"GetCaretPos返回: {success}");
                result.AppendLine($"光标位置: ({caretPoint.X}, {caretPoint.Y})");
                
                if (success)
                {
                    if (caretPoint.X == 0 && caretPoint.Y == 0)
                    {
                        result.AppendLine("警告: 光标位置为(0,0)，可能表示无效光标");
                    }
                    else if (caretPoint.X < 0 || caretPoint.Y < 0)
                    {
                        result.AppendLine("警告: 光标位置为负数，超出屏幕范围");
                    }
                    else
                    {
                        result.AppendLine("光标位置有效");
                    }
                }
                else
                {
                    result.AppendLine("GetCaretPos失败，没有文本光标");
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"直接GetCaretPos测试失败: {ex.Message}");
            }
            
            return result.ToString();
        }

        private static string TestCrossProcessCaretString()
        {
            var result = new StringBuilder();
            result.AppendLine("\n--- 测试方法2: 跨进程AttachThreadInput ---");
            
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                {
                    result.AppendLine("没有活动窗口，跳过跨进程测试");
                    return result.ToString();
                }
                
                uint targetThreadId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
                uint currentThreadId = GetCurrentThreadId();
                
                result.AppendLine($"目标线程ID: {targetThreadId}");
                result.AppendLine($"当前线程ID: {currentThreadId}");
                
                if (targetThreadId > 0 && currentThreadId > 0 && targetThreadId != currentThreadId)
                {
                    result.AppendLine("尝试附加线程输入...");
                    
                    if (AttachThreadInput(currentThreadId, targetThreadId, true))
                    {
                        try
                        {
                            var caretPoint = new POINT();
                            if (GetCaretPos(ref caretPoint))
                            {
                                result.AppendLine($"跨进程GetCaretPos成功: ({caretPoint.X}, {caretPoint.Y})");
                                
                                // 尝试转换为屏幕坐标
                                IntPtr caretWindow = GetFocus();
                                if (caretWindow != IntPtr.Zero)
                                {
                                    ClientToScreen(caretWindow, ref caretPoint);
                                    result.AppendLine($"屏幕坐标: ({caretPoint.X}, {caretPoint.Y})");
                                }
                            }
                            else
                            {
                                result.AppendLine("跨进程GetCaretPos失败");
                            }
                        }
                        finally
                        {
                            AttachThreadInput(currentThreadId, targetThreadId, false);
                            result.AppendLine("已分离线程输入");
                        }
                    }
                    else
                    {
                        result.AppendLine("AttachThreadInput失败");
                    }
                }
                else
                {
                    result.AppendLine("同进程或无效线程ID，跳过跨进程测试");
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"跨进程测试失败: {ex.Message}");
            }
            
            return result.ToString();
        }

        private static string TestGuiThreadInfoString()
        {
            var result = new StringBuilder();
            result.AppendLine("\n--- 测试方法3: GetGUIThreadInfo ---");
            
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                {
                    result.AppendLine("没有活动窗口，跳过GUI线程信息测试");
                    return result.ToString();
                }
                
                uint threadId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
                result.AppendLine($"线程ID: {threadId}");
                
                if (threadId > 0)
                {
                    var guiInfo = new GUITHREADINFO();
                    guiInfo.cbSize = (uint)Marshal.SizeOf(guiInfo);
                    
                    if (GetGUIThreadInfo(threadId, ref guiInfo))
                    {
                        result.AppendLine($"hwndCaret: {guiInfo.hwndCaret}");
                        result.AppendLine($"rcCaret: ({guiInfo.rcCaret.Left},{guiInfo.rcCaret.Top}-{guiInfo.rcCaret.Right},{guiInfo.rcCaret.Bottom})");
                        
                        if (guiInfo.hwndCaret != IntPtr.Zero && 
                            guiInfo.rcCaret.Right > guiInfo.rcCaret.Left && 
                            guiInfo.rcCaret.Bottom > guiInfo.rcCaret.Top)
                        {
                            int caretX = (guiInfo.rcCaret.Left + guiInfo.rcCaret.Right) / 2;
                            int caretY = (guiInfo.rcCaret.Top + guiInfo.rcCaret.Bottom) / 2;
                            result.AppendLine($"光标中心位置: ({caretX}, {caretY})");
                        }
                        else
                        {
                            result.AppendLine("没有有效文本光标");
                        }
                    }
                    else
                    {
                        result.AppendLine("GetGUIThreadInfo失败");
                    }
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"GUI线程信息测试失败: {ex.Message}");
            }
            
            return result.ToString();
        }

        private static string TestEMPosFromCharString()
         {
             var result = new StringBuilder();
             result.AppendLine("\n--- 测试方法4: EM_POSFROMCHAR消息 --- ");
             
             try
             {
                 IntPtr foregroundWindow = GetForegroundWindow();
                 if (foregroundWindow == IntPtr.Zero)
                 {
                     result.AppendLine("没有活动窗口，跳过EM_POSFROMCHAR测试");
                     return result.ToString();
                 }
                 
                 IntPtr focusedControl = GetFocus();
                 if (focusedControl == IntPtr.Zero)
                 {
                     result.AppendLine("没有焦点控件，跳过EM_POSFROMCHAR测试");
                     return result.ToString();
                 }
                 
                 result.AppendLine($"焦点控件句柄: {focusedControl}");
                 
                 // 步骤1: 使用EM_GETSEL获取当前光标位置（字符索引）
                     IntPtr startIndexPtr = IntPtr.Zero;
                     IntPtr endIndexPtr = IntPtr.Zero;
                     int selectionResult = SendMessage(focusedControl, EM_GETSEL, ref startIndexPtr, ref endIndexPtr).ToInt32();
                     
                     if (selectionResult >= 0) // EM_GETSEL 成功返回
                     {
                         int startIndex = startIndexPtr.ToInt32();
                         int endIndex = endIndexPtr.ToInt32();
                         int currentCaretIndex = endIndex; // 当前光标位置
                     
                     result.AppendLine($"EM_GETSEL成功: 开始={startIndex}, 结束={endIndex}, 光标字符索引={currentCaretIndex}");
                     
                     // 步骤2: 使用EM_POSFROMCHAR将字符索引转换为屏幕坐标
                     IntPtr charPos = new IntPtr(currentCaretIndex);
                     IntPtr screenPos = SendMessage(focusedControl, EM_POSFROMCHAR, charPos, IntPtr.Zero);
                     
                     if (screenPos != IntPtr.Zero)
                     {
                         int x = (int)(screenPos.ToInt32() & 0xFFFF);
                         int y = (int)((screenPos.ToInt32() >> 16) & 0xFFFF);
                         result.AppendLine($"EM_POSFROMCHAR成功: 屏幕坐标 ({x}, {y})");
                         
                         // 验证坐标是否有效（在屏幕范围内）
                         var screen = Screen.PrimaryScreen?.WorkingArea;
                         if (screen.HasValue)
                         {
                             if (x >= screen.Value.X && x <= screen.Value.Right && 
                                 y >= screen.Value.Y && y <= screen.Value.Bottom)
                             {
                                 result.AppendLine("坐标验证通过: 在屏幕工作区域内");
                             }
                             else
                             {
                                 result.AppendLine("警告: 坐标超出屏幕工作区域");
                             }
                         }
                     }
                     else
                     {
                         result.AppendLine("EM_POSFROMCHAR失败: 可能是RichTextBox或其他类型控件");
                     }
                 }
                 else
                 {
                     result.AppendLine("EM_GETSEL失败: 控件可能不支持此消息");
                 }
             }
             catch (Exception ex)
             {
                 result.AppendLine($"EM_POSFROMCHAR测试失败: {ex.Message}");
             }
             
             return result.ToString();
         }
    }
}