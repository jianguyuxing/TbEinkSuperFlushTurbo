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
using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Media;
using System.ComponentModel;

namespace TbEinkSuperFlushTurbo
{
    public partial class MainForm : Form
    {
        D3DCaptureAndCompute? _d3d;
        System.Windows.Forms.Timer? _pollTimer;
        private string _logFilePath = string.Empty;
        private StreamWriter? _logWriter;
        private CancellationTokenSource? _cts;
        private OverlayForm? _overlayForm;
        private uint _frameCounter = 0; // The new frame counter

        public Action<string>? DebugLogger { get; private set; }

        // --- Refresh parameters ---
        private int _tileSize = 8; // 区块的像素边长数，默认值8代表8*8像素
        private int _pixelDelta = 10;
        // Average window size for frame difference calculation (maximum supported: 4 frames)
        private const uint AVERAGE_WINDOW_SIZE = 3;
        private const uint STABLE_FRAMES_REQUIRED = 4;
        private const uint ADDITIONAL_COOLDOWN_FRAMES = 2;
        private const uint FIRST_REFRESH_EXTRA_DELAY = 1;

        public const int OVERLAY_DISPLAY_TIME = 100; // ms
        private int _pollInterval = 500; // ms detect period, configurable

        // 合围区域配置，用于抑制滚动区域的刷新 - 单个区域内m帧内n帧变动时，区域内区块不刷新
        private const int BOUNDING_AREA_WIDTH = 45;  // 每个合围区域宽度（区块数量）
        private const int BOUNDING_AREA_HEIGHT = 45; // 每个合围区域高度（区块数量）
        private const int BOUNDING_AREA_HISTORY_FRAMES = 3; // 历史帧数
        private const int BOUNDING_AREA_CHANGE_THRESHOLD = 3; // 变化帧阈值
        private const double BOUNDING_AREA_REFRESH_BLOCK_RATIO = 0.75; // 区块比例阈值（75%的区块变化时抑制刷新）
        private const int BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD = (int)(BOUNDING_AREA_WIDTH * BOUNDING_AREA_HEIGHT * BOUNDING_AREA_REFRESH_BLOCK_RATIO); // 区块变化数阈值（由比例计算得出）

        private int PollTimerInterval => _pollInterval; // Use configurable poll interval
        private static uint ProtectionFrames => (uint)Math.Ceiling((double)OVERLAY_DISPLAY_TIME / 500) + ADDITIONAL_COOLDOWN_FRAMES; // Use default 500ms for protection calculation

        private const double RESET_THRESHOLD_PERCENT = 95;
        private bool _forceDirectXCapture;  // 强制使用GDI+截屏 (从config.json读取)

        public bool ForceDirectXCapture
        {
            get => _forceDirectXCapture;
            set
            {
                _forceDirectXCapture = value;
                Log($"ForceDirectXCapture set to: {_forceDirectXCapture}");
            }
        }
        // 快捷键相关字段
        private const int TOGGLE_HOTKEY_ID = 9001;
        private Keys _toggleHotkey = Keys.None; // 默认无快捷键
        private bool _isRecordingHotkey = false;
        private bool _isHotkeyRegistered = false;
        // 显示器选择相关字段
        private int _targetScreenIndex = 0; // 默认使用主显示器
        
        // 超过59Hz自动停止功能
        private int _stopOver59hz = 1; // 默认开启（1开启，0关闭）
        
        // 显示器变化监控相关字段
        private string[]? _lastDisplaySignatures; // 存储上次检测的显示器签名
        private int _displayCheckCounter = 0; // 显示器检测计数器
        private const int DISPLAY_CHECK_INTERVAL = 2; // 每秒检测一次（假设500ms定时器间隔）
        private bool _isDisplayMonitoringEnabled = true; // 是否启用显示器变化监控
        private DateTime _lastDisplayChangeDetectionTime = DateTime.MinValue; // 上次检测到显示器变化的时间
        private const int DISPLAY_CHANGE_DEDUPLICATION_INTERVAL = 2000; // 去重间隔：2秒内只响应一次显示器变化

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private const int CARET_CHECK_INTERVAL = 400;
        private const int IME_CHECK_INTERVAL = 400;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int MOUSE_EXCLUSION_RADIUS_FACTOR = 2;
        private const int NOISE_DENSITY = 20;
        private const int NOISE_POINT_INTERVAL = 3;
        private const string OVERLAY_BASE_COLOR = "Black";
        private const string OVERLAY_BORDER_COLOR = "64,64,64";
        private const int OVERLAY_BORDER_WIDTH = 0;
        private const int OVERLAY_BORDER_ALPHA = 100;

        // 托盘图标相关字段
        private bool _allowVisible = true;     // 允许窗体显示
        private bool _allowClose = false;      // 允许窗体关闭
        
        // 快捷键触发提示相关字段
        private bool _isTriggeredByHotkey = false; // 是否由快捷键触发

