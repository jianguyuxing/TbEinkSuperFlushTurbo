using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Globalization;
using System.Linq;

namespace TbEinkSuperFlushTurbo
{
    public partial class MainForm : Form
    {
        D3DCaptureAndCompute? _d3d;
        System.Windows.Forms.Timer? _pollTimer;
        private string _logFilePath = string.Empty;
        private StreamWriter? _logWriter;
        private CancellationTokenSource? _cts; // Replaced _isStopping with CancellationTokenSource
        private OverlayForm? _overlayForm; // 添加OverlayForm字段以重用实例

        public Action<string>? DebugLogger { get; private set; } // Public property for logging

        // --- New state variables for per-tile stable refresh logic ---
        private int[]? _tileStableCounters;
        // --- Per-tile protection expiry tracking ---
        private long[]? _tileProtectionExpiry; // 记录每个区块的保护到期时间（帧号）
        private int _currentFrameNumber = 0;  // 当前帧号计数器
        // -----------------------------------------------------------
        
        // 记录"已触发刷新、正等待覆盖层消失"的区块，用于精准屏蔽自我刷新
        private readonly HashSet<int> _pendingTiles = new();
        // ---------------------------
        
        // --- Refresh parameters ---
        private const int TILE_SIZE = 8;               // 图块像素边长，平衡灵敏度和噪声
        private const int PIXEL_DELTA = 15;               // 每个像素的亮度差异阈值
        private const int INITIAL_COOLDOWN = -2;       // 初始化冷却帧数（负值，延缓多少帧）
        private const uint AVERAGE_WINDOW_SIZE = 4;     // 平均窗口帧数，更好检测渐变
        private const int STABLE_FRAMES_REQUIRED = 3;   // 稳定帧数，平衡响应速度和稳定性
        private const int ADDITIONAL_COOLDOWN_FRAMES = 2; // 额外冷却帧数，避免过度刷新
        private const int FIRST_REFRESH_EXTRA_DELAY = 2; // 首次刷新额外延迟帧数，用于-1状态区块
    
    // 计数器状态定义：
    // -2: 已刷新过的区块（需要保护）
    // -1: 从未变化过的区块（新区块）
    // 0-5: 正在稳定期检测中的区块

        public const int OVERLAY_DISPLAY_TIME = 100; // 刷新颜色停留毫秒
        
        // 保护期现在包含显示时长，动态计算显示时间对应的帧数

        private static int ProtectionFrames =>  (int)Math.Ceiling((double)OVERLAY_DISPLAY_TIME / POLL_TIMER_INTERVAL); // 显示时间对应的帧数（动态计算）
        private static int ProtectedFramesAfterOverlay => ProtectionFrames + ADDITIONAL_COOLDOWN_FRAMES; // 总保护期帧数（动态计算）
        
        // --- 重置区间设置 ---
        // 背景：当检测到变化的区块超过全部区块的95%时，通常代表用户使用了系统自带的Eink驱动进行了全屏刷新
        // 此时我们应该跳过刷新处理，将所有区块重置为与点击开始按钮后相同的状态，避免重复刷新
        private const double RESET_THRESHOLD_PERCENT = 95; // 重置阈值百分比（95%）
        private System.Windows.Forms.Timer? _overlayHideTimer; // 单例定时器，避免多个定时器冲突
        private NotifyIcon? _trayIcon;
        private bool _forceDirectXCapture = false; // 是否强制使用DirectX捕获
        
        // 公共属性用于控制DirectX捕获模式
        public bool ForceDirectXCapture
        {
            get => _forceDirectXCapture;
            set
            {
                _forceDirectXCapture = value;
                Log($"ForceDirectXCapture 设置为: {_forceDirectXCapture}");
            }
        }
        private const int HOTKEY_ID = 9000;
         private const int MOD_NONE = 0;
         private const int VK_F6 = 0x75;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
         private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
         [System.Runtime.InteropServices.DllImport("user32.dll")]
         private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private const int POLL_TIMER_INTERVAL = 515;    // 检测周期（毫秒）
        private const int CARET_CHECK_INTERVAL = 400;   // 文本光标检查间隔（毫秒）
        private const int IME_CHECK_INTERVAL = 400;     // 输入法窗口检查间隔（毫秒）
        
        // SetWindowPos常量
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int MOUSE_EXCLUSION_RADIUS_FACTOR = 2; // 鼠标排除区域半径因子（图块大小的倍数）
        private const int NOISE_DENSITY = 20;           // 噪声密度（百分比）
        private const int NOISE_POINT_INTERVAL = 3;     // 噪声点间隔（像素）
         private const string OVERLAY_BASE_COLOR = "Black"; // 覆盖层基础颜色
         
         // --- 边框控制参数 ---
         private const string OVERLAY_BORDER_COLOR = "64,64,64"; // 边框颜色 RGB (默认深灰色)
         private const int OVERLAY_BORDER_WIDTH = 0;              // 边框宽度 (像素).看起来不能有边框，否则视觉上看残影无法完全清除，但此参数保留
         private const int OVERLAY_BORDER_ALPHA = 100;          // 边框透明度 (0-255)
        
        // 系统UI检测参数
        private const double SYSTEM_UI_BOTTOM_RATIO = 0.15; // 屏幕底部15%区域可能是系统UI（任务栏、开始菜单）
        private const int SYSTEM_UI_MIN_TILES = 5; // 系统UI区域最小瓦片数量
        // --------------------------

        public MainForm()
        {
            try
            {
                Console.WriteLine("MainForm constructor started");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor started{Environment.NewLine}");
                InitLogFile();
                InitUI();
                
                // 运行亮度测试
                try
                {
                    TestBrightness.TestBrightnessCalculation();
                }
                catch (Exception ex)
                {
                    Log($"亮度测试失败: {ex.Message}");
                }
                Console.WriteLine("MainForm constructor completed");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor completed{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor ERROR: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] StackTrace: {ex.StackTrace}{Environment.NewLine}");
                throw; // 重新抛出异常
            }
        }

