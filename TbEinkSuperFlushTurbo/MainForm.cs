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
        private bool _forceDirectXCapture = false;

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
                    ToggleHotkey = (int)_toggleHotkey
                };
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configJsonPath, json);
                Log($"Saved config: PIXEL_DELTA={_pixelDelta}, POLL_INTERVAL={_pollInterval}ms, TILE_SIZE={_tileSize}, SCREEN_INDEX={_targetScreenIndex}, HOTKEY={(int)_toggleHotkey}");
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
                System.Diagnostics.Debug.WriteLine(logEntry);
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
            Log("Display settings changed, stopping capture.");
            StopCapture();
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
                Log("Display settings changed (WM_DISPLAYCHANGE or WM_DPICHANGED), stopping capture.");
                StopCapture();
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
            try
            {
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

                // 方法3: 使用Graphics对象检测
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float dpiX = graphics.DpiX;
                    float scale = dpiX / 96f;
                    Log($"DPI检测: Graphics DPI = {dpiX}, Scale = {scale:F2}");
                    return scale;
                }
            }
            catch (Exception ex)
            {
                Log($"DPI检测失败: {ex.Message}, 使用默认值 1.0");
                return 1.0f;
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

                _overlayForm = new OverlayForm(_d3d.TileSize, _d3d.ScreenWidth, _d3d.ScreenHeight, NOISE_DENSITY, NOISE_POINT_INTERVAL, overlayBaseColor, borderColor, OVERLAY_BORDER_WIDTH, Log, _targetScreenIndex)
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    TopMost = true,
                    Size = new Size(_d3d.ScreenWidth, _d3d.ScreenHeight)
                };
                _overlayForm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;

                _overlayForm.Show();
            }

            _overlayForm?.UpdateContent(tiles, brightnessData);
        }

        private float GetDpiScale()
        {
            return GetDpiForWindow(this.Handle) / 96f;
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

            // 执行自适应布局
            UpdateAdaptiveLayout();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                UpdateAdaptiveLayout();
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

            btnStart.Enabled = false;
            _cts = new CancellationTokenSource();
            _frameCounter = 0; // Reset frame counter on start

            // 禁用设置项修改
            trackPixelDelta.Enabled = false;
            trackPollInterval.Enabled = false;

            lblInfo.Text = "Status: Initializing GPU capture...";
            Log("Initializing GPU capture...");
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
                    if (_cts.Token.IsCancellationRequested || _d3d == null) return;

                    _frameCounter++; // Increment frame counter

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
                };
                _pollTimer.Start();

                // 获取系统缩放比例 - 使用更准确的DPI检测
                float dpiScale = GetSystemDpiScale();
                int scalePercent = (int)(dpiScale * 100);
                lblInfo.Text = $"{Localization.GetText("StatusRunning")} (Screen: {_d3d.ScreenWidth}x{_d3d.ScreenHeight}, Scale: {scalePercent}%, Tile Size: {_tileSize}x{_tileSize} pixels)";
                btnStop.Enabled = true;
                Log($"GPU capture initialized successfully. Screen: {_d3d.ScreenWidth}x{_d3d.ScreenHeight}, Scale: {scalePercent}%, Tile Size: {_tileSize}x{_tileSize} pixels");
            }
            catch (Exception ex)
            {
                string errorMessage = $"Initialization failed: {ex.Message}";
                Log(errorMessage + "\n" + ex.StackTrace);
                MessageBox.Show(errorMessage);
                btnStart.Enabled = true;
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

            _cts?.Dispose();
            _cts = null;

            _overlayForm?.HideOverlay();
            Log("GPU capture stopped");
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

            // 绘制黑色边框
            using (var borderPen = new Pen(Color.Black, 1))
            {
                e.Graphics.DrawEllipse(borderPen, 0, 0, btn.Width - 1, btn.Height - 1);
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

            // 绘制黑色边框
            using (var borderPen = new Pen(Color.Black, 2))
            {
                e.Graphics.DrawRectangle(borderPen, 1, 1, btn.Width - 2, btn.Height - 2);
            }

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

        private void _displayChangeTimer_Tick(object? sender, EventArgs e)
        {
            if (_d3d != null)
            {
                double refreshRate = _d3d.GetCurrentPrimaryDisplayRefreshRate();
                if (refreshRate >= 59.0)
                {
                    Log($"High refresh rate detected ({refreshRate}Hz), stopping capture.");
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
        }
    }
}