        public MainForm()
        {
            try
            {
                // 检测并设置系统语言
                Localization.DetectAndSetLanguage();

                LoadConfig();
                InitLogFile();

                // Designer会自动调用InitializeComponent()
                InitializeComponent();

                try
                {
                    TestBrightness.TestBrightnessCalculation();
                }
                catch (Exception ex)
                {
                    Log($"Brightness test failed: {ex.Message}");
                }

                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

                _displayChangeTimer.Start();

                // 注册快捷键
                RegisterToggleHotkey();

                // 设置窗口属性以支持拖动边框缩放
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.MaximizeBox = true;
                this.MinimizeBox = true;
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.ResizeRedraw, true);

                // 初始化托盘图标
                InitializeTrayIcon();
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor ERROR: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] StackTrace: {ex.StackTrace}{Environment.NewLine}");
                throw;
            }
        }

        private void InitLogFile()
        {
            try
            {
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
                else
                {
                    CleanupOldApplicationLogFiles(logDirectory);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
                _logFilePath = Path.Combine(logDirectory, $"application_{timestamp}.log");

                _logWriter = new StreamWriter(_logFilePath, false, System.Text.Encoding.UTF8) { AutoFlush = true };

                Log("Application started");
                DebugLogger = Log;
                Log($"[Config] ProtectionFrames={ProtectionFrames}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create log file: {ex.Message}", "Logging Error", MessageBoxButtons.OK, MessageBoxIcon.None);
            }
        }

        private void LoadConfig()
        {
            try
            {
                // 先尝试加载新的JSON格式配置文件
                string configJsonPath = Path.Combine(AppContext.BaseDirectory, "config", "config.json");
                if (File.Exists(configJsonPath))
                {
                    string json = File.ReadAllText(configJsonPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("PixelDelta", out JsonElement pixelDeltaElement))
                    {
                        _pixelDelta = Math.Max(2, Math.Min(25, pixelDeltaElement.GetInt32()));
                    }
                    if (root.TryGetProperty("PollInterval", out JsonElement pollIntervalElement))
                    {
                        _pollInterval = Math.Max(200, Math.Min(5000, pollIntervalElement.GetInt32()));
                    }
                    if (root.TryGetProperty("TileSize", out JsonElement tileSizeElement))
                    {
                        _tileSize = Math.Max(8, Math.Min(64, tileSizeElement.GetInt32()));
                    }
                    if (root.TryGetProperty("ScreenIndex", out JsonElement screenIndexElement))
                    {
                        _targetScreenIndex = screenIndexElement.GetInt32();
                    }
                    // 加载快捷键配置
                    if (root.TryGetProperty("ToggleHotkey", out JsonElement hotkeyElement))
                    {
                        _toggleHotkey = (Keys)hotkeyElement.GetInt32();
                    }
                    // 加载超过59Hz自动停止配置
                    if (root.TryGetProperty("stopOver59hz", out JsonElement stopOver59hzElement))
                    {
                        _stopOver59hz = Math.Max(0, Math.Min(1, stopOver59hzElement.GetInt32()));
                    }
                    // 加载强制DirectX截屏配置
                    if (root.TryGetProperty("ForceDirectXCapture", out JsonElement forceDirectXCaptureElement))
                    {
                        _forceDirectXCapture = forceDirectXCaptureElement.GetBoolean();
                    }
                }
                else
                {
                    // 如果不存在JSON配置文件，则尝试加载旧的文本格式配置文件
                    string configTxtPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
                    if (File.Exists(configTxtPath))
                    {
                        string[] lines = File.ReadAllLines(configTxtPath);
                        if (lines.Length >= 1 && int.TryParse(lines[0], out int savedPixelDelta))
                        {
                            _pixelDelta = Math.Max(2, Math.Min(25, savedPixelDelta));
                        }
                        if (lines.Length >= 2 && int.TryParse(lines[1], out int savedPollInterval))
                        {
                            _pollInterval = Math.Max(200, Math.Min(5000, savedPollInterval));
                        }
                        if (lines.Length >= 3 && int.TryParse(lines[2], out int savedTileSize))
                        {
                            _tileSize = Math.Max(8, Math.Min(64, savedTileSize));
                        }
                        // 加载显示器索引配置
                        if (lines.Length >= 4 && int.TryParse(lines[3], out int savedScreenIndex))
                        {
                            _targetScreenIndex = savedScreenIndex;
                        }
                    }
                    
                    // 加载快捷键配置（旧方式）
                    string hotkeyConfigPath = Path.Combine(AppContext.BaseDirectory, "hotkey.json");
                    if (File.Exists(hotkeyConfigPath))
                    {
                        string json = File.ReadAllText(hotkeyConfigPath);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        if (root.TryGetProperty("ToggleHotkey", out JsonElement hotkeyElement))
                        {
                            _toggleHotkey = (Keys)hotkeyElement.GetInt32();
                        }
                    }
                    else
                    {
                        _toggleHotkey = Keys.None;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}");
                _toggleHotkey = Keys.None;
                // 设置默认值
                _forceDirectXCapture = false;
            }
        }

        private void SaveConfig()
        {
            try
            {
                // 保存为新的JSON格式配置文件（包含所有配置）
                string configDir = Path.Combine(AppContext.BaseDirectory, "config");
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }
                
                string configJsonPath = Path.Combine(configDir, "config.json");
                var config = new
                {
                    PixelDelta = _pixelDelta,
                    PollInterval = _pollInterval,
                    TileSize = _tileSize,
                    ScreenIndex = _targetScreenIndex,
                    ToggleHotkey = (int)_toggleHotkey,
                    stopOver59hz = _stopOver59hz,
                    ForceDirectXCapture = _forceDirectXCapture
                };
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configJsonPath, json);
                Log($"Saved config: PIXEL_DELTA={_pixelDelta}, POLL_INTERVAL={_pollInterval}ms, TILE_SIZE={_tileSize}, SCREEN_INDEX={_targetScreenIndex}, HOTKEY={(int)_toggleHotkey}, STOP_OVER_59HZ={_stopOver59hz}, FORCE_DIRECTX_CAPTURE={_forceDirectXCapture}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save config: {ex.Message}");
            }
        }

        public void Log(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]:  {message}";
                _logWriter?.WriteLine(logEntry);
                
                // RELEASE模式修复：确保日志在release模式下也能工作
                // 使用Trace代替Debug，因为Trace在release模式下仍然有效
                System.Diagnostics.Trace.WriteLine(logEntry);
                
                // 强制刷新日志写入器，确保日志立即写入文件
                _logWriter?.Flush();
            }
            catch { /* Ignore logging errors */ }
        }

        private void CleanupOldApplicationLogFiles(string logDirectory)
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "application_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(1)
                    .ToList();

                foreach (var file in logFiles)
                {
                    try { file.Delete(); } catch { /* Ignore delete errors */ }
                }
            }
            catch { /* Ignore cleanup errors */ }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Log("检测到显示器设置变化事件 (SystemEvents.DisplaySettingsChanged)");
            AutoStopDueToDisplayChange("显示器设置变化");
        }

        private void StopCapture()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(StopCapture));
                return;
            }

            if (_pollTimer?.Enabled == true)
            {
                btnStop?.PerformClick();
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED = 0x02E0;
            const int WM_HOTKEY = 0x0312;

            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            {
                Log("检测到显示器变化消息 (WM_DISPLAYCHANGE or WM_DPICHANGED)");
                AutoStopDueToDisplayChange("显示器配置变化");
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == TOGGLE_HOTKEY_ID)
            {
                // 全局快捷键触发（仅在非录制状态下响应）
                if (!_isRecordingHotkey)
                {
                    _isTriggeredByHotkey = true;
                    ToggleCaptureState();
                    _isTriggeredByHotkey = false;
                }
                return;
            }

            base.WndProc(ref m);
        }

        private float GetSystemDpiScale()
        {
            // 优先尝试获取指定显示器的DPI设置
            try
            {
                // 获取所有显示器信息
                var allScreens = Screen.AllScreens;
                Log($"DPI检测: 总共检测到 {allScreens.Length} 个显示器");
                
                // 检查目标显示器索引是否有效
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];
                    Log($"DPI检测: 尝试获取显示器 [{_targetScreenIndex}] 的DPI设置");
                    
                    // 尝试为特定显示器创建Graphics对象以获取其DPI
                    try
                    {
                        // 获取显示器的边界矩形
                        var bounds = targetScreen.Bounds;
                        Log($"DPI检测: 显示器 [{_targetScreenIndex}] 边界 = ({bounds.Left}, {bounds.Top}, {bounds.Width}x{bounds.Height})");
                        
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
                                    float scale = dpiX / 96f;
                                    Log($"DPI检测: 成功获取显示器 [{_targetScreenIndex}] DPI = {dpiX}, Scale = {scale:F2}");
                                    return scale;
                                }
                            }
                            finally
                            {
                                NativeMethods.DestroyWindow(tempHwnd);
                            }
                        }
                        else
                        {
                            Log($"DPI检测: 无法为显示器 [{_targetScreenIndex}] 创建临时窗口句柄");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"DPI检测: 获取显示器 [{_targetScreenIndex}] DPI时发生异常: {ex.Message}");
                        // 如果无法获取特定DPI，继续使用其他方法
                    }
                }
                else
                {
                    Log($"DPI检测: 目标显示器索引 {_targetScreenIndex} 超出范围 [0-{allScreens.Length-1}]");
                }
            }
            catch (Exception ex)
            {
                Log($"DPI检测: 枚举显示器信息时发生异常: {ex.Message}");
                // 如果无法获取特定显示器的DPI，继续使用其他方法
            }

            // 方法1: 使用GetDpiForWindow（如果窗口句柄有效）
            if (this.Handle != IntPtr.Zero)
            {
                uint windowDpi = GetDpiForWindow(this.Handle);
                if (windowDpi > 0)
                {
                    float scale = windowDpi / 96f;
                    Log($"DPI检测: Window DPI = {windowDpi}, Scale = {scale:F2}");
                    return scale;
                }
            }

            // 方法2: 使用GetDpiForSystem
            uint systemDpi = GetDpiForSystem();
            if (systemDpi > 0)
            {
                float scale = systemDpi / 96f;
                Log($"DPI检测: System DPI = {systemDpi}, Scale = {scale:F2}");
                return scale;
            }

            // 方法3: 使用Graphics对象检测主显示器DPI
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                float dpiX = graphics.DpiX;
                float scale = dpiX / 96f;
                Log($"DPI检测: Primary Screen DPI = {dpiX}, Scale = {scale:F2}");
                return scale;
            }
        }

        // 获取指定显示器的物理和逻辑分辨率
        private (int physicalWidth, int physicalHeight, int logicalWidth, int logicalHeight) GetScreenResolutions(int screenIndex)
        {
            try
            {
                // 获取目标显示器
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    screenIndex = 0; // 默认使用主显示器
                }
                
                var targetScreen = allScreens[screenIndex];
                
                // 逻辑分辨率：Screen.Bounds 返回的就是逻辑分辨率（如 2560x1440）
                int logicalWidth = targetScreen.Bounds.Width;
                int logicalHeight = targetScreen.Bounds.Height;
                
                // 物理分辨率：使用 EnumDisplaySettings 获取真实的硬件分辨率
                int physicalWidth = logicalWidth;
                int physicalHeight = logicalHeight;
                
                // 获取设备名称
                string deviceName = targetScreen.DeviceName;
                
                // 尝试使用EnumDisplaySettings获取真实的物理分辨率
                NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
                
                if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                {
                    physicalWidth = devMode.dmPelsWidth;
                    physicalHeight = devMode.dmPelsHeight;
                    Log($"通过DEVMODE获取物理分辨率: {physicalWidth}x{physicalHeight}");
                }
                else
                {
                    Log($"DEVMODE获取失败，物理分辨率使用逻辑分辨率: {physicalWidth}x{physicalHeight}");
                }
                
                // 计算DPI缩放比例
                double scaleX = (double)physicalWidth / logicalWidth;
                double scaleY = (double)physicalHeight / logicalHeight;
                
                Log($"显示器 [{screenIndex}] 分辨率信息:");
                Log($"  逻辑分辨率: {logicalWidth}x{logicalHeight} (Screen.Bounds)");
                Log($"  物理分辨率: {physicalWidth}x{physicalHeight} (DEVMODE)");
                Log($"  DPI缩放比例: {scaleX:F2}x{scaleY:F2}");
                
                return (physicalWidth, physicalHeight, logicalWidth, logicalHeight);
            }
            catch (Exception ex)
            {
                Log($"获取分辨率时发生异常: {ex.Message}");
                // 回退到使用Screen.Bounds作为逻辑分辨率
                Screen[] allScreens = Screen.AllScreens;
                var screen = screenIndex >= 0 && screenIndex < allScreens.Length ? 
                           allScreens[screenIndex] : Screen.PrimaryScreen!;
                return (screen.Bounds.Width, screen.Bounds.Height, screen.Bounds.Width, screen.Bounds.Height);
            }
        }
        
        // 获取显示器的友好名称
        private string GetScreenFriendlyName(int screenIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    screenIndex = 0; // 默认使用主显示器
                }
                
                var targetScreen = allScreens[screenIndex];
                string deviceName = targetScreen.DeviceName.Replace("\\\\.\\", ""); // 去掉前缀与下拉框格式一致
                
                // 使用 EnumDisplayDevices 获取显示器的友好名称
                NativeMethods.DISPLAY_DEVICE displayDevice = new NativeMethods.DISPLAY_DEVICE();
                displayDevice.cb = Marshal.SizeOf(displayDevice);
                
                if (NativeMethods.EnumDisplayDevices(targetScreen.DeviceName, 0, ref displayDevice, 0))
                {
                    // 如果获取到了友好名称，则返回它
                    if (!string.IsNullOrEmpty(displayDevice.DeviceString))
                    {
                        return displayDevice.DeviceString;
                    }
                }
                
                // 如果无法获取友好名称，则返回设备名称（与下拉框格式一致）
                string primaryMark = targetScreen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                return $"{deviceName}{primaryMark}";
            }
            catch (Exception ex)
            {
                Log($"获取显示器友好名称时发生异常: {ex.Message}");
                var targetScreen = Screen.AllScreens[screenIndex >= 0 && screenIndex < Screen.AllScreens.Length ? screenIndex : 0];
                string deviceName = targetScreen.DeviceName.Replace("\\\\.\\", "");
                string primaryMark = targetScreen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                return $"{deviceName}{primaryMark}";
            }
        }

        // 使用Windows API获取显示器刷新率
        private double GetRefreshRateFromApi(int screenIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    return 0.0;
                }
                
                var targetScreen = allScreens[screenIndex];
                string deviceName = targetScreen.DeviceName;
                
                // 使用EnumDisplaySettings获取当前显示模式
                NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(devMode);
                
                // ENUM_CURRENT_SETTINGS = -1, 获取当前设置
                if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                {
                    // dmDisplayFrequency 包含刷新率（Hz）
                    if (devMode.dmDisplayFrequency > 0)
                    {
                        Log($"使用Windows API成功获取显示器 {screenIndex} 刷新率: {devMode.dmDisplayFrequency}Hz");
                        return devMode.dmDisplayFrequency;
                    }
                }
                
                Log($"使用Windows API无法获取显示器 {screenIndex} 刷新率");
                return 0.0;
            }
            catch (Exception ex)
            {
                Log($"使用Windows API获取显示器 {screenIndex} 刷新率失败: {ex.Message}");
                return 0.0;
            }
        }

        // 生成显示器签名（包含索引、名称、分辨率、DPI、刷新率等）
        private string GetDisplaySignature(int index, Screen screen)
        {
            try
            {
                // 获取DPI信息（使用与下拉框相同的方法）
                uint dpiX = 96, dpiY = 96;
                try
                {
                    var bounds = screen.Bounds;
                    var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
                    
                    if (hMonitor != IntPtr.Zero)
                    {
                        NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out dpiX, out dpiY);
                    }
                }
                catch { }
                
                // 获取刷新率
                double refreshRate = GetRefreshRateFromApi(index);
                
                // 计算DPI百分比
                int dpiScalePercent = (int)(dpiX * 100 / 96);
                
                // 构建签名：索引:设备名称:分辨率:DPI:刷新率:主显示器标志
                return $"{index}:{screen.DeviceName}:{screen.Bounds.Width}x{screen.Bounds.Height}:{dpiScalePercent}:{refreshRate:F0}:{screen.Primary}";
            }
            catch (Exception ex)
            {
                Log($"生成显示器 {index} 签名失败: {ex.Message}");
                return $"{index}:{screen.DeviceName}:error:error:error:{screen.Primary}";
            }
        }

        // 记录初始显示器状态
        private void RecordInitialDisplayState()
        {
            try
            {
                var screens = Screen.AllScreens;
                _lastDisplaySignatures = new string[screens.Length];
                
                Log($"记录初始显示器状态，发现 {screens.Length} 个显示器:");
                for (int i = 0; i < screens.Length; i++)
                {
                    _lastDisplaySignatures[i] = GetDisplaySignature(i, screens[i]);
                    Log($"  显示器 [{i}] 签名: {_lastDisplaySignatures[i]}");
                }
            }
            catch (Exception ex)
            {
                Log($"记录初始显示器状态失败: {ex.Message}");
                _lastDisplaySignatures = null;
            }
        }

        // 检查显示器变化
        private void CheckDisplayChanges()
        {
            if (!_isDisplayMonitoringEnabled || _lastDisplaySignatures == null)
                return;
                
            try
            {
                var currentScreens = Screen.AllScreens;
                
                // 检查数量变化
                if (currentScreens.Length != _lastDisplaySignatures.Length)
                {
                    Log($"检测到显示器数量变化：{_lastDisplaySignatures.Length} -> {currentScreens.Length}");
                    AutoStopDueToDisplayChange("显示器数量变化");
                    return;
                }
                
                // 检查每个显示器的状态
                for (int i = 0; i < currentScreens.Length; i++)
                {
                    string currentSignature = GetDisplaySignature(i, currentScreens[i]);
                    if (currentSignature != _lastDisplaySignatures[i])
                    {
                        Log($"检测到显示器 {i} 配置变化：");
                        Log($"  原签名: {_lastDisplaySignatures[i]}");
                        Log($"  新签名: {currentSignature}");
                        AutoStopDueToDisplayChange("显示器配置变化");
                        return;
                    }
                }
                
                // 只在调试模式下记录无变化的检查（避免频繁日志输出）
                // Log("显示器配置检查完成，无变化");
            }
            catch (Exception ex)
            {
                Log($"检查显示器变化时出错：{ex.Message}");
            }
        }

        // 由于显示器变化自动停止
        private void AutoStopDueToDisplayChange(string reason)
        {
            // 去重检查：如果在去重间隔内已经处理过显示器变化，则忽略本次检测
            var now = DateTime.Now;
            var timeSinceLastDetection = now - _lastDisplayChangeDetectionTime;
            if (timeSinceLastDetection.TotalMilliseconds < DISPLAY_CHANGE_DEDUPLICATION_INTERVAL)
            {
                Log($"忽略重复的显示器变化检测（{reason}），距离上次检测仅{timeSinceLastDetection.TotalMilliseconds:F0}ms");
                return;
            }
            
            _lastDisplayChangeDetectionTime = now;
            Log($"由于{reason}，自动停止刷新");
            
            try
            {
                // 停止刷新
                if (_pollTimer?.Enabled == true)
                {
                    this.Invoke(new Action(() =>
                    {
                        if (btnStop.Enabled)
                        {
                            btnStop.PerformClick();
                        }
                    }));
                }
                
                // 显示提示信息（根据当前语言选择中文或英文）
                this.Invoke(new Action(() =>
                {
                    string title, message;
                    if (Localization.CurrentLanguage == Localization.Language.ChineseSimplified || 
                        Localization.CurrentLanguage == Localization.Language.ChineseTraditional)
                    {
                        title = "显示器配置变化";
                        message = $"检测到{reason}，刷新已自动停止。请重新选择显示器后重新开始。";
                    }
                    else
                    {
                        title = "Display Configuration Changed";
                        message = $"Detected {reason}. Screen refresh has been automatically stopped. Please reselect the display and start again.";
                    }
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
                
                // 重新初始化显示器列表和签名
                this.Invoke(new Action(() =>
                {
                    PopulateDisplayList();
                    RecordInitialDisplayState();
                }));
            }
            catch (Exception ex)
            {
                Log($"自动停止刷新时出错：{ex.Message}");
            }
        }

        public async void ManualRefresh()
        {
            if (_d3d == null) return;
            Log("[Manual] F6 triggered refresh");
            try
            {
                var (tiles, bright) = await _d3d.CaptureAndComputeOnceAsync(_frameCounter, CancellationToken.None);
                ShowTemporaryOverlay(tiles, bright);
            }
            catch (Exception ex)
            {
                Log($"[ManualRefresh] Exception: {ex}");
            }
        }

        void ShowTemporaryOverlay(List<(int bx, int by)>? tiles, float[]? brightnessData)
        {
            if (_cts?.IsCancellationRequested == true || _d3d == null || tiles == null || tiles.Count == 0) return;

            if (_overlayForm == null)
            {
                Color overlayBaseColor = Color.FromName(OVERLAY_BASE_COLOR);
                string[] rgbParts = OVERLAY_BORDER_COLOR.Split(',');
                Color borderColor = Color.FromArgb(OVERLAY_BORDER_ALPHA, int.Parse(rgbParts[0].Trim()), int.Parse(rgbParts[1].Trim()), int.Parse(rgbParts[2].Trim()));

                // 获取物理和逻辑分辨率
                var (physicalWidth, physicalHeight, logicalWidth, logicalHeight) = GetScreenResolutions(_targetScreenIndex);
                
                // 计算物理分辨率到逻辑分辨率的缩放比例
                double scaleX = (double)physicalWidth / logicalWidth;
                double scaleY = (double)physicalHeight / logicalHeight;
                
                Log($"覆盖层创建: 物理分辨率={physicalWidth}x{physicalHeight}, 逻辑分辨率={logicalWidth}x{logicalHeight}, 缩放比例={scaleX:F2}x{scaleY:F2}");
                
                // 获取目标显示器的位置信息
                var allScreens = Screen.AllScreens;
                var targetScreen = _targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length ? 
                    allScreens[_targetScreenIndex] : Screen.PrimaryScreen!;
                var screenBounds = targetScreen.Bounds;
                
                _overlayForm = new OverlayForm(_d3d.TileSize, logicalWidth, logicalHeight, NOISE_DENSITY, NOISE_POINT_INTERVAL, overlayBaseColor, borderColor, OVERLAY_BORDER_WIDTH, Log, _targetScreenIndex, scaleX, scaleY)
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    TopMost = true,
                    Size = new Size(logicalWidth, logicalHeight),
                    Location = screenBounds.Location  // 确保覆盖层显示在正确的显示器上
                };
                _overlayForm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;

                _overlayForm.Show();
                Log($"覆盖层显示在显示器 [{_targetScreenIndex}]: 位置=({screenBounds.Left}, {screenBounds.Top}), 大小={logicalWidth}x{logicalHeight}");
            }

            _overlayForm?.UpdateContent(tiles, brightnessData);
        }

        private float GetDpiScale()
        {
            // 尝试获取指定显示器的DPI设置
            try
            {
                // 获取所有显示器信息
                var allScreens = Screen.AllScreens;
                Log($"GetDpiScale: 总共检测到 {allScreens.Length} 个显示器");
                
                // 检查目标显示器索引是否有效
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];
                    Log($"GetDpiScale: 尝试获取显示器 [{_targetScreenIndex}] 的DPI设置");
                    
                    // 尝试为特定显示器创建Graphics对象以获取其DPI
                    try
                    {
                        // 获取显示器的边界矩形
                        var bounds = targetScreen.Bounds;
                        Log($"GetDpiScale: 显示器 [{_targetScreenIndex}] 边界 = ({bounds.Left}, {bounds.Top}, {bounds.Width}x{bounds.Height})");
                        
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
                                    float scale = dpiX / 96f;
                                    Log($"GetDpiScale: 成功获取显示器 [{_targetScreenIndex}] DPI = {dpiX}, Scale = {scale:F2}");
                                    return scale;
                                }
                            }
                            finally
                            {
                                NativeMethods.DestroyWindow(tempHwnd);
                            }
                        }
                        else
                        {
                            Log($"GetDpiScale: 无法为显示器 [{_targetScreenIndex}] 创建临时窗口句柄");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GetDpiScale: 获取显示器 [{_targetScreenIndex}] DPI时发生异常: {ex.Message}");
                        // 如果无法获取特定DPI，继续使用窗口DPI
                    }
                }
                else
                {
                    Log($"GetDpiScale: 目标显示器索引 {_targetScreenIndex} 超出范围 [0-{allScreens.Length-1}]");
                }
            }
            catch (Exception ex)
            {
                Log($"GetDpiScale: 枚举显示器信息时发生异常: {ex.Message}");
                // 如果无法获取特定显示器的DPI，继续使用窗口DPI
            }
            
            // 回退到原来的实现
            uint windowDpi = GetDpiForWindow(this.Handle);
            float fallbackScale = windowDpi / 96f;
            Log($"GetDpiScale: 回退到窗口DPI = {windowDpi}, Scale = {fallbackScale:F2}");
            return fallbackScale;
        }

        private string FormatShortcut(Keys keys)
        {
            var parts = new List<string>();

            if ((keys & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((keys & Keys.Alt) == Keys.Alt) parts.Add("Alt");
            if ((keys & Keys.Shift) == Keys.Shift) parts.Add("Shift");

            var keyCode = keys & Keys.KeyCode;
            if (keyCode != Keys.None)
            {
                parts.Add(keyCode.ToString());
            }

            return string.Join(" + ", parts);
        }

        // 注册快捷键
        private void RegisterToggleHotkey()
        {
            if (_isHotkeyRegistered)
            {
                UnregisterToggleHotkey();
            }

            try
            {
                // 提取虚拟键码和修饰键
                Keys keyCode = _toggleHotkey & Keys.KeyCode;
                Keys modifiers = _toggleHotkey & Keys.Modifiers;

                int modFlags = 0;
                if ((modifiers & Keys.Control) == Keys.Control)
                    modFlags |= 0x0002; // MOD_CONTROL
                if ((modifiers & Keys.Alt) == Keys.Alt)
                    modFlags |= 0x0001; // MOD_ALT
                if ((modifiers & Keys.Shift) == Keys.Shift)
                    modFlags |= 0x0004; // MOD_SHIFT

                // 注册系统级快捷键
                bool result = RegisterHotKey(this.Handle, TOGGLE_HOTKEY_ID, modFlags, (int)keyCode);
                _isHotkeyRegistered = result;
                
                if (!result)
                {
                    Log($"Failed to register hotkey: {_toggleHotkey}");
                }
                else
                {
                    Log($"Successfully registered hotkey: {_toggleHotkey}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error registering hotkey: {ex.Message}");
                _isHotkeyRegistered = false;
            }
        }

        // 取消注册快捷键
        private void UnregisterToggleHotkey()
        {
            if (_isHotkeyRegistered)
            {
                UnregisterHotKey(this.Handle, TOGGLE_HOTKEY_ID);
                _isHotkeyRegistered = false;
                Log("Hotkey unregistered");
            }
        }

        private void SaveToggleHotkey()
        {
            SaveConfig();
            RegisterToggleHotkey();
        }

        private void CancelHotkeyRecording()
        {
            _isRecordingHotkey = false;
            _toggleHotkey = Keys.None;
            txtToggleHotkey.Text = Localization.GetText("ClickButtonToSet");
            btnToggleRecord.Text = "●";
            btnToggleRecord.ForeColor = Color.Red;
            btnToggleRecord.BackColor = Color.White;

            Log("Hotkey recording cancelled");
        }

        private void ToggleCaptureState()
        {
            // 检查当前焦点窗口
            IntPtr foregroundWindow = GetForegroundWindow();
            bool isCurrentWindowFocused = (foregroundWindow == this.Handle);
            
             // 只有当当前窗口没有焦点时才显示气泡提示
            bool shouldShowNotification = !isCurrentWindowFocused;

            if (_pollTimer?.Enabled != true)
            {
                // 启动捕获
                if (shouldShowNotification)
                {
                    ShowNotification(Localization.GetText("CaptureStartedTitle"), Localization.GetText("CaptureStartedMessage"));
                }
                btnStart.PerformClick();
            }
            else
            {
                // 停止捕获
                if (shouldShowNotification)
                {
                    ShowNotification(Localization.GetText("CaptureStoppedTitle"), Localization.GetText("CaptureStoppedMessage"));
                }
                btnStop.PerformClick();
            }
        }

        // 显示托盘通知
        private void ShowNotification(string title, string message)
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(2000);
        }

        private void UpdateAdaptiveLayout()
        {
            // 完全移除自适应布局逻辑，因为它在高DPI环境下引起问题
            // 依赖于WinForm内置的DPI处理机制
        }

        // ==================== Designer Event Handlers ====================

        private void MainForm_Load(object? sender, EventArgs e)
        {
            // 更新控件文本为本地化文本
            UpdateLocalizedTexts();

            // 更新控件值
            trackPixelDelta.Value = _pixelDelta;
            lblPixelDeltaValue.Text = _pixelDelta.ToString();
            trackPollInterval.Value = _pollInterval;
            lblPollIntervalValue.Text = _pollInterval.ToString();
            
            // 如果没有设置快捷键，显示提示文本
            if (_toggleHotkey == Keys.None)
            {
                txtToggleHotkey.Text = Localization.GetText("ClickButtonToSet");
            }
            else
            {
                txtToggleHotkey.Text = FormatShortcut(_toggleHotkey);
            }

            // 设置初始按钮状态
            lblInfo.Text = Localization.GetText("StatusStopped");

            // 填充显示器列表
            PopulateDisplayList();

            // 执行自适应布局
            UpdateAdaptiveLayout();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                UpdateAdaptiveLayout();
                AdjustStatusLabelProperties(); // 窗口大小改变时重新调整状态标签
            }
        }

        // 查找刷新率最小的显示器索引
        private int FindLowestRefreshRateDisplay(Screen[] screens)
        {
            try
            {
                if (screens.Length == 0)
                    return 0;
                
                if (screens.Length == 1)
                    return 0;

                double minRefreshRate = double.MaxValue;
                int minIndex = 0;

                for (int i = 0; i < screens.Length; i++)
                {
                    double refreshRate = GetRefreshRateFromApi(i);
                    
                    // 如果获取刷新率失败（返回0），给一个默认值60Hz以便比较
                    if (refreshRate <= 0)
                        refreshRate = 60.0;
                    
                    Log($"检测显示器 {i} 刷新率: {refreshRate:F1}Hz");
                    
                    // 选择刷新率最小的显示器
                    if (refreshRate < minRefreshRate)
                    {
                        minRefreshRate = refreshRate;
                        minIndex = i;
                    }
                }

                Log($"选择刷新率最小的显示器: 索引 {minIndex}, 刷新率 {minRefreshRate:F1}Hz");
                return minIndex;
            }
            catch (Exception ex)
            {
                Log($"查找最小刷新率显示器失败: {ex.Message}, 默认使用索引0");
                return 0;
            }
        }

        private void PopulateDisplayList()
        {
            try
            {
                comboDisplay.Items.Clear();
                
                // 获取所有显示器，使用系统默认顺序（不特殊处理主显示器）
                var screens = Screen.AllScreens;
                
                // 添加详细的显示器调试信息
                Log($"系统发现 {screens.Length} 个显示器:");
                for (int debugIdx = 0; debugIdx < screens.Length; debugIdx++)
                {
                    var debugScreen = screens[debugIdx];
                    Log($"  显示器 [{debugIdx}]: {debugScreen.DeviceName}, 主显示器: {debugScreen.Primary}, 边界: {debugScreen.Bounds}");
                }
                Log($"配置文件中的目标显示器索引: {_targetScreenIndex}");
                
                for (int i = 0; i < screens.Length; i++)
                {
                    var screen = screens[i];
                    
                    // 获取显示器的DPI信息（使用GetDpiForMonitor方法）
                    uint dpiX = 96;
                    uint dpiY = 96;
                    try
                    {
                        // 获取显示器中心点
                        var bounds = screen.Bounds;
                        var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                        
                        // 获取显示器句柄
                        IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
                        
                        if (hMonitor != IntPtr.Zero)
                        {
                            // 使用GetDpiForMonitor获取DPI信息（不使用V2版本）
                        int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out dpiX, out dpiY);
                            
                            if (result == 0)
                            {
                                Log($"成功获取显示器 {i} DPI: {dpiX}x{dpiY}");
                            }
                            else
                            {
                                Log($"获取显示器 {i} DPI失败，错误码: 0x{result:X8}，使用默认值96 DPI");
                                dpiX = 96;
                                dpiY = 96;
                            }
                        }
                        else
                        {
                            Log($"无法获取显示器 {i} 的监视器句柄，使用默认值96 DPI");
                            dpiX = 96;
                            dpiY = 96;
                        }
                    }
                    catch (Exception dpiEx)
                    {
                        Log($"获取显示器 {i} DPI失败: {dpiEx.Message}，使用默认值96 DPI");
                        dpiX = 96;
                        dpiY = 96;
                    }
                    
                    // 计算DPI百分比（相对于标准96 DPI）
                    int dpiScalePercent = (int)(dpiX * 100 / 96);
                    
                    // 获取刷新率（使用Windows API）
                    double refreshRate = GetRefreshRateFromApi(i);
                    
                    // 构建显示名称，包含DPI和刷新率信息
                    string dpiInfo = $"{dpiScalePercent}%";
                    string refreshInfo = refreshRate > 0 ? $" {refreshRate:F0}Hz" : "";
                    string primaryMark = screen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                    // 使用设备名称确保正确匹配，但保持格式简洁
                    string deviceName = screen.DeviceName.Replace("\\\\.\\", ""); // 去掉前缀使显示更简洁
                    string displayName = $"{deviceName}{primaryMark}: {screen.Bounds.Width}×{screen.Bounds.Height} @ {dpiInfo}{refreshInfo}";
                    comboDisplay.Items.Add(displayName);
                }
                
                // 根据配置文件选择显示器（使用简单直接的索引匹配）
                if (comboDisplay.Items.Count > 0)
                {
                    if (_targetScreenIndex >= 0 && _targetScreenIndex < comboDisplay.Items.Count)
                    {
                        // 配置文件中的索引有效，直接使用该索引
                        comboDisplay.SelectedIndex = _targetScreenIndex;
                    }
                    else
                    {
                         // 配置文件中的索引无效，选择刷新率最小的显示器
                        int targetIndex = FindLowestRefreshRateDisplay(screens);
                        Log($"配置文件中的显示器索引 {_targetScreenIndex} 无效，选择刷新率最小的显示器索引 {targetIndex}");
                        comboDisplay.SelectedIndex = targetIndex;
                        _targetScreenIndex = targetIndex;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error populating display list: {ex.Message}");
                comboDisplay.Items.Add($"DISPLAY1 [{Localization.GetText("Primary")}]: 1920×1080 @ 100% 60Hz");
                comboDisplay.SelectedIndex = 0;
                _targetScreenIndex = 0;
            }
        }

        private void comboDisplay_SelectedIndexChanged(object? sender, EventArgs e)
        {
            try
            {
                // 只在选择真正改变时处理
                if (comboDisplay.SelectedIndex != _targetScreenIndex)
                {
                    // 检查是否正在截屏
                    if (_pollTimer != null && _pollTimer.Enabled)
                    {
                        string message = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                            "截屏运行中，停止截屏后才能切换显示器。" :
                            "Screen capture is running. Please stop capture first before switching display.";
                        string title = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                            "提示" :
                            "Notice";
                        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        
                        // 恢复原来的选择，但不阻止用户操作
                        comboDisplay.SelectedIndex = _targetScreenIndex;
                        return;
                    }

                    // 只有在截屏停止时才允许切换
                    if (comboDisplay.SelectedIndex >= 0)
                    {
                        _targetScreenIndex = comboDisplay.SelectedIndex;
                        SaveConfig(); // 保存配置到文件
                        Log($"Display changed to index: {_targetScreenIndex}");
                        
                        // 销毁现有的覆盖层，确保下次会在新显示器上重新创建
                        if (_overlayForm != null)
                        {
                            _overlayForm.Close();
                            _overlayForm.Dispose();
                            _overlayForm = null;
                            Log($"覆盖层已销毁，将在新显示器 [{_targetScreenIndex}] 上重新创建");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error changing display selection: {ex.Message}");
            }
        }

        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_isRecordingHotkey)
            {
                // 录制模式：捕获按键
                e.Handled = true;
                e.SuppressKeyPress = true;

                if (e.KeyCode == Keys.Escape)
                {
                    // ESC键取消录制
                    CancelHotkeyRecording();
                    return;
                }

                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
                {
                    // 忽略单独的修饰键
                    return;
                }

                // 获取按键组合
                Keys keyCombo = e.KeyData;

                // 更新内部变量和显示
                _toggleHotkey = keyCombo;
                string formattedShortcut = FormatShortcut(keyCombo);
                txtToggleHotkey.Text = formattedShortcut;

                Log($"Hotkey recorded: {formattedShortcut} - continue recording");
            }
            else if (e.KeyData == _toggleHotkey && _isHotkeyRegistered && !_isRecordingHotkey)
            {
                // 切换运行状态（仅在非录制状态下响应）
                e.Handled = true;
                e.SuppressKeyPress = true;
                ToggleCaptureState();
            }
        }

        private void btnStart_Click(object? sender, EventArgs e)
        {
            // 检查是否正在录制快捷键
            if (_isRecordingHotkey)
            {
                string message = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                    "无法在录制热键时启动截屏，请先完成热键录制。" :
                    "Cannot start screen capture while recording hotkey, Please complete hotkey recording first.";
                string title = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                    "热键录制进行中" : 
                    "Hotkey Recording in Progress";
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.None);
                return;
            }

            // 检查显示器刷新率（超过59Hz自动停止功能）
            if (_stopOver59hz == 1)
            {
                double refreshRate = GetRefreshRateFromApi(_targetScreenIndex);
                if (refreshRate >= 59.0)
                {
                    string message = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                        $"为了避免误选择，默认禁止在超过59Hz的显示器上运行。当前显示器刷新率为{refreshRate:F1}Hz。若您的墨水屏超过59Hz或刷新率检测错误，请点击齿轮关闭此限制" :
                        $"To avoid mis-selection, screen capture is disabled by default on displays over 59Hz. Current display refresh rate is {refreshRate:F1}Hz. If your e-ink display is over 59Hz or refresh rate detection is incorrect, please click the gear button to disable this restriction.";
                    string title = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                        "刷新率限制" : 
                        "Refresh Rate Limit";
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Log($"Blocked start due to high refresh rate: {refreshRate:F1}Hz (stopOver59hz={_stopOver59hz})");
                    return;
                }
            }

            btnStart.Enabled = false;
            _cts = new CancellationTokenSource();
            _frameCounter = 0; // Reset frame counter on start

            // 禁用设置项修改
            trackPixelDelta.Enabled = false;
            trackPollInterval.Enabled = false;
            // 注意：齿轮按钮保持启用状态，通过点击事件拦截来处理禁用逻辑

            lblInfo.Text = "Status: Initializing GPU capture...";
            Log("Initializing GPU capture...");
            
            // 记录初始显示器状态（用于变化检测）
            RecordInitialDisplayState();
            
            try
            {
                _d3d = new D3DCaptureAndCompute(DebugLogger, _tileSize, _pixelDelta, AVERAGE_WINDOW_SIZE, STABLE_FRAMES_REQUIRED, ADDITIONAL_COOLDOWN_FRAMES, FIRST_REFRESH_EXTRA_DELAY, CARET_CHECK_INTERVAL, IME_CHECK_INTERVAL, MOUSE_EXCLUSION_RADIUS_FACTOR,
                    new BoundingAreaConfig(
                        BOUNDING_AREA_WIDTH,
                        BOUNDING_AREA_HEIGHT,
                        BOUNDING_AREA_HISTORY_FRAMES,
                        BOUNDING_AREA_CHANGE_THRESHOLD,
                        BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD), _forceDirectXCapture, ProtectionFrames, _targetScreenIndex);

                _pollTimer = new System.Windows.Forms.Timer
                {
                    Interval = _pollInterval
                };
                _pollTimer.Tick += async (ss, ee) =>
                {
                    try
                    {
                        // 每3秒检查一次显示器变化（计数器达到DISPLAY_CHECK_INTERVAL时检测）
                        _displayCheckCounter++;
                        if (_displayCheckCounter >= DISPLAY_CHECK_INTERVAL)
                        {
                            _displayCheckCounter = 0;
                            CheckDisplayChanges();
                        }

                        if (_cts.Token.IsCancellationRequested || _d3d == null) return;

                        _frameCounter++; // Increment frame counter

                        // 定期释放内存压力（每100帧约50秒）
                        if (_frameCounter % 100 == 0)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            Log($"Memory pressure relieved at frame {_frameCounter}");
                        }

                        // Capture screen and compute differences
                        var (tilesToRefresh, brightnessData) = await _d3d.CaptureAndComputeOnceAsync(_frameCounter, _cts.Token);
                        if (_cts.Token.IsCancellationRequested) return;

                        if (tilesToRefresh.Count > 0)
                        {
                            double refreshRatio = (double)tilesToRefresh.Count / (_d3d.TilesX * _d3d.TilesY);
                            if (refreshRatio >= RESET_THRESHOLD_PERCENT / 100.0)
                            {
                                Log($"System-wide refresh detected ({refreshRatio:P1}), skipping overlay.");
                            }
                            else
                            {
                                Log($"Tiles to refresh: {tilesToRefresh.Count}");
                                this.Invoke(new Action(() =>
                                {
                                    listBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}:  tiles: {tilesToRefresh.Count}");
                                    if (listBox.Items.Count > 200) listBox.Items.RemoveAt(listBox.Items.Count - 1);
                                }));
                                ShowTemporaryOverlay(tilesToRefresh, brightnessData);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in poll timer: {ex.Message}");
                        Log($"Stack trace: {ex.StackTrace}");
                        
                        // 如果发生严重错误，自动停止捕获
                        if (ex is ArgumentException || ex is OutOfMemoryException)
                        {
                            Log($"Critical error detected, stopping capture automatically");
                            this.Invoke(new Action(() =>
                            {
                                if (btnStop.Enabled)
                                {
                                    btnStop.PerformClick();
                                }
                            }));
                        }
                    }
                };
                _pollTimer.Start();

                // 获取物理和逻辑分辨率
                var (physicalWidth, physicalHeight, logicalWidth, logicalHeight) = GetScreenResolutions(_targetScreenIndex);
                
                // 获取显示器友好名称
                string screenFriendlyName = GetScreenFriendlyName(_targetScreenIndex);
                
                // 使用Windows API获取的DPI重新计算逻辑分辨率，确保与下拉框显示一致
                // 获取当前显示器的DPI
                uint dpiX = 96, dpiY = 96;
                try
                {
                    var targetScreen = Screen.AllScreens[_targetScreenIndex];
                    var bounds = targetScreen.Bounds;
                    var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
                    
                    if (hMonitor != IntPtr.Zero)
                    {
                        int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out dpiX, out dpiY);
                        if (result == 0)
                        {
                            Log($"状态栏使用显示器 {_targetScreenIndex} DPI: {dpiX}x{dpiY}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"状态栏获取DPI失败: {ex.Message}，使用默认值96 DPI");
                    dpiX = 96;
                    dpiY = 96;
                }
                
                // 使用Windows API的DPI重新计算逻辑分辨率
                double scaleX = (double)dpiX / 96.0;
                double scaleY = (double)dpiY / 96.0;
                logicalWidth = (int)(physicalWidth / scaleX);
                logicalHeight = (int)(physicalHeight / scaleY);
                
                // 计算DPI缩放比例：物理分辨率 ÷ 逻辑分辨率
                double dpiScaleX = (double)physicalWidth / logicalWidth;
                double dpiScaleY = (double)physicalHeight / logicalHeight;
                double dpiScale = Math.Max(dpiScaleX, dpiScaleY); // 使用较大的缩放比例
                int scalePercent = (int)(dpiScale * 100);
                lblInfo.Text = $"{Localization.GetText("StatusRunning")} (Display: {screenFriendlyName}, Physical: {physicalWidth}x{physicalHeight}, Logical: {logicalWidth}x{logicalHeight}, Scale: {scalePercent}%, Tile Size: {_tileSize}x{_tileSize} pixels)";
                btnStop.Enabled = true;
                Log($"GPU capture initialized successfully. Physical: {physicalWidth}x{physicalHeight}, Logical: {logicalWidth}x{logicalHeight} (DXGI), Scale: {scalePercent}%, DPI: {dpiScaleX:F2}x{dpiScaleY:F2}, Tile Size: {_tileSize}x{_tileSize} pixels");
                
                // D3D初始化完成后，重新填充显示器列表以获取刷新率信息
                this.Invoke(new Action(() =>
                {
                    PopulateDisplayList();
                }));
            }
            catch (Exception ex)
            {
                string errorMessage = $"Initialization failed: {ex.Message}";
                Log(errorMessage + "\n" + ex.StackTrace);
                MessageBox.Show(errorMessage);
                btnStart.Enabled = true;
                // 齿轮按钮保持启用状态，无需特别处理
                lblInfo.Text = "Status: Failed";
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void btnStop_Click(object? sender, EventArgs e)
        {
            Log("Stopping GPU capture...");
            _cts?.Cancel();
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
            _d3d?.Dispose();
            _d3d = null;

            lblInfo.Text = Localization.GetText("StatusStopped");
            btnStart.Enabled = true;
            btnStop.Enabled = false;

            // 重新启用设置项修改
            trackPixelDelta.Enabled = true;
            trackPollInterval.Enabled = true;
            // 齿轮按钮保持启用状态，无需特别处理
        }

        private void btnSettings_Click(object? sender, EventArgs e)
        {
            // 检查是否正在截屏
            if (_pollTimer != null && _pollTimer.Enabled)
            {
                string message = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                    "截屏运行中，请先停止截屏再修改设置。" :
                    "Screen capture is running, please stop screen capture first before modifying settings.";
                string title = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                    "提示" : 
                    "Information";
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var settingsForm = new SettingsForm(_stopOver59hz == 1))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    _stopOver59hz = settingsForm.StopOver59Hz ? 1 : 0;
                    SaveConfig();
                    Log($"设置已更新：停止超过59Hz显示器 = {(settingsForm.StopOver59Hz ? "开启" : "关闭")}");
                }
            }
        }

        private void trackPixelDelta_ValueChanged(object? sender, EventArgs e)
        {
            _pixelDelta = trackPixelDelta.Value;
            lblPixelDeltaValue.Text = _pixelDelta.ToString();
            SaveConfig();
        }

        private void trackPollInterval_ValueChanged(object? sender, EventArgs e)
        {
            _pollInterval = trackPollInterval.Value;
            lblPollIntervalValue.Text = _pollInterval.ToString();
            SaveConfig();
            // 更新定时器间隔
            if (_pollTimer != null)
            {
                _pollTimer.Interval = _pollInterval;
            }
        }

        private void btnHelpPixelDelta_Click(object? sender, EventArgs e)
        {
            string helpText;
            string title;

            // 根据当前语言显示相应语言的帮助内容
            if (Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional)
            {
                helpText = "像素颜色差异阈值说明:\n\n" +
                    "控制区块内每个颜色通道(R/G/B)亮度变化的敏感度。\n\n" +
                    "• 较低值(2-8): 适合默认浅色主题，检测细微变化\n" +
                    "  （区分白色和浅灰色及浅亮度彩色）\n\n" +
                    "• 较高值(15-25): 适合高对比度主题，忽略微小变化\n\n" +
                    "推荐: 从10开始，根据您的主题进行调整。";
                title = "像素颜色差异阈值 - 详细说明";
            }
            else
            {
                helpText = "Pixel Color Diff Threshold:\n\n" +
                    "Controls the sensitivity to luminance changes in individual color channels (R/G/B) for each tile.\n\n" +
                    "• Lower values (2-8): Better for default light themes, detects subtle changes\n" +
                    "  (Distinguishes white from light gray and low-brightness colors)\n\n" +
                    "• Higher values (15-25): Better for high-contrast themes, ignores minor variations\n\n" +
                    "Recommended: Start with 10 and adjust based on your theme.";
                title = "Pixel Color Diff Threshold - Help";
            }

            MessageBox.Show(helpText, title, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        private void btnHelpPixelDelta_Paint(object? sender, PaintEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            // 创建圆形区域
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(0, 0, btn.Width - 1, btn.Height - 1);
                btn.Region = new Region(path);
            }

            // 绘制圆形背景
            using (var backgroundBrush = new SolidBrush(btn.BackColor))
            {
                e.Graphics.FillEllipse(backgroundBrush, 0, 0, btn.Width - 1, btn.Height - 1);
            }

            using (var font = new Font(btn.Font.FontFamily, btn.Font.Size, btn.Font.Style))
            using (var brush = new SolidBrush(btn.BackColor == Color.FromArgb(135, 206, 235) ? Color.White : btn.ForeColor))
            {
                // 精确测量文本尺寸
                var textSize = e.Graphics.MeasureString("?", font);
                // 计算精确位置实现完美居中，并在圆圈内右移2个像素
                var x = (btn.Width - textSize.Width) / 2 + 2;
                var y = (btn.Height - textSize.Height) / 2;
                e.Graphics.DrawString("?", font, brush, x, y);
            }
        }

        private void btnHelpPixelDelta_MouseEnter(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.FromArgb(135, 206, 235); // 稍微暗一点的淡蓝色
                btn.Cursor = Cursors.Hand;
            }
        }

        private void btnHelpPixelDelta_MouseLeave(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.LightBlue;
                btn.Cursor = Cursors.Default;
            }
        }

        private void btnToggleRecord_Click(object? sender, EventArgs e)
        {
            if (_isRecordingHotkey)
            {
                // 如果正在录制，优先处理录制逻辑
                // 停止录制，如果没有输入任何按键则清空快捷键
                _isRecordingHotkey = false;
                btnToggleRecord.Text = "●";
                btnToggleRecord.ForeColor = Color.Red;
                btnToggleRecord.BackColor = Color.White;

                // 如果文本框显示的是提示文字，说明用户没有输入任何按键
                if (txtToggleHotkey.Text == Localization.GetText("PressHotkeyCombination"))
                {
                    // 用户没有输入任何按键，清空快捷键
                    CancelHotkeyRecording();
                }
                else
                {
                    // 用户输入了按键，保存快捷键
                    SaveToggleHotkey();
                }
            }
            else
            {
                // 如果不在录制状态，检查是否正在运行
                if (_pollTimer != null && _pollTimer.Enabled)
                {
                    string message = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                        "运行时无法修改热键，请先停止截屏。" :
                        "Cannot modify hotkey while screen capture is running, Please stop screen capture first.";
                    string title = Localization.CurrentLanguage == Localization.Language.ChineseSimplified || Localization.CurrentLanguage == Localization.Language.ChineseTraditional ?
                        "警告" : 
                        "Warning";
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.None);
                    return;
                }

                // 开始录制
                _isRecordingHotkey = true;
                btnToggleRecord.Text = "✓"; // 改为对勾符号
                btnToggleRecord.BackColor = Color.White;
                txtToggleHotkey.Text = Localization.GetText("PressHotkeyCombination");

                // 开始录制时临时注销当前快捷键，避免冲突
                if (_isHotkeyRegistered)
                {
                    NativeMethods.UnregisterHotKey(this.Handle, TOGGLE_HOTKEY_ID);
                    _isHotkeyRegistered = false;
                }

                // 开始录制时清空临时变量，确保每次录制都是全新的开始
                _toggleHotkey = Keys.None;
            }
        }

        private void btnToggleRecord_Paint(object? sender, PaintEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var text = btn.Text;

            // 绘制背景
            e.Graphics.Clear(btn.BackColor);

            // 根据文本内容选择不同的绘制方式，确保都基于中心对齐
            using (var font = new Font(btn.Font.FontFamily, btn.Font.Size, btn.Font.Style))
            {
                if (text == "●") // 圆点 - 改为圆环+内部小圆点
                {
                    // 检查是否是录制按钮，如果是则使用图片绘制
                    if (btn == btnToggleRecord)
                    {
                        DrawRecordButton(e.Graphics, btn.Width, btn.Height);
                    }
                    else
                    {
                        using (var brush = new SolidBrush(btn.ForeColor))
                        {
                            // 绘制圆环 - 参考方点尺寸，不要让圆环占满整个按钮
                            int baseSize = Math.Min(btn.Width, btn.Height);
                            int outerDiameter = baseSize - 32; // 再放大圆环，从-36改为-32
                            int ringThickness = 2; // 固定环厚度，不再DPI缩放
                            int x = (btn.Width - outerDiameter) / 2;
                            int y = (btn.Height - outerDiameter) / 2;

                            // 绘制外圆环（红色）
                            using (var redBrush = new SolidBrush(Color.Red))
                            {
                                e.Graphics.FillEllipse(redBrush, x, y, outerDiameter, outerDiameter);
                            }

                            // 绘制内圆（白色背景，形成圆环效果）
                            int innerDiameter = outerDiameter - (ringThickness * 2);
                            int innerX = x + ringThickness;
                            int innerY = y + ringThickness;
                            e.Graphics.FillEllipse(new SolidBrush(btn.BackColor), innerX, innerY, innerDiameter, innerDiameter);

                            // 绘制中心小圆点 - 固定尺寸，不再DPI缩放
                            int centerDiameter = innerDiameter - 12; // 减小中心圆点尺寸，保持比例协调
                            int centerX = (btn.Width - centerDiameter) / 2;
                            int centerY = (btn.Height - centerDiameter) / 2;
                            e.Graphics.FillEllipse(brush, centerX, centerY, centerDiameter, centerDiameter);
                        }
                    }
                }
                else if (text == "✓") // 对勾符号
                {
                    // 绘制绿色对勾图像而不是文字
                    DrawGreenCheckmark(e.Graphics, btn.Width, btn.Height);
                }
                else // 其他字符，使用文本绘制
                {
                    using (var brush = new SolidBrush(btn.ForeColor))
                    {
                        var textSize = e.Graphics.MeasureString(text, font);
                        var x = (btn.Width - textSize.Width) / 2;
                        var y = (btn.Height - textSize.Height) / 2;
                        e.Graphics.DrawString(text, font, brush, x, y);
                    }
                }
            }
        }

        private void DrawGreenCheckmark(Graphics g, int width, int height)
        {
            // 首先尝试加载自定义图片
            try
            {
                string imagePath = Path.Combine(Application.StartupPath, "Resources", "checkmark.png");
                if (File.Exists(imagePath))
                {
                    using (var image = Image.FromFile(imagePath))
                    {
                        // 计算居中位置
                        int x = (width - image.Width) / 2;
                        int y = (height - image.Height) / 2;
                        
                        // 确保图片不会超出按钮边界
                        if (image.Width <= width && image.Height <= height)
                        {
                            g.DrawImage(image, x, y, image.Width, image.Height);
                        }
                        else
                        {
                            // 如果图片太大，则按比例缩放
                            float scale = Math.Min((float)width / image.Width, (float)height / image.Height) * 0.8f;
                            int scaledWidth = (int)(image.Width * scale);
                            int scaledHeight = (int)(image.Height * scale);
                            x = (width - scaledWidth) / 2;
                            y = (height - scaledHeight) / 2;
                            g.DrawImage(image, x, y, scaledWidth, scaledHeight);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果加载图片失败，继续使用绘制的绿色方块
                System.Diagnostics.Debug.WriteLine($"Failed to load checkmark image: {ex.Message}");
            }
            
            // 如果没有找到图片或加载失败，使用绿色方块作为备选
            using (var brush = new SolidBrush(Color.Green))
            {
                // 绘制一个居中的绿色方块
                int squareSize = Math.Min(width, height) / 2;
                int x = (width - squareSize) / 2;
                int y = (height - squareSize) / 2;
                g.FillRectangle(brush, x, y, squareSize, squareSize);
            }
        }
        
        private void DrawRecordButton(Graphics g, int width, int height)
        {
            // 首先尝试加载自定义图片
            try
            {
                string imagePath = Path.Combine(Application.StartupPath, "Resources", "record_button.png");
                if (File.Exists(imagePath))
                {
                    using (var image = Image.FromFile(imagePath))
                    {
                        // 计算居中位置
                        int x = (width - image.Width) / 2;
                        int y = (height - image.Height) / 2;
                        
                        // 确保图片不会超出按钮边界
                        if (image.Width <= width && image.Height <= height)
                        {
                            g.DrawImage(image, x, y, image.Width, image.Height);
                        }
                        else
                        {
                            // 如果图片太大，则按比例缩放
                            float scale = Math.Min((float)width / image.Width, (float)height / image.Height) * 0.8f;
                            int scaledWidth = (int)(image.Width * scale);
                            int scaledHeight = (int)(image.Height * scale);
                            x = (width - scaledWidth) / 2;
                            y = (height - scaledHeight) / 2;
                            g.DrawImage(image, x, y, scaledWidth, scaledHeight);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果加载图片失败，继续使用绘制的红色圆点
                System.Diagnostics.Debug.WriteLine($"Failed to load record button image: {ex.Message}");
            }
            
            // 如果没有找到图片或加载失败，使用红色圆点作为备选
            using (var brush = new SolidBrush(Color.Red))
            {
                // 绘制一个居中的红色圆点
                int diameter = Math.Min(width, height) / 2;
                int x = (width - diameter) / 2;
                int y = (height - diameter) / 2;
                g.FillEllipse(brush, x, y, diameter, diameter);
            }
        }
        
        private void btnToggleRecord_MouseEnter(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.LightGray; // 悬停时背景变灰
            }
        }
        
        private void btnToggleRecord_MouseLeave(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.White; // 离开时恢复白色
            }
        }

        private void btnSettings_MouseEnter(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.DarkBlue; // 悬停时背景变为深蓝色
                btn.ForeColor = Color.White; // 悬停时齿轮文本变为白色
            }
        }

        private void btnSettings_MouseLeave(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.White; // 离开时恢复白色背景
                btn.ForeColor = SystemColors.ControlText; // 离开时恢复系统默认文本颜色
            }
        }

        private void _displayChangeTimer_Tick(object? sender, EventArgs e)
        {
            if (_d3d != null)
            {
                double refreshRate = _d3d.GetCurrentPrimaryDisplayRefreshRate();
                if (refreshRate >= 59.0 && _stopOver59hz == 1)
                {
                    Log($"High refresh rate detected ({refreshRate:F2}Hz), stopping capture. (stopOver59hz={_stopOver59hz})");
                    StopCapture();
                }
            }
        }

        private void InitializeTrayIcon()
        {
            // 设置托盘图标
            _trayIcon.Icon = this.Icon ?? SystemIcons.Application;
            _trayIcon.Visible = true;
            _trayIcon.Text = Localization.GetText("TrayIconText");
            
            // 创建托盘菜单
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            
            // 显示面板菜单项
            ToolStripMenuItem showItem = new ToolStripMenuItem(Localization.GetText("ShowPanel"));
            showItem.Click += (sender, e) => ShowMainForm();
            trayMenu.Items.Add(showItem);
            
            // 分隔符
            trayMenu.Items.Add(new ToolStripSeparator());
            
            // 退出菜单项
            ToolStripMenuItem exitItem = new ToolStripMenuItem(Localization.GetText("Exit"));
            exitItem.Click += (sender, e) => ExitApplication();
            trayMenu.Items.Add(exitItem);
            
            // 设置托盘图标上下文菜单
            _trayIcon.ContextMenuStrip = trayMenu;
            
            // 设置托盘图标点击事件
            _trayIcon.MouseClick += TrayIcon_MouseClick;
            _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;
        }

        // 显示主窗体
        private void ShowMainForm()
        {
            _allowVisible = true;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        // 退出应用程序
        private void ExitApplication()
        {
            _allowClose = true;
            this.Close();
        }

        // 托盘图标单击事件
        private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainForm();
            }
        }

        // 托盘图标双击事件
        private void TrayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            ShowMainForm();
        }

        // 重写OnResize方法处理最小化到任务栏
        protected override void OnResize(EventArgs e)
        {
            // 当窗口最小化时，正常最小化到任务栏
            base.OnResize(e);
        }

        // 重写OnFormClosing方法控制窗体关闭
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 区分关闭原因：
            // 如果是用户点击关闭按钮，则隐藏窗口但不退出程序
            // 如果是其他原因（如系统关机），则正常退出
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                // 用户点击关闭按钮[X]，隐藏窗口但保持托盘图标
                e.Cancel = true;
                this.Hide(); // 隐藏窗口而不是最小化
                
                // 显示托盘提示
                _trayIcon.BalloonTipTitle = Localization.GetText("MinimizedToTrayTitle");
                _trayIcon.BalloonTipText = Localization.GetText("MinimizedToTrayMessage");
                _trayIcon.ShowBalloonTip(2000);
                
                return;
            }

            UnregisterToggleHotkey();
            _overlayForm?.HideOverlay();
            _displayChangeTimer?.Stop();

            base.OnFormClosing(e);
        }

        // 退出菜单项点击事件
        private void exitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            ExitApplication();
        }

        private void listBox_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void trackPollInterval_Scroll(object sender, EventArgs e)
        {

        }

        private void lblPollIntervalUnit_Click(object sender, EventArgs e)
        {

        }

        private void lblPollIntervalValue_Click(object sender, EventArgs e)
        {

        }

        private void lblInfo_TextChanged(object? sender, EventArgs e)
        {
            AdjustStatusLabelProperties(); // 文本改变时重新调整标签大小
        }

        // 更新本地化文本
        private void UpdateLocalizedTexts()
        {
            // 更新窗口标题
            this.Text = Localization.GetText("WindowTitle");
            
            // 更新标签文本
            lblPixelDelta.Text = Localization.GetText("PixelColorDiff");
            lblPollInterval.Text = Localization.GetText("DetectInterval");
            lblToggleHotkey.Text = Localization.GetText("ToggleHotkey");
            lblPollIntervalUnit.Text = Localization.GetText("Milliseconds");
            btnHelpPixelDelta.Text = Localization.GetText("QuestionMark");
            lblDisplay.Text = Localization.GetText("DisplaySelection");
            
            // 更新按钮文本
            btnStart.Text = Localization.GetText("Start");
            btnStop.Text = Localization.GetText("Stop");
            
            // 更新状态标签
            switch (lblInfo.Text)
            {
                case "Status: Stopped":
                    lblInfo.Text = Localization.GetText("StatusStopped");
                    break;
                case "Status: Running":
                    lblInfo.Text = Localization.GetText("StatusRunning");
                    break;
                case "Status: Initializing GPU capture...":
                    lblInfo.Text = Localization.GetText("StatusInitializing");
                    break;
                case "Status: Failed":
                    lblInfo.Text = Localization.GetText("StatusFailed");
                    break;
            }
            
            // 调整状态标签属性以支持换行
            AdjustStatusLabelProperties();
        }
        
        // 确保状态标签能够正确换行显示
        private void AdjustStatusLabelProperties()
        {
            lblInfo.AutoSize = false;
            lblInfo.MaximumSize = new Size(panelBottom.Width - 10, 0); // 留一些边距
            // 测量文本所需的高度
            var textSize = TextRenderer.MeasureText(lblInfo.Text, lblInfo.Font, 
                new Size(lblInfo.MaximumSize.Width, int.MaxValue), TextFormatFlags.WordBreak);
            lblInfo.Height = textSize.Height;
            
            // 调整listBox的位置，使其始终在lblInfo下方
            listBox.Location = new Point(listBox.Location.X, lblInfo.Height + 5);
            listBox.Height = panelBottom.ClientSize.Height - listBox.Location.Y - 5;
            
            // 强制面板重新布局，使listBox跟随lblInfo的高度变化
            panelBottom.PerformLayout();
        }
    }
}