        private void InitLogFile()
        {
            try
            {
                // 创建日志目录
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
                System.Diagnostics.Debug.WriteLine($"Attempting to create log directory: {logDirectory}");
                Console.WriteLine($"Attempting to create log directory: {logDirectory}");

                // 记录保护期配置
                Log($"[保护期配置] 显示时间={OVERLAY_DISPLAY_TIME}ms, 检测周期={POLL_TIMER_INTERVAL}ms, 总保护期帧数={ProtectedFramesAfterOverlay} (保护期{ProtectionFrames}帧+额外冷却{ADDITIONAL_COOLDOWN_FRAMES}帧)");
                
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                    System.Diagnostics.Debug.WriteLine("Log directory created successfully");
                    Console.WriteLine("Log directory created successfully");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Log directory already exists");
                    Console.WriteLine("Log directory already exists");
                    // 删除除最新日志文件外的所有application日志文件
                    CleanupOldApplicationLogFiles(logDirectory);
                }

                // 创建基于时间戳的日志文件
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
                _logFilePath = Path.Combine(logDirectory, $"application_{timestamp}.log");
                
                _logWriter = new StreamWriter(_logFilePath, false, System.Text.Encoding.UTF8);
                _logWriter.AutoFlush = true;
                
                Log("Application started");
                DebugLogger = Log; // Assign Log method to DebugLogger
                
                // 测试日志写入
                Log($"Log file created successfully at: {_logFilePath}");
                Log($"Base directory: {AppContext.BaseDirectory}");
                Log($"Current user: {Environment.UserName}");
                Log($"Working directory: {Environment.CurrentDirectory}");
                
                Console.WriteLine($"Log file initialized successfully: {_logFilePath}");
            }
            catch (Exception ex)
            {
                // 如果无法创建日志文件，至少在控制台输出错误
                System.Diagnostics.Debug.WriteLine($"Failed to create log file: {ex.Message}");
                Console.WriteLine($"Failed to create log file: {ex.Message}");
                Console.WriteLine($"Exception details: {ex}");
                Console.WriteLine($"Base directory: {AppContext.BaseDirectory}");
                
                MessageBox.Show(@"无法创建日志文件: " + ex.Message + @"

详细错误: " + ex + @"

基础目录: " + AppContext.BaseDirectory, "日志初始化错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public async Task LogAsync(string message) // Changed to public
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                if (_logWriter != null)
                {
                    await _logWriter.WriteLineAsync(logEntry);
                    await _logWriter.FlushAsync();
                }
                System.Diagnostics.Debug.WriteLine(logEntry); // Still output to debug console
            }
            catch
            {
                // 忽略日志记录错误
            }
        }

        // 保持同步方法用于兼容性 - 关键：构造函数和初始化阶段使用
        public void Log(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                if (_logWriter != null)
                {
                    _logWriter.WriteLine(logEntry);
                    _logWriter.Flush();
                }
                System.Diagnostics.Debug.WriteLine(logEntry); // Still output to debug console
            }
            catch
            {
                // 忽略日志记录错误
            }
        }

