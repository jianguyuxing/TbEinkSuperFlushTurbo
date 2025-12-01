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
        private const int TILE_SIZE = 8;
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
        private const int BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD = 1518; // 区块变化数阈值（单个合围区域内每帧）

        private int PollTimerInterval => _pollInterval; // Use configurable poll interval
        private static uint ProtectionFrames => (uint)Math.Ceiling((double)OVERLAY_DISPLAY_TIME / 500) + ADDITIONAL_COOLDOWN_FRAMES; // Use default 500ms for protection calculation

        private const double RESET_THRESHOLD_PERCENT = 95;
        private NotifyIcon? _trayIcon;
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
        // private const int HOTKEY_ID = 9000;
        // private const int MOD_NONE = 0;
        // private const int VK_F6 = 0x75;

        // [DllImport("user32.dll")]
        // private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        // [DllImport("user32.dll")]
        // private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
        
        private System.Windows.Forms.Timer? _displayChangeTimer;

        public MainForm()
        {
            try
            {
                LoadConfig();
                InitLogFile();
                InitUI();
                
                try
                {
                    TestBrightness.TestBrightnessCalculation();
                }
                catch (Exception ex)
                {
                    Log($"Brightness test failed: {ex.Message}");
                }
                
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                
                _displayChangeTimer = new System.Windows.Forms.Timer();
                _displayChangeTimer.Interval = 2000; // Check every 2 seconds
                _displayChangeTimer.Tick += OnDisplayChangeTimerTick;
                _displayChangeTimer.Start();
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
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
                string[] lines = { _pixelDelta.ToString(), _pollInterval.ToString() };
                File.WriteAllLines(configPath, lines);
                Log($"Saved config: PIXEL_DELTA={_pixelDelta}, POLL_INTERVAL={_pollInterval}ms");
            }
            catch (Exception ex)
            {
                Log($"Failed to save config: {ex.Message}");
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Log("Display settings changed, stopping capture.");
            StopCapture();
        }

        private void OnDisplayChangeTimerTick(object? sender, EventArgs e)
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

        private void StopCapture()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(StopCapture));
                return;
            }

            if (_pollTimer?.Enabled == true)
            {
                // Simulate stop button click
                var btnStop = Controls.OfType<Button>().FirstOrDefault(b => b.Text == "Stop");
                btnStop?.PerformClick();
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED = 0x02E0;

            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            {
                Log("Display settings changed (WM_DISPLAYCHANGE or WM_DPICHANGED), stopping capture.");
                StopCapture();
            }

            base.WndProc(ref m);
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
                MessageBox.Show($"Failed to create log file: {ex.Message}", "Logging Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void Log(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
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

        private void InitUI()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            
            _overlayForm = null;

            Text = "EInk Kaleido Ghost Reducer (GPU)";
            Width = 1800; // 进一步扩大窗口宽度
            Height = 1100;  // 进一步扩大窗口高度
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;

            int buttonWidth = 180;  // 增加按钮宽度适应高DPI
            int buttonHeight = 60;  // 增加按钮高度适应高DPI
            int labelWidth = 550;   // 大幅增加标签宽度，确保长文本完整显示
            int sliderWidth = 700;  // 大幅增加滑动条宽度适应高DPI
            int valueWidth = 150;   // 增加数值显示宽度适应高DPI
            
            var btnStart = new Button() { Text = "Start", Left = 30, Top = 30, Width = buttonWidth, Height = buttonHeight, Font = new Font(this.Font.FontFamily, 12f, FontStyle.Bold) };
            var btnStop = new Button() { Text = "Stop", Left = 220, Top = 30, Width = buttonWidth, Height = buttonHeight, Font = new Font(this.Font.FontFamily, 12f, FontStyle.Bold), Enabled = false };
            
            // 设置项放在单独一行 - Per-Pixel Brightness Threshold (增加垂直间距)
            var lblPixelDelta = new Label() { Text = "Per-Pixel Brightness Threshold:", Left = 30, Top = 120, Width = labelWidth, Height = buttonHeight, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(this.Font.FontFamily, 12f) };
            var trackPixelDelta = new TrackBar() { Left = 620, Top = 120, Width = sliderWidth, Height = 56, Minimum = 2, Maximum = 25, Value = _pixelDelta, TickFrequency = 1, SmallChange = 1, LargeChange = 5 };
            var lblPixelDeltaValue = new Label() { Text = _pixelDelta.ToString(), Left = 1230, Top = 120, Width = valueWidth, Height = buttonHeight, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(this.Font.FontFamily, 12f) };
            
            // 设置项放在单独一行 - Detection Interval (增加垂直间距和顶部空间)
            var lblPollInterval = new Label() { Text = "Detection Interval (ms):", Left = 30, Top = 220, Width = labelWidth, Height = 80, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(this.Font.FontFamily, 12f) };
            var trackPollInterval = new TrackBar() { Left = 520, Top = 237, Width = sliderWidth, Height = 56, Minimum = 200, Maximum = 5000, Value = _pollInterval, TickFrequency = 500, SmallChange = 50, LargeChange = 500 };
            var lblPollIntervalValue = new Label() { Text = _pollInterval.ToString(), Left = 1230, Top = 230, Width = valueWidth, Height = 60, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(this.Font.FontFamily, 12f) };
            var lblPollIntervalUnit = new Label() { Text = "ms", Left = 1390, Top = 230, Width = 80, Height = 60, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(this.Font.FontFamily, 12f) };
            
            // 问号按钮 - 仅悬停提示 (增大高度和宽度保持圆形)
            var btnHelp = new Button() { Text = "?", Left = 1500, Top = 220, Width = 80, Height = 80, Font = new Font("Segoe UI", 18f, FontStyle.Bold), BackColor = Color.LightBlue, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 } };
            btnHelp.TextAlign = ContentAlignment.MiddleCenter;
            // 设置圆形区域（宽高相同保持正圆）
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, 80, 80);
            btnHelp.Region = new Region(path);
            
            var lblInfo = new Label() { Left = 30, Top = 320, Width = 1600, Height = 60, Text = "Status: stopped", Font = new Font(this.Font.FontFamily, 12f) };
            var listBox = new ListBox() { Left = 30, Top = 400, Width = 1600, Height = 500 };

            this.Font = new Font(this.Font.FontFamily, 9f);

            Controls.Add(btnStart);
            Controls.Add(btnStop);
            Controls.Add(lblInfo);
            Controls.Add(listBox);
            Controls.Add(lblPixelDelta);
            Controls.Add(trackPixelDelta);
            Controls.Add(lblPixelDeltaValue);
            Controls.Add(lblPollInterval);
            Controls.Add(trackPollInterval);
            Controls.Add(lblPollIntervalValue);
            Controls.Add(lblPollIntervalUnit);
            Controls.Add(btnHelp);

            // 添加鼠标悬停提示 - 多行详细说明
            var toolTip = new ToolTip();
            toolTip.SetToolTip(lblPixelDelta, "Per-Pixel Brightness Threshold:\n\nControls how sensitive the detection is to pixel brightness changes.\n• Lower values (2-8): Better for light themes, detects subtle changes\n• Higher values (15-25): Better for high-contrast themes, ignores minor variations\n\nRecommended: Start with 10 and adjust based on your theme.");
            toolTip.SetToolTip(trackPixelDelta, "Per-Pixel Brightness Threshold:\n\nControls how sensitive the detection is to pixel brightness changes.\n• Lower values (2-8): Better for light themes, detects subtle changes\n• Higher values (15-25): Better for high-contrast themes, ignores minor variations\n\nRecommended: Start with 10 and adjust based on your theme.");
            toolTip.SetToolTip(lblPollInterval, "Detection Interval (ms):\n\nSets how often the screen is checked for changes.\n• Lower values (200-500ms): More responsive but higher CPU usage\n• Higher values (1000-5000ms): Less CPU usage but slower response\n\nRecommended: 500ms for balanced performance.");
            toolTip.SetToolTip(trackPollInterval, "Detection Interval (ms):\n\nSets how often the screen is checked for changes.\n• Lower values (200-500ms): More responsive but higher CPU usage\n• Higher values (1000-5000ms): Less CPU usage but slower response\n\nRecommended: 500ms for balanced performance.");
            
            // 优化问号按钮提示 - 支持换行和透明效果
            var helpToolTip = new ToolTip();
            helpToolTip.ToolTipTitle = "Settings Help";
            helpToolTip.UseFading = true;
            helpToolTip.UseAnimation = true;
            helpToolTip.IsBalloon = false;
            helpToolTip.BackColor = Color.FromArgb(240, 240, 240);
            helpToolTip.ForeColor = Color.Black;
            helpToolTip.AutoPopDelay = 15000; // 15秒显示时间
            helpToolTip.InitialDelay = 500;   // 0.5秒延迟
            helpToolTip.ReshowDelay = 100;     // 0.1秒重新显示延迟
            helpToolTip.SetToolTip(btnHelp, "Per-Pixel Brightness Threshold:\nControls the sensitivity of pixel change detection. Higher values require larger pixel differences to trigger updates.\n\nDetection Interval:\nSets the time interval (in milliseconds) between screen change checks. Lower values provide more responsive updates but use more CPU.");

            // 滑动条事件处理
            trackPixelDelta.ValueChanged += (s, e) => {
                _pixelDelta = trackPixelDelta.Value;
                lblPixelDeltaValue.Text = _pixelDelta.ToString();
                SaveConfig();
            };

            trackPollInterval.ValueChanged += (s, e) => {
                _pollInterval = trackPollInterval.Value;
                lblPollIntervalValue.Text = _pollInterval.ToString();
                SaveConfig();
                // 更新定时器间隔
                if (_pollTimer != null)
                {
                    _pollTimer.Interval = _pollInterval;
                }
            };

            // 问号按钮不再有点击事件，仅用于悬停提示

            _trayIcon = new NotifyIcon() { Icon = SystemIcons.Application, Text = "EInk Ghost Reducer", Visible = true };
            _trayIcon.Click += (s, e) => { if (this.WindowState == FormWindowState.Minimized || !this.Visible) { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); } else { this.Hide(); } };
            _trayIcon.DoubleClick += (s, e) => ManualRefresh();
            
            var exitMenu = new ToolStripMenuItem("Exit");
            exitMenu.Click += (s, e) => this.Close();
            var menu = new ContextMenuStrip();
            menu.Items.Add(exitMenu);
            _trayIcon.ContextMenuStrip = menu;

            btnStart.Click += (s, e) =>
            {
                btnStart.Enabled = false;
                _cts = new CancellationTokenSource();
                _frameCounter = 0; // Reset frame counter on start

                // 禁用设置项修改
                trackPixelDelta.Enabled = false;
                trackPollInterval.Enabled = false;

                lblInfo.Text = "Status: initializing GPU capture...";
                Log("Initializing GPU capture...");
                try
                {
                    _d3d = new D3DCaptureAndCompute(DebugLogger, TILE_SIZE, _pixelDelta, AVERAGE_WINDOW_SIZE, STABLE_FRAMES_REQUIRED, ADDITIONAL_COOLDOWN_FRAMES, FIRST_REFRESH_EXTRA_DELAY, CARET_CHECK_INTERVAL, IME_CHECK_INTERVAL, MOUSE_EXCLUSION_RADIUS_FACTOR,
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
                                    listBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} tiles: {tilesToRefresh.Count}");
                                    if (listBox.Items.Count > 200) listBox.Items.RemoveAt(listBox.Items.Count - 1);
                                }));
                                ShowTemporaryOverlay(tilesToRefresh, brightnessData);
                            }
                        }
                    };
                    _pollTimer.Start();

                    lblInfo.Text = $"Status: running (screen {_d3d.ScreenWidth}x{_d3d.ScreenHeight})";
                    btnStop.Enabled = true;
                    Log($"GPU capture initialized successfully. Screen: {_d3d.ScreenWidth}x{_d3d.ScreenHeight}");
                }
                catch (Exception ex)
                {
                    string errorMessage = $"Initialization failed: {ex.Message}";
                    Log(errorMessage + "\n" + ex.StackTrace);
                    MessageBox.Show(errorMessage);
                    btnStart.Enabled = true;
                    lblInfo.Text = "Status: failed";
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = null;
                }
            };

            btnStop.Click += (s, e) =>
            {
                Log("Stopping GPU capture...");
                _cts?.Cancel();
                _pollTimer?.Stop();
                _pollTimer?.Dispose();
                _pollTimer = null;
                _d3d?.Dispose();
                _d3d = null;

                lblInfo.Text = "Status: stopped";
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                
                // 重新启用设置项修改
                trackPixelDelta.Enabled = true;
                trackPollInterval.Enabled = true;
                
                _cts?.Dispose();
                _cts = null;
                
                _overlayForm?.HideOverlay();
                Log("GPU capture stopped");
            };
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

        // protected override void WndProc(ref Message m)
        // {
        //     if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
        //     {
        //         ManualRefresh();
        //         return;
        //     }
        //     base.WndProc(ref m);
        // }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // RegisterHotKey(this.Handle, HOTKEY_ID, MOD_NONE, VK_F6);
            _trayIcon?.ShowBalloonTip(3000, "EInk Ghost Reducer", "Application started. Click tray icon to show panel.", ToolTipIcon.Info);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log("Application closing...");
            _cts?.Cancel();
            Thread.Sleep(100);
            base.OnFormClosing(e);
            _d3d?.Dispose();
            _overlayForm?.Dispose();
            _trayIcon?.Dispose();
            // UnregisterHotKey(this.Handle, HOTKEY_ID);
            _logWriter?.Close();
            _cts?.Dispose();
            Log("Application closed");
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displayChangeTimer?.Stop();
            _displayChangeTimer?.Dispose();
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
