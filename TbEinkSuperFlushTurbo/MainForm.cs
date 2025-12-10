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
        private Keys _toggleHotkey = Keys.F6; // 默认快捷键
        private bool _isRecordingHotkey = false;
        private bool _isHotkeyRegistered = false;

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

                // 初始化托盘图标菜单
                _trayIcon.ContextMenuStrip = contextMenuStrip1;
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainForm constructor ERROR: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_output.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] StackTrace: {ex.StackTrace}{Environment.NewLine}");
                throw;
            }
        }

        private void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath);
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
                }

                // 加载快捷键配置
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
                    _toggleHotkey = Keys.F6;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}");
                _toggleHotkey = Keys.F6;
            }
        }

        private void SaveConfig()
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
                string[] lines = { _pixelDelta.ToString(), _pollInterval.ToString(), _tileSize.ToString() };
                File.WriteAllLines(configPath, lines);
                Log($"Saved config: PIXEL_DELTA={_pixelDelta}, POLL_INTERVAL={_pollInterval}ms, TILE_SIZE={_tileSize}");

                // 保存快捷键配置
                string hotkeyConfigPath = Path.Combine(AppContext.BaseDirectory, "hotkey.json");
                var hotkeyConfig = new
                {
                    ToggleHotkey = (int)_toggleHotkey
                };
                string json = JsonSerializer.Serialize(hotkeyConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(hotkeyConfigPath, json);
                Log($"Saved hotkey config: {FormatShortcut(_toggleHotkey)}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save config: {ex.Message}");
            }
        }

        private void InitLogFile()
        {
            try
            {
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
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
                    ToggleCaptureState();
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

                _overlayForm = new OverlayForm(_d3d.TileSize, _d3d.ScreenWidth, _d3d.ScreenHeight, NOISE_DENSITY, NOISE_POINT_INTERVAL, overlayBaseColor, borderColor, OVERLAY_BORDER_WIDTH, Log)
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    TopMost = true,
                    Size = new Size(_d3d.ScreenWidth, _d3d.ScreenHeight)
                };
                _overlayForm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                _overlayForm.Location = new Point(0, 0);

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

        private void RegisterToggleHotkey()
        {
            try
            {
                uint modifiers = GetModifiers(_toggleHotkey);
                uint virtualKey = (uint)GetKeyCode(_toggleHotkey);

                if (NativeMethods.RegisterHotKey(this.Handle, TOGGLE_HOTKEY_ID, modifiers, virtualKey))
                {
                    _isHotkeyRegistered = true;
                    Log($"Hotkey registered: {FormatShortcut(_toggleHotkey)}");
                }
                else
                {
                    Log($"Failed to register hotkey: {FormatShortcut(_toggleHotkey)}");
                    _isHotkeyRegistered = false;
                }
            }
            catch (Exception ex)
            {
                Log($"Error registering hotkey: {ex.Message}");
                _isHotkeyRegistered = false;
            }
        }

        private void UnregisterToggleHotkey()
        {
            if (_isHotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(this.Handle, TOGGLE_HOTKEY_ID);
                _isHotkeyRegistered = false;
                Log("Hotkey unregistered");
            }
        }

        private uint GetModifiers(Keys keys)
        {
            uint modifiers = 0;
            if ((keys & Keys.Control) == Keys.Control) modifiers |= 0x0002; // MOD_CONTROL
            if ((keys & Keys.Alt) == Keys.Alt) modifiers |= 0x0001; // MOD_ALT
            if ((keys & Keys.Shift) == Keys.Shift) modifiers |= 0x0004; // MOD_SHIFT
            return modifiers;
        }

        private Keys GetKeyCode(Keys keys)
        {
            return keys & Keys.KeyCode;
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
            if (_pollTimer?.Enabled == true)
            {
                btnStop?.PerformClick();
            }
            else
            {
                btnStart?.PerformClick();
            }
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
            txtToggleHotkey.Text = FormatShortcut(_toggleHotkey);

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
                MessageBox.Show("Cannot start capture while recording hotkey. Please complete hotkey recording first.", "Hotkey Recording in Progress", MessageBoxButtons.OK, MessageBoxIcon.None);
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
                        BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD), _forceDirectXCapture, ProtectionFrames);

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

            e.Graphics.Clear(btn.BackColor);

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
                    MessageBox.Show("Cannot modify hotkey while capture is running. Please stop capture first.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.None);
                    return;
                }

                // 开始录制
                _isRecordingHotkey = true;
                btnToggleRecord.Text = "■";
                btnToggleRecord.ForeColor = Color.Black;
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
            using (var brush = new SolidBrush(btn.ForeColor))
            {
                if (text == "●") // 圆点 - 改为圆环+内部小圆点
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
                else if (text == "■") // 方点
                {
                    // 绘制一个较小的实心方形，基于中心点
                    int size = Math.Min(btn.Width, btn.Height) - 42; // 再小一点
                    int x = (btn.Width - size) / 2;
                    int y = (btn.Height - size) / 2;
                    e.Graphics.FillRectangle(brush, x, y, size, size);
                }
                else // 其他字符，使用文本绘制
                {
                    var textSize = e.Graphics.MeasureString(text, font);
                    var x = (btn.Width - textSize.Width) / 2;
                    var y = (btn.Height - textSize.Height) / 2;
                    e.Graphics.DrawString(text, font, brush, x, y);
                }
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

        private void _trayIcon_Click(object? sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void _trayIcon_DoubleClick(object? sender, EventArgs e)
        {
            ManualRefresh();
        }

        private void exitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

        private void UpdateLocalizedTexts()
        {
            this.Text = Localization.GetText("WindowTitle");
            btnStart.Text = Localization.GetText("Start");
            btnStop.Text = Localization.GetText("Stop");
            lblPixelDelta.Text = Localization.GetText("PixelColorDiff");
            lblPollInterval.Text = Localization.GetText("DetectInterval");
            lblPollIntervalUnit.Text = Localization.GetText("Milliseconds");
            lblToggleHotkey.Text = Localization.GetText("ToggleHotkey");
            btnHelpPixelDelta.Text = Localization.GetText("QuestionMark");

            // 设置帮助按钮为圆形
            SetCircularButton(btnHelpPixelDelta);

            // 如果快捷键为None，确保显示"click button to set"
            if (_toggleHotkey == Keys.None)
            {
                txtToggleHotkey.Text = Localization.GetText("ClickButtonToSet");
            }
        }

        private void SetCircularButton(Button button)
        {
            // 设置圆形区域（宽高相同保持正圆）
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, button.Width, button.Height);
            button.Region = new Region(path);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterToggleHotkey();
            _overlayForm?.HideOverlay();
            _displayChangeTimer?.Stop();

            base.OnFormClosing(e);
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
    }

    class OverlayForm : Form
        {
            [DllImport("user32.dll")]
            private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll")]
            private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
            readonly int _tileSize, _screenW, _screenH, _noiseDensity, _noisePointInterval, _borderWidth;
            readonly Color _baseColor, _borderColor;

            public bool IsDisplaying => _isDisplaying;

            public void UpdateContent(List<(int bx, int by)> tiles, float[]? brightnessData = null)
            {
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

                // 更新显示（累积模式，不清除之前的显示）
                UpdateVisuals();
                if (!_isDisplaying)
                {
                    _isDisplaying = true;
                }
                if (addedNewTiles)
                {
                    Logger?.Invoke($"DEBUG: 刷新色显示开始，将显示{MainForm.OVERLAY_DISPLAY_TIME}ms，当前瓦片数: {_tiles.Count}");
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

                // 从位图上擦除过期的瓦片
                ClearExpiredTilesFromBitmap();

                // 从取消令牌列表中移除这个令牌
                _batchCancellationTokenSources.Remove(cts);
                cts.Dispose();

                // 更新显示以清除过期的瓦片
                UpdateVisuals();

                Logger?.Invoke($"DEBUG: 部分刷新色过期，本次过期瓦片数: {expiredCount}，剩余瓦片数: {_tiles.Count}，过期瓦片数: {_expiredTiles.Count}");
                // 清理临时过期瓦片列表，为下一轮刷新做准备
                _expiredTiles.Clear();
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
                UpdateVisuals();
                Logger?.Invoke("DEBUG: 刷新色强制隐藏");
            }

            // 从位图上清除过期的瓦片
            private void ClearExpiredTilesFromBitmap()
            {
                lock (_bitmapLock)
                {
                    if (_overlayBitmap == null) return;

                    using (Graphics g = Graphics.FromImage(_overlayBitmap))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

                        foreach (var tile in _expiredTiles)
                        {
                            int sx = tile.bx * _tileSize;
                            int sy = tile.by * _tileSize;
                            int w = Math.Min(_tileSize, _screenW - sx);
                            int h = Math.Min(_tileSize, _screenH - sy);

                            // 使用透明色清除过期的瓦片区域
                            using (var brush = new SolidBrush(Color.Transparent))
                                g.FillRectangle(brush, sx, sy, w, h);
                        }
                    }
                }
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

            public void UpdateVisuals()
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
                        // 不清空位图，实现累积显示效果，避免闪烁 （目前实测这个是否注释影响不大）
                        // using (Graphics g = Graphics.FromImage(_overlayBitmap))
                        // {
                        //     // 使用透明色清空位图
                        //     g.Clear(Color.Transparent);
                        // }
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
                                overlayColor = Color.FromArgb(85, 0, 0, 0); // 半透明黑色
                            }
                            else
                            {
                                overlayColor = Color.FromArgb(85, 255, 255, 255); // 半透明白色
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

                    // 注意：不要在这里清理_expiredTiles列表，因为在MarkTilesAsExpiredInternal方法中还需要使用它
                    // _expiredTiles.Clear();
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

            [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Win32Point pptDst, ref Win32Size psize, IntPtr hdcSrc, ref Win32Point pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
            [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

            [StructLayout(LayoutKind.Sequential)] private struct Win32Point { public int X, Y; public Win32Point(int x, int y) { X = x; Y = y; } }
            [StructLayout(LayoutKind.Sequential)] private struct Win32Size { public int cx, cy; public Win32Size(int cx, int cy) { this.cx = cx; this.cy = cy; } }
            [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        }
}