        // 清理旧的application日志文件，保留最新的一个
        private void CleanupOldApplicationLogFiles(string logDirectory)
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "application_*.log");
                if (logFiles.Length > 1)
                {
                    // 按创建时间排序，获取除最新文件外的所有文件
                    var filesToDelete = logFiles
                        .Select(f => new { Path = f, Info = new FileInfo(f) })
                        .OrderByDescending(f => f.Info.CreationTime)
                        .Skip(1) // 跳过最新的文件
                        .Select(f => f.Path);

                    // 删除旧文件
                    foreach (var file in filesToDelete)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // 忽略删除失败的情况
                        }
                    }
                }
            }
            catch
            {
                // 忽略清理过程中的任何错误
            }
        }

        private void InitUI()
        {
            // 设置DPI自适应属性
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            
            // --- START DPI SCALING MODIFICATION ---
            // Get the current screen's DPI scaling factor to correctly size UI elements.
            float dpiScale;
            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                dpiScale = graphics.DpiX / 96.0f;
            }
            // --- END DPI SCALING MODIFICATION ---

            // 初始化_overlayForm字段
            _overlayForm = null;

            Text = "EInk Kaleido Ghost Reducer (GPU)";
            // 增加窗体尺寸，宽高都增加一倍
            Width = 1200; 
            Height = 800; // 宽高都增加一倍
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // 根据用户偏好调整按钮尺寸，使其大于文字
            int buttonWidth = 120;  // 固定宽度
            int buttonHeight = 50;  // 固定高度，确保大于文字
            var btnStart = new Button() { 
                Text = "Start", 
                Left = 20, 
                Top = 20, 
                Width = buttonWidth, 
                Height = buttonHeight,
                Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold) // 调整按钮字体大小
            };
            var btnStop = new Button() { 
                Text = "Stop", 
                Left = 160, 
                Top = 20, 
                Width = buttonWidth, 
                Height = buttonHeight,
                Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold), // 调整按钮字体大小
                Enabled = false 
            };
            
            // 调整标签和列表框尺寸以适应较大的窗体
            var lblInfo = new Label() { 
                Left = 30, 
                Top = 80, 
                Width = 1140,  // 增加宽度以适应窗体
                Height = 40, 
                Text = "Status: stopped" 
            };
            var listBox = new ListBox() { 
                Left = 30, 
                Top = 130, 
                Width = 1100,  // 减小宽度以在右侧留出更多空白
                Height = 550   // 减小高度以在底部留出更多空白
            };

            // 设置窗体字体大小
            this.Font = new Font(this.Font.FontFamily, 9f);

            Controls.Add(btnStart);
            Controls.Add(btnStop);
            Controls.Add(lblInfo);
            Controls.Add(listBox);

            // ---- 托盘图标初始化 ----
            _trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application,
                Text = "EInk Ghost Reducer",
                Visible = true
            };
            // 左键单击：显示/隐藏主窗口
            _trayIcon.Click += (s,e)=> 
            {
                Log($"Tray icon clicked - WindowState: {this.WindowState}, Visible: {this.Visible}, TrayIconVisible: {_trayIcon.Visible}");
                
                // 添加错误处理和额外的调试信息
                try
                {
                    if(this.WindowState==FormWindowState.Minimized || !this.Visible)
                    {
                        Log("Showing main window...");
                        // 确保窗口完全显示
                        this.WindowState = FormWindowState.Normal;
                        this.Visible = true;
                        this.Show();
                        this.BringToFront();
                        this.Activate();
                        Log($"Main window shown - WindowState: {this.WindowState}, Visible: {this.Visible}");
                        
                        // 显示一个临时的提示，确认点击事件被触发
                        _trayIcon.ShowBalloonTip(1000, "EInk Ghost Reducer", "主窗口已显示", ToolTipIcon.Info);
                    }
                    else
                    {
                        Log("Hiding main window...");
                        this.Hide();
                        Log($"Main window hidden - Visible: {this.Visible}");
                        
                        // 显示一个临时的提示，确认点击事件被触发
                        _trayIcon.ShowBalloonTip(1000, "EInk Ghost Reducer", "主窗口已隐藏", ToolTipIcon.Info);
                    }
                }
                catch (Exception ex)
                {
                    Log($"ERROR in tray icon click handler: {ex.GetType().Name}: {ex.Message}");
                    Log($"StackTrace: {ex.StackTrace}");
                }
            };
            // 双击仍可手动刷新
            _trayIcon.DoubleClick += (s,e)=>ManualRefresh();
            // 右键菜单 - 修复自动消失问题
            var exitMenu=new ToolStripMenuItem("退出");
            exitMenu.Click+=(s,e)=>
            {
                Log($"[EXIT] 用户点击托盘退出菜单，_cts={_cts}");
                
                // 首先停止定时器
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Enabled = false;
                }
                
                // 取消异步操作
                _cts?.Cancel();
                
                Log($"[EXIT] 已调用_cancel，准备关闭窗口");
                
                // 先关闭窗口，让OnFormClosing处理资源清理
                this.Close();
            };
            var menu=new ContextMenuStrip();
            menu.Items.Add(exitMenu);
            
            // 修复右键菜单自动消失的问题
            menu.Closing += (s, e) => 
            {
                // 只有当用户点击了菜单项时才关闭，而不是失去焦点时关闭
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    e.Cancel = false; // 允许关闭
                }
                else if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange || 
                         e.CloseReason == ToolStripDropDownCloseReason.AppClicked)
                {
                    e.Cancel = true; // 阻止自动关闭
                }
            };
            
            _trayIcon.ContextMenuStrip=menu;
            
            // 添加鼠标事件处理，确保右键能正确显示菜单
            _trayIcon.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    // 确保菜单在正确的位置显示
                    var menu = _trayIcon.ContextMenuStrip;
                    if (menu != null && !menu.Visible)
                    {
                        menu.Show(Cursor.Position);
                    }
                }
            };

            btnStart.Click += (s, e) =>
            {
                btnStart.Enabled = false;
                _cts = new CancellationTokenSource(); // Create new CancellationTokenSource

                lblInfo.Text = "Status: initializing GPU capture...";
                Log("Initializing GPU capture...");
                try
                {
                    // Create D3DCaptureAndCompute instance with logger and parameters
                    _d3d = new D3DCaptureAndCompute(DebugLogger, TILE_SIZE, PIXEL_DELTA, AVERAGE_WINDOW_SIZE, CARET_CHECK_INTERVAL, IME_CHECK_INTERVAL, MOUSE_EXCLUSION_RADIUS_FACTOR, _forceDirectXCapture);

                    // Initialize the per-tile counter array with -1 to indicate unprocessed tiles
                    _tileStableCounters = new int[_d3d.TilesX * _d3d.TilesY];
                    // Set all tiles to -1 to indicate they haven't been processed yet
                    Array.Fill(_tileStableCounters, -1);
                    Log($"Initialized per-tile stability counters for {_tileStableCounters.Length} tiles.");

                    // Initialize the per-tile protection expiry array with 0 to indicate no protection
                    _tileProtectionExpiry = new long[_d3d.TilesX * _d3d.TilesY];
                    // Set all tiles to 0 to indicate no protection
                    for (int i = 0; i < _tileProtectionExpiry.Length; i++)
                    {
                        _tileProtectionExpiry[i] = 0L;
                    }
                    Log($"Initialized per-tile protection expiry array for {_tileProtectionExpiry.Length} tiles.");

                    // 获取当前屏幕的 DPI 缩放信息
                    using (Graphics graphics = Graphics.FromHwnd(Handle))
                    {
                        float dpiX = graphics.DpiX;
                        float dpiY = graphics.DpiY;
                        lblInfo.Text = $"Status: Initializing... (DPI: {dpiX}x{dpiY})";
                        Log($"Current System DPI: {dpiX}x{dpiY}");
                    }


                    // 设置查询 timer 的频率以适应 GPU 刷新频率
                    _pollTimer = new System.Windows.Forms.Timer();
                    _pollTimer.Interval = POLL_TIMER_INTERVAL; // 检测周期ms, to better capture cursor blinking < 530
                    _pollTimer.Tick += async (ss, ee) =>
                    {
                        var token = _cts.Token;
                        if (token.IsCancellationRequested) return;

                        // 同步执行检测逻辑，避免不必要的异步开销
                        if (token.IsCancellationRequested || _d3d == null || _tileStableCounters == null) return;
                        
                        // 1. Get the list of tiles that changed in this frame and brightness data
                        // Use explicit access to tuple items to ensure compatibility and clarity.
                        var result = await _d3d.CaptureAndComputeOnceAsync(token);
                        var changedTiles = result.Item1;
                        var brightnessData = result.Item2;
                        if (token.IsCancellationRequested) return;

                        // 更新当前帧号
                        _currentFrameNumber++;

                        // 精准屏蔽：覆盖层亮灯期间 **或** 保护期内，仅屏蔽"已触发刷新、正在等覆盖层消失"的区块，不拦新区块
                        // 使用到期时间数组判断保护期，替代全局计数器
                        bool inProtection = false;
                        if (changedTiles.Count > 0)
                        {
                            var filteredTiles = new List<(int bx, int by)>();
                            foreach (var t in changedTiles)
                            {
                                int idx = t.by * _d3d.TilesX + t.bx;
                                // 检查是否在保护期内（当前帧号 < 到期帧号）
                                if (_tileProtectionExpiry[idx] > _currentFrameNumber)
                                {
                                    // 该区块仍在保护期内，需要屏蔽
                                    inProtection = true;
                                    // 保持-2状态，继续保护
                                }
                                else
                                {
                                    // 该区块不在保护期内，允许正常处理
                                    filteredTiles.Add(t);
                                }
                            }
                            changedTiles = filteredTiles;  // 只保留需要处理的区块
                        }
                        
                        bool shouldBlock = (_overlayForm != null && _overlayForm.Visible) || inProtection;
                        Log($"屏蔽检查: inProtection={inProtection}, shouldBlock={shouldBlock}, currentFrame={_currentFrameNumber}, overlayVisible={_overlayForm?.Visible ?? false}");

                        // 2. Create a lookup set for efficient checking
                        var changedTilesSet = new HashSet<int>(changedTiles.Select(t => t.by * _d3d.TilesX + t.bx));
                        
                        var tilesToRefreshNow = new List<(int bx, int by)>();
                        int totalTiles = _d3d.TilesX * _d3d.TilesY;

                        // 3. Iterate through all tiles to update counters and find which ones to refresh
                        int totalStableTiles = 0; // 统计稳定瓦片数量
                        int totalReadyTiles = 0;  // 统计准备刷新的瓦片数量
                        
                        for (int i = 0; i < totalTiles; i++)
                        {
                            // **关键修复**：检查保护期 - 如果区块在保护期内，完全跳过处理
                            if (_tileProtectionExpiry[i] > _currentFrameNumber)
                            {
                                // 该区块在保护期内，保持-2状态，不进行任何处理
                                continue;
                            }
                            
                            // 1. 更新稳定性计数器
                            if (changedTilesSet.Contains(i))
                            {
                                // 瓦片发生变化：重置计数器开始新的稳定期检测
                                if (_tileStableCounters[i] == -1)
                                {
                                    // -1状态区块（首次变化）给予额外延迟，跳过前几帧检测
                                    _tileStableCounters[i] = FIRST_REFRESH_EXTRA_DELAY;
                                }
                                else if (_tileStableCounters[i] == -2)
                                {
                                    // -2状态区块在保护期过后发生变化，也给予延迟，跳过前几帧检测
                                    _tileStableCounters[i] = FIRST_REFRESH_EXTRA_DELAY;
                                }
                                else if (_tileStableCounters[i] > STABLE_FRAMES_REQUIRED && _tileStableCounters[i] <= STABLE_FRAMES_REQUIRED + ADDITIONAL_COOLDOWN_FRAMES)
                                {
                                    // **关键修复**：冷却期内的区块变化直接忽略，保持冷却期状态
                                    // 这样可以避免重复刷新循环，冷却期内的变化不触发新的刷新检测
                                    // 保持当前冷却期计数器不变，继续冷却期倒计时
                                }
                                else
                                {
                                    // 普通变化区块：从1开始计数，需要达到STABLE_FRAMES_REQUIRED才能刷新
                                    _tileStableCounters[i] = 1;
                                }
                            }
                            else if (_tileStableCounters[i] >= 0)
                            {
                                // 瓦片未变化且之前检测到过变化：递增计数器
                                if (_tileStableCounters[i] < STABLE_FRAMES_REQUIRED + ADDITIONAL_COOLDOWN_FRAMES)
                                {
                                    _tileStableCounters[i]++;
                                }
                                else
                                {
                                    // 冷却期结束，重置为-2，表示该区块已刷新过，需要保护
                                    _tileStableCounters[i] = -2;
                                }
                            }

                            // 2. 检查瓦片稳定性 - 只处理之前检测到过变化的瓦片
                            if (_tileStableCounters[i] >= 0)
                            {
                                bool shouldRefresh = false;
                                
                                // 统一逻辑：检测跳过机制
                                // 首次变化（-1状态）和保护期后变化（-2状态）的区块需要额外跳过前几帧
                                // 其他变化区块跳过首帧检测
                                // 所有区块都需要达到STABLE_FRAMES_REQUIRED才能刷新
                                if (_tileStableCounters[i] >= STABLE_FRAMES_REQUIRED)
                                {
                                    shouldRefresh = true;
                                }
                                
                                if (shouldRefresh)
                                {
                                    // 转换瓦片索引回(bx, by)坐标并添加到刷新列表
                                    int bx = i % _d3d.TilesX;
                                    int by = i / _d3d.TilesX;
                                    
                                    // **关键修复**：在添加到刷新列表前再次检查保护期
                                    // 防止在处理过程中有新的保护期设置
                                    if (_tileProtectionExpiry[i] <= _currentFrameNumber)
                                    {
                                        tilesToRefreshNow.Add((bx, by));
                                        totalReadyTiles++;
                                        // 重置计数器到-2，表示该区块已刷新过，需要保护
                                        _tileStableCounters[i] = -2;
                                        // 设置保护期到期时间（当前帧号 + 保护期帧数）
                                        _tileProtectionExpiry[i] = _currentFrameNumber + ProtectedFramesAfterOverlay;
                                    }
                                }
                            }
                            
                            // 3. 统计稳定瓦片数量（用于日志）
                            if (_tileStableCounters[i] >= 0 && _tileStableCounters[i] <= STABLE_FRAMES_REQUIRED + ADDITIONAL_COOLDOWN_FRAMES)
                            {
                                totalStableTiles++;
                            }
                        }
                            
                        // 记录本轮真正要刷的区块，用于后续精准屏蔽自我刷新
                        _pendingTiles.Clear();
                        foreach (var (bx, by) in tilesToRefreshNow)
                        {
                            _pendingTiles.Add(by * _d3d.TilesX + bx);
                        }
                        
                        // 检查是否达到重置阈值：当要刷新的区块超过95%时，代表系统进行了全屏刷新
                        double refreshRatio = (double)tilesToRefreshNow.Count / (_d3d.TilesX * _d3d.TilesY);
                        if (refreshRatio >= RESET_THRESHOLD_PERCENT)
                        {
                            Log($"检测到系统全屏刷新（{refreshRatio:P1}），跳过刷新处理并重置所有区块状态");
                            
                            // 重置所有区块状态，与点击开始按钮后的处理方式相同
                            for (int i = 0; i < _tileStableCounters.Length; i++)
                            {
                                _tileStableCounters[i] = -1; // 设置为从未变化过的状态
                                _tileProtectionExpiry[i] = 0; // 清除保护期
                            }
                            
                            // 清空刷新列表，跳过本次刷新
                            tilesToRefreshNow.Clear();
                            _pendingTiles.Clear();
                            
                            // 如果覆盖层正在显示，也隐藏它
                            if (_overlayForm != null && _overlayForm.Visible)
                            {
                                _overlayForm.HideOverlay();
                            }
                        }
                            



                            if (changedTiles.Count > 0 || tilesToRefreshNow.Count > 0)
                                Log($"Captured frame. Changed tiles: {changedTiles.Count}. Tiles to refresh now: {tilesToRefreshNow.Count} (currentFrame: {_currentFrameNumber}, overlayVisible: {_overlayForm?.Visible ?? false})");

                            // 5. If there are any tiles that have met the stability criteria, refresh them
                            if (tilesToRefreshNow.Count > 0)
                            {
                                
                                // 使用异步方式更新UI，避免阻塞
                                if (!token.IsCancellationRequested)
                                {
                                    await Task.Run(() =>
                                    {
                                        if (this.InvokeRequired)
                                        {
                                            this.Invoke(new Action(() =>
                                            {
                                                listBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} tiles (per-tile stable): {tilesToRefreshNow.Count}");
                                                if (listBox.Items.Count > 200) listBox.Items.RemoveAt(listBox.Items.Count - 1);
                                            }));
                                        }
                                        else
                                        {
                                            listBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} tiles (per-tile stable): {tilesToRefreshNow.Count}");
                                            if (listBox.Items.Count > 200) listBox.Items.RemoveAt(listBox.Items.Count - 1);
                                        }
                                    }, token);
                                    
                                    await ShowTemporaryOverlayAsync(tilesToRefreshNow, brightnessData, token);
                                }
                            }
                    };
                    _pollTimer.Start();

                    lblInfo.Text = $"Status: running (screen {_d3d.ScreenWidth}x{_d3d.ScreenHeight})";
                    btnStop.Enabled = true;
                    Log($"GPU capture initialized successfully. Screen resolution: {_d3d.ScreenWidth}x{_d3d.ScreenHeight}");
                }
                catch (Exception ex)
                {
                    string errorMessage = $"初始化失败: {ex.Message}";
                    Log(errorMessage + "\n" + ex.StackTrace);
                    MessageBox.Show(errorMessage);
                    btnStart.Enabled = true;
                    lblInfo.Text = "Status: failed";
                    _cts?.Cancel(); // Cancel on error
                    _cts?.Dispose();
                    _cts = null;
                }
            };

            btnStop.Click += (s, e) =>
            {
                Log("Stopping GPU capture...");
                _cts?.Cancel(); // Signal cancellation
                _pollTimer?.Stop();
                _pollTimer?.Dispose();
                _pollTimer = null;
                _d3d?.Dispose();
                _d3d = null;
                
                // Reset state
                _tileStableCounters = null;
                
                // 重置刷新色停留计数器
                // 保留此行以确保状态一致性

                lblInfo.Text = "Status: stopped";
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                _cts?.Dispose(); // Dispose CancellationTokenSource
                _cts = null;
                
                // 隐藏OverlayForm而不是销毁它
                _overlayForm?.HideOverlay();
                
                Log("GPU capture stopped");
            };
        }

        // 临时的覆盖层可以有效防止屏幕刷新时的视觉抖动，即使在全屏模式下，临时覆盖层也可以防止屏幕刷新时的视觉抖动
        async Task ShowTemporaryOverlayAsync(List<(int bx, int by)>? tiles, float[]? brightnessData, CancellationToken token)
        {
            if (token.IsCancellationRequested || _d3d == null) return;
            
            // 不限制瓦片数量，确保检测所有变化区域
            
            // 确保有瓦片需要显示
            if (tiles == null || tiles.Count == 0)
            {
                Log("DEBUG: 没有瓦片需要显示，跳过覆盖层");
                return;
            }
            
            Log($"DEBUG: ShowTemporaryOverlayAsync called with {tiles.Count} tiles");
            
            // 使用重用的OverlayForm实例
            if (_overlayForm == null)
            {
                Color overlayBaseColor = Color.FromName(OVERLAY_BASE_COLOR);
                
                // 解析边框颜色
                string[] rgbParts = OVERLAY_BORDER_COLOR.Split(',');
                Color borderColor = Color.FromArgb(
                    OVERLAY_BORDER_ALPHA,
                    int.Parse(rgbParts[0].Trim()),
                    int.Parse(rgbParts[1].Trim()),
                    int.Parse(rgbParts[2].Trim())
                );
                
                _overlayForm = new OverlayForm(_d3d.TileSize, _d3d.ScreenWidth, _d3d.ScreenHeight, NOISE_DENSITY, NOISE_POINT_INTERVAL, overlayBaseColor, borderColor, OVERLAY_BORDER_WIDTH, Log);
                // ② 先让窗口普通显示，确保能看见
                _overlayForm.ShowInTaskbar = false;
                _overlayForm.FormBorderStyle = FormBorderStyle.None;
                _overlayForm.BackColor = Color.Red;    // 恢复红色
                _overlayForm.TopMost = true;
                _overlayForm.Opacity = 1.0;    // 交还给 UpdateLayeredWindow 控制
                _overlayForm.StartPosition = FormStartPosition.Manual;
                // 恢复全屏
                _overlayForm.Location = new Point(0, 0);
                _overlayForm.Size = new Size(_d3d.ScreenWidth, _d3d.ScreenHeight);
                
                // 使用UI线程异步显示覆盖层
                await Task.Run(() =>
                {
                    if (this.InvokeRequired)
                    {
                        this.Invoke(new Action(() => _overlayForm.Show()));
                    }
                    else
                    {
                        _overlayForm.Show();
                    }
                }, token);
                
                // 诊断：窗口到底在哪
                var msg = $"DEBUG: OverlayForm Handle=0x{_overlayForm.Handle:X}, Bounds={_overlayForm.Bounds}";
                Trace.WriteLine(msg);
                Log(msg);      // 确保写进日志文件
                Log("DEBUG: OverlayForm created and shown");
            }
            
            // 强制给覆盖层一个不透明背景，确保肉眼可见
            _overlayForm.BackColor = Color.FromArgb(255, 255, 0, 0); // 不透明白红
            
            // 正确更新瓦片内容和视觉效果，让OverlayForm自己管理生命周期
            await Task.Run(() => _overlayForm.UpdateContent(tiles, brightnessData), token);
            Log($"DEBUG: Overlay content updated with {tiles.Count} tiles using UpdateContent");
            
            Log($"DEBUG: 刷新色显示开始，将显示{OVERLAY_DISPLAY_TIME}ms");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ManualRefresh();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnLoad(EventArgs e)
        {
            Console.WriteLine("MainForm OnLoad started");
            base.OnLoad(e);
            
            try
            {
                // 一次性设置，避免后面反复改
                this.ShowInTaskbar = true;    // 主窗口出现在任务栏，便于调试
                this.WindowState = FormWindowState.Normal;  // 启动时显示正常窗口
                this.Visible = true;          // 启动即显示，便于调试
                
                // 确保窗口有 WS_EX_LAYERED 扩展样式
                int exStyle = GetWindowLong(this.Handle, -20); // GWL_EXSTYLE = -20
                SetWindowLong(this.Handle, -20, exStyle | 0x00080000); // WS_EX_LAYERED = 0x00080000
                
                Log($"MainForm OnLoad started - Handle=0x{this.Handle:X}, ExStyle=0x{GetWindowLong(this.Handle, -20):X}");
                
                // 注册全局 F6
                RegisterHotKey(this.Handle, HOTKEY_ID, MOD_NONE, VK_F6);
                Log($"MainForm OnLoad completed - WS_EX_LAYERED set to 0x{GetWindowLong(this.Handle, -20):X}");
                
                // 确保托盘图标正确初始化
                if (_trayIcon != null)
                {
                    Log($"Tray icon initialized: Visible={_trayIcon.Visible}, Text='{_trayIcon.Text}'");
                    // 显示启动提示
                    try
                    {
                        _trayIcon.ShowBalloonTip(3000, "EInk Ghost Reducer", "程序已启动，点击托盘图标显示控制面板", ToolTipIcon.Info);
                        Log("Balloon tip shown successfully");
                    }
                    catch (Exception balloonEx)
                    {
                        Log($"Failed to show balloon tip: {balloonEx.Message}");
                    }
                }
                else
                {
                    Log("ERROR: Tray icon is null!");
                    // 如果托盘图标为空，显示窗口而不是退出
                    this.Visible = true;
                    this.WindowState = FormWindowState.Normal;
                    MessageBox.Show("托盘图标初始化失败，程序将以窗口模式运行", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR in OnLoad: {ex.GetType().Name}: {ex.Message}");
                Log($"StackTrace: {ex.StackTrace}");
                throw; // 重新抛出异常，让上层处理
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log("Application closing...");
            
            try
            {
                // 首先停止轮询定时器，防止新的捕获开始
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Enabled = false;
                }
                
                // 取消所有异步操作
                _cts?.Cancel(); // Signal cancellation
                
                // 等待一小段时间让线程安全退出
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Log($"ERROR during closing preparation: {ex.Message}");
            }
            
            base.OnFormClosing(e);
            
            try
            {
                // 释放DirectX资源（在停止定时器之后）
                _d3d?.Dispose();
                
                // 释放OverlayForm资源
                _overlayForm?.Dispose();
                _overlayForm = null;
                
                // 释放托盘图标
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                
                // 释放定时器资源
                if (_overlayHideTimer != null)
                {
                    _overlayHideTimer.Stop();
                    _overlayHideTimer.Dispose();
                    _overlayHideTimer = null;
                }
                
                // 注销热键
                UnregisterHotKey(this.Handle, HOTKEY_ID);
                
                // 最后关闭日志写入器
                _logWriter?.Close();
                _logWriter?.Dispose();
                
                // 释放CancellationTokenSource
                _cts?.Dispose();
                _cts = null;
            }
            catch (Exception ex)
            {
                Log($"ERROR during resource cleanup: {ex.Message}");
            }
            
            Log("Application closed");
        }

        // 手动触发一次完整刷新
        public async void ManualRefresh()
        {
            Console.WriteLine("[ManualRefresh] 入口");
            Log("[Manual] F5 triggered refresh");
            if (_d3d == null)
            {
                Console.WriteLine("[ManualRefresh] _d3d 为 null");
                return;
            }

            try
            {
                var (tiles, bright) = await _d3d.CaptureAndComputeOnceAsync(CancellationToken.None);
                Console.WriteLine($"[ManualRefresh] 抓到 {tiles?.Count ?? -1} 个 tiles，亮度数据长度 {bright?.Length ?? -1}");
                await ShowTemporaryOverlayAsync(tiles, bright, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManualRefresh] 异常：{ex}");
            }
        }
    }

    class OverlayForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        
        // SetWindowPos常量
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly Action<string> Logger;
        // 使用Dictionary存储瓦片，提高查找效率，键为(bx,by)元组，值为亮度数据
        readonly Dictionary<(int bx, int by), float> _tiles = new Dictionary<(int bx, int by), float>();
        readonly List<(int bx, int by)> _expiredTiles = new List<(int bx, int by)>(); // 用于当前绘制周期的过期瓦片
        readonly List<CancellationTokenSource> _batchCancellationTokenSources = new List<CancellationTokenSource>(); // 记录每批瓦片的取消令牌
        Bitmap? _overlayBitmap; // 累积位图
        private readonly object _bitmapLock = new object(); // 用于同步访问_bitmap的锁
        private bool _isDisplaying = false; // 标记是否正在显示刷新色
        public int TileCount => _tiles.Count;
        readonly int _tileSize;
        readonly int _screenW, _screenH;
        readonly int _noiseDensity;
        readonly int _noisePointInterval;
        readonly Color _baseColor;
        readonly Color _borderColor;
        readonly int _borderWidth;
        
        // 使用主类的显示时间常量，不再硬编码
        public void UpdateContent(List<(int bx, int by)> tiles, float[]? brightnessData = null)
        {
            bool hadTilesBefore = _tiles.Count > 0;
            bool addedNewTiles = false;
            
            // 添加新瓦片到现有Dictionary中，使用ContainsKey提高查找效率(O(1))
            foreach (var tile in tiles)
            {
                // 计算瓦片索引
                int tilesX = (_screenW + _tileSize - 1) / _tileSize;
                int tileIdx = tile.by * tilesX + tile.bx;
                
                // 获取该瓦片的亮度值
                float brightness = 0.5f; // 默认亮度
                if (brightnessData != null && tileIdx < brightnessData.Length)
                {
                    brightness = brightnessData[tileIdx];
                }
                
                // 检查瓦片是否已经在显示列表中，使用Dictionary的ContainsKey方法，O(1)复杂度
                if (!_tiles.ContainsKey(tile))
                {
                    _tiles[tile] = brightness;
                    addedNewTiles = true;
                }
            }
            
            // 为当前批次的瓦片创建一个统一的定时器，无论是否有新瓦片添加
            // 确保每次调用都为这批瓦片设置过期时间
            if (tiles.Count > 0)
            {
                // 为这批新的瓦片创建一个统一的定时器
                CancellationTokenSource cts = new CancellationTokenSource();
                _batchCancellationTokenSources.Add(cts);
                
                // 启动统一的定时器来清除这批瓦片
                _ = Task.Run(async () => {
                    try 
                    {
                        await Task.Delay(MainForm.OVERLAY_DISPLAY_TIME, cts.Token);
                        // 时间到了，标记这批瓦片为过期
                        MarkTilesAsExpired(tiles, cts);
                    }
                    catch (OperationCanceledException)
                    {
                        // 任务被取消，正常情况
                    }
                });
            }
            
            // 只有在之前没有显示或者有新瓦片时才更新显示
            if (!_isDisplaying || addedNewTiles)
            {
                UpdateVisuals();
                _isDisplaying = true;
                if (addedNewTiles)
                {
                    Logger?.Invoke($"DEBUG: 刷新色显示开始，将显示{MainForm.OVERLAY_DISPLAY_TIME}ms，当前瓦片数: {_tiles.Count}");
                }
            }
        }
    
        // 从UI线程安全地标记一批瓦片为过期
        private void MarkTilesAsExpired(List<(int bx, int by)> tiles, CancellationTokenSource cts)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => MarkTilesAsExpiredInternal(tiles, cts)));
            }
            else
            {
                MarkTilesAsExpiredInternal(tiles, cts);
            }
        }
    
        private void MarkTilesAsExpiredInternal(List<(int bx, int by)> tiles, CancellationTokenSource cts)
        {
            // 将这些瓦片标记为过期
            int expiredCount = 0;
            foreach (var tile in tiles)
            {
                // 使用Dictionary的Remove方法，O(1)复杂度
                if (_tiles.Remove(tile))
                {
                    _expiredTiles.Add(tile);
                    expiredCount++;
                }
            }
            
            // 从取消令牌列表中移除这个令牌
            _batchCancellationTokenSources.Remove(cts);
            cts.Dispose();
            
            // 更新显示以清除过期的瓦片
            UpdateVisuals();
            
            // 只有当还有其他瓦片时，清理临时过期瓦片列表，为下一轮刷新做准备
            if (_tiles.Count > 0)
            {
                Logger?.Invoke($"DEBUG: 部分刷新色过期，本次过期瓦片数: {expiredCount}，剩余瓦片数: {_tiles.Count}，过期瓦片数: {_expiredTiles.Count}");
                // 清理临时过期瓦片列表，为下一轮刷新做准备
                _expiredTiles.Clear();
            }
            else
            {
                // 当没有更多瓦片时，清理资源并标记为不显示
                lock (_bitmapLock)
                {
                    _overlayBitmap?.Dispose();
                    _overlayBitmap = null;
                }
                _isDisplaying = false;
                Logger?.Invoke($"DEBUG: 刷新色显示结束，所有瓦片已过期，本次过期瓦片数: {expiredCount}，过期瓦片数: {_expiredTiles.Count}");
                
                // 清理所有过期记录，为下一轮刷新做准备
                _expiredTiles.Clear();
            }
        }
    
        public void HideOverlay()
        {
            // 取消所有正在进行的定时器
            foreach (var cts in _batchCancellationTokenSources)
            {
                cts.Cancel();
            }
            
            // 清理所有数据
            _tiles.Clear();
            _expiredTiles.Clear();
            foreach (var cts in _batchCancellationTokenSources)
            {
                cts.Dispose();
            }
            _batchCancellationTokenSources.Clear();
            
            // 清理位图资源
            lock (_bitmapLock)
            {
                _overlayBitmap?.Dispose();
                _overlayBitmap = null;
            }
            _isDisplaying = false;
            UpdateVisuals(null);
            Logger?.Invoke("DEBUG: 刷新色强制隐藏");
        }

        public OverlayForm(int tileSize, int screenW, int screenH, int noiseDensity, int noisePointInterval, Color baseColor, Color borderColor, int borderWidth, Action<string> log)
        {
            _tileSize = tileSize;
            _screenW = screenW; 
            _screenH = screenH;
            _noiseDensity = noiseDensity;
            _noisePointInterval = noisePointInterval;
            _baseColor = baseColor;
            _borderColor = borderColor;
            _borderWidth = borderWidth;
            Logger = log;
            
            // 初始化位图
            lock (_bitmapLock)
            {
                _overlayBitmap = new Bitmap(screenW, screenH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Location = new Point(0, 0);
            Size = new Size(screenW, screenH);
            
            // 设置 WS_EX_LAYERED 扩展样式
            int exStyle = GetWindowLong(this.Handle, -20); // GWL_EXSTYLE = -20
            SetWindowLong(this.Handle, -20, exStyle | 0x00080000); // WS_EX_LAYERED = 0x00080000
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020 | 0x00000080 | 0x00080000; // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED
                return cp;
            }
        }

        public void UpdateVisuals(float[]? brightnessData = null, int tileCount = 0)
        {
            // 确保窗口可见且置顶
            if (!this.Visible)
                this.Show();
            
            // 使用SetWindowPos确保窗口在最顶层且位置正确
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, _screenW, _screenH, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            
            // 在位图上绘制内容
            DrawOverlayBitmap();
            
            // 使用UpdateLayeredWindow更新窗口
            UpdateLayeredWindowFromBitmap();
        }
    
        private void DrawOverlayBitmap()
        {
            lock (_bitmapLock)
            {
                // 只在没有位图或尺寸不匹配时重新创建，避免频繁重建位图
                if (_overlayBitmap == null || _overlayBitmap.Width != _screenW || _overlayBitmap.Height != _screenH)
                {
                    // 释放旧位图
                    _overlayBitmap?.Dispose();
                    // 创建新位图
                    _overlayBitmap = new Bitmap(_screenW, _screenH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                }
                else
                {
                    // 清空现有位图，避免旧内容残留，比重新创建更高效
                    using (Graphics g = Graphics.FromImage(_overlayBitmap))
                    {
                        // 使用透明色清空位图
                        g.Clear(Color.Transparent);
                    }
                }
                
                using (Graphics g = Graphics.FromImage(_overlayBitmap))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    
                    // 只在刷新区域绘制亮度反向的半透明白/黑色覆盖
                    foreach (var tile in _tiles.Keys)
                    {
                        // 从Dictionary中获取亮度数据
                        float brightness = _tiles[tile];
                        int bx = tile.bx;
                        int by = tile.by;
                        
                        int sx = bx * _tileSize;
                        int sy = by * _tileSize;
                        int w = Math.Min(_tileSize, _screenW - sx);
                        int h = Math.Min(_tileSize, _screenH - sy);
                        
                        // 根据亮度值决定显示黑色还是白色（反向显示）
                        Color overlayColor;
                        // 亮度 > 0.5 显示黑色，亮度 <= 0.5 显示白色（反向）
                        if (brightness > 0.5f)
                        {
                            overlayColor = Color.FromArgb(100, 0, 0, 0); // 半透明黑色
                        }
                        else
                        {
                            overlayColor = Color.FromArgb(100, 255, 255, 255); // 半透明白色
                        }
                        
                        // 在刷新区域绘制反向亮度颜色的半透明方块
                        using (var br = new SolidBrush(overlayColor))
                            g.FillRectangle(br, sx, sy, w, h);
                        
                        // 只有当边框宽度大于等于1时才绘制边框
                        if (_borderWidth >= 1)
                        {
                            // 添加边框，颜色与填充颜色相反
                            Color borderColor;
                            if (brightness > 0.5f)
                            {
                                borderColor = Color.FromArgb(180, 255, 255, 255); // 白色边框（与黑色填充相反）
                            }
                            else
                            {
                                borderColor = Color.FromArgb(180, 0, 0, 0); // 黑色边框（与白色填充相反）
                            }
                            using (var pen = new Pen(borderColor, _borderWidth))
                                g.DrawRectangle(pen, sx, sy, w-1, h-1);
                        }
                    }
                }
                
                // 清理过期瓦片列表，避免重复清除
                _expiredTiles.Clear();
            }
        }
    
        private void UpdateLayeredWindowFromBitmap()
        {
            lock (_bitmapLock)
            {
                // 检查位图是否存在
                if (_overlayBitmap == null) return;
                
                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = _overlayBitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                IntPtr hOld = SelectObject(hdcMem, hBitmap);

                Win32Point ptSrc = new Win32Point(0, 0);
                Win32Size sz = new Win32Size(_screenW, _screenH);
                Win32Point ptDest = new Win32Point(0, 0);
                
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = 0, // AC_SRC_OVER
                    BlendFlags = 0,
                    SourceConstantAlpha = 255, // 255 = fully opaque
                    AlphaFormat = 1 // AC_SRC_ALPHA
                };

                UpdateLayeredWindow(this.Handle, hdcScreen, ref ptDest, ref sz, hdcMem, ref ptSrc, 0, ref blend, 2);

                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
    
        // Win32 API declarations for UpdateLayeredWindow
        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Win32Point pptDst, ref Win32Size psize, IntPtr hdcSrc, ref Win32Point pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
            
            public Win32Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Size
        {
            public int cx;
            public int cy;
            
            public Win32Size(int cx, int cy)
            {
                this.cx = cx;
                this.cy = cy;
            }
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }
}