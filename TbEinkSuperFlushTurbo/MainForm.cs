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
        private CancellationTokenSource? _cts;
        private OverlayForm? _overlayForm;

        public Action<string>? DebugLogger { get; private set; }

        // --- Refresh parameters ---
        private const int TILE_SIZE = 8;
        private const int PIXEL_DELTA = 15;
        private const uint AVERAGE_WINDOW_SIZE = 4;
        private const uint STABLE_FRAMES_REQUIRED = 3;
        private const uint ADDITIONAL_COOLDOWN_FRAMES = 2;
        private const uint FIRST_REFRESH_EXTRA_DELAY = 2;

        public const int OVERLAY_DISPLAY_TIME = 100; // ms
        private const int POLL_TIMER_INTERVAL = 100; // ms, now matches overlay time for simplicity

        private static uint ProtectionFrames => (uint)Math.Ceiling((double)OVERLAY_DISPLAY_TIME / POLL_TIMER_INTERVAL) + ADDITIONAL_COOLDOWN_FRAMES;

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
        private const int HOTKEY_ID = 9000;
        private const int MOD_NONE = 0;
        private const int VK_F6 = 0x75;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
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
        
        public MainForm()
        {
            try
            {
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
            Width = 1200; 
            Height = 800;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int buttonWidth = 120;
            int buttonHeight = 50;
            var btnStart = new Button() { Text = "Start", Left = 20, Top = 20, Width = buttonWidth, Height = buttonHeight, Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold) };
            var btnStop = new Button() { Text = "Stop", Left = 160, Top = 20, Width = buttonWidth, Height = buttonHeight, Font = new Font(this.Font.FontFamily, 10f, FontStyle.Bold), Enabled = false };
            
            var lblInfo = new Label() { Left = 30, Top = 80, Width = 1140, Height = 40, Text = "Status: stopped" };
            var listBox = new ListBox() { Left = 30, Top = 130, Width = 1100, Height = 550 };

            this.Font = new Font(this.Font.FontFamily, 9f);

            Controls.Add(btnStart);
            Controls.Add(btnStop);
            Controls.Add(lblInfo);
            Controls.Add(listBox);

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

                lblInfo.Text = "Status: initializing GPU capture...";
                Log("Initializing GPU capture...");
                try
                {
                    _d3d = new D3DCaptureAndCompute(DebugLogger, TILE_SIZE, PIXEL_DELTA, AVERAGE_WINDOW_SIZE, STABLE_FRAMES_REQUIRED, ADDITIONAL_COOLDOWN_FRAMES, FIRST_REFRESH_EXTRA_DELAY, CARET_CHECK_INTERVAL, IME_CHECK_INTERVAL, MOUSE_EXCLUSION_RADIUS_FACTOR, _forceDirectXCapture)
                    {
                        ProtectionFrames = ProtectionFrames
                    };

                    _pollTimer = new System.Windows.Forms.Timer
                    {
                        Interval = POLL_TIMER_INTERVAL
                    };
                    _pollTimer.Tick += async (ss, ee) =>
                    {
                        if (_cts.Token.IsCancellationRequested || _d3d == null) return;

                        var (tilesToRefresh, brightnessData) = await _d3d.CaptureAndComputeOnceAsync(_cts.Token);
                        if (_cts.Token.IsCancellationRequested) return;

                        if (tilesToRefresh.Count > 0)
                        {
                            double refreshRatio = (double)tilesToRefresh.Count / (_d3d.TilesX * _d3d.TilesY);
                            if (refreshRatio >= RESET_THRESHOLD_PERCENT / 100.0)
                            {
                                Log($"System-wide refresh detected ({refreshRatio:P1}), skipping overlay.");
                                // The GPU state will automatically handle this, no need for CPU-side reset.
                            }
                            else
                            {
                                Log($"Tiles to refresh: {tilesToRefresh.Count}");
                                this.Invoke(new Action(() =>
                                {
                                    listBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} tiles: {tilesToRefresh.Count}");
                                    if (listBox.Items.Count > 200) listBox.Items.RemoveAt(listBox.Items.Count - 1);
                                }));
                                await ShowTemporaryOverlayAsync(tilesToRefresh, brightnessData, _cts.Token);
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
                _cts?.Dispose();
                _cts = null;
                
                _overlayForm?.HideOverlay();
                Log("GPU capture stopped");
            };
        }

        async Task ShowTemporaryOverlayAsync(List<(int bx, int by)>? tiles, float[]? brightnessData, CancellationToken token)
        {
            if (token.IsCancellationRequested || _d3d == null || tiles == null || tiles.Count == 0) return;

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
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(0, 0),
                    Size = new Size(_d3d.ScreenWidth, _d3d.ScreenHeight)
                };
                
                await Task.Run(() => this.Invoke(new Action(() => _overlayForm.Show())), token);
            }
            
            await Task.Run(() => _overlayForm.UpdateContent(tiles, brightnessData), token);
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
            base.OnLoad(e);
            RegisterHotKey(this.Handle, HOTKEY_ID, MOD_NONE, VK_F6);
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
            UnregisterHotKey(this.Handle, HOTKEY_ID);
            _logWriter?.Close();
            _cts?.Dispose();
            Log("Application closed");
        }

        public async void ManualRefresh()
        {
            if (_d3d == null) return;
            Log("[Manual] F6 triggered refresh");
            try
            {
                var (tiles, bright) = await _d3d.CaptureAndComputeOnceAsync(CancellationToken.None);
                await ShowTemporaryOverlayAsync(tiles, bright, CancellationToken.None);
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
        readonly Dictionary<(int bx, int by), float> _tiles = new Dictionary<(int bx, int by), float>();
        readonly List<CancellationTokenSource> _batchCts = new List<CancellationTokenSource>();
        Bitmap? _overlayBitmap;
        private readonly object _bitmapLock = new object();
        private bool _isDisplaying = false;
        readonly int _tileSize, _screenW, _screenH, _noiseDensity, _noisePointInterval, _borderWidth;
        readonly Color _baseColor, _borderColor;
        
        public void UpdateContent(List<(int bx, int by)> tiles, float[]? brightnessData = null)
        {
            bool addedNewTiles = false;
            int tilesX = (_screenW + _tileSize - 1) / _tileSize;

            foreach (var tile in tiles)
            {
                if (!_tiles.ContainsKey(tile))
                {
                    int tileIdx = tile.by * tilesX + tile.bx;
                    float brightness = (brightnessData != null && tileIdx < brightnessData.Length) ? brightnessData[tileIdx] : 0.5f;
                    _tiles[tile] = brightness;
                    addedNewTiles = true;
                }
            }
            
            if (tiles.Count > 0)
            {
                var cts = new CancellationTokenSource();
                _batchCts.Add(cts);
                
                Task.Run(async () => {
                    try 
                    {
                        await Task.Delay(MainForm.OVERLAY_DISPLAY_TIME, cts.Token);
                        MarkTilesAsExpired(tiles, cts);
                    }
                    catch (OperationCanceledException) { }
                });
            }
            
            if (!_isDisplaying || addedNewTiles)
            {
                UpdateVisuals();
                _isDisplaying = true;
            }
        }
    
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
            foreach (var tile in tiles)
            {
                _tiles.Remove(tile);
            }
            
            _batchCts.Remove(cts);
            cts.Dispose();
            
            UpdateVisuals();
            
            if (_tiles.Count == 0)
            {
                lock (_bitmapLock)
                {
                    _overlayBitmap?.Dispose();
                    _overlayBitmap = null;
                }
                _isDisplaying = false;
            }
        }
    
        public void HideOverlay()
        {
            foreach (var cts in _batchCts) cts.Cancel();
            _batchCts.ForEach(cts => cts.Dispose());
            _batchCts.Clear();
            _tiles.Clear();
            
            lock (_bitmapLock)
            {
                _overlayBitmap?.Dispose();
                _overlayBitmap = null;
            }
            _isDisplaying = false;
            UpdateVisuals();
        }

        public OverlayForm(int tileSize, int screenW, int screenH, int noiseDensity, int noisePointInterval, Color baseColor, Color borderColor, int borderWidth, Action<string> log)
        {
            _tileSize = tileSize; _screenW = screenW; _screenH = screenH;
            _noiseDensity = noiseDensity; _noisePointInterval = noisePointInterval;
            _baseColor = baseColor; _borderColor = borderColor; _borderWidth = borderWidth;
            Logger = log;
            
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Location = new Point(0, 0);
            Size = new Size(screenW, screenH);
            
            int exStyle = GetWindowLong(this.Handle, -20);
            SetWindowLong(this.Handle, -20, exStyle | 0x00080000);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020 | 0x00000080 | 0x00080000;
                return cp;
            }
        }

        public void UpdateVisuals()
        {
            if (!this.Visible) this.Show();
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, _screenW, _screenH, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            DrawOverlayBitmap();
            UpdateLayeredWindowFromBitmap();
        }
    
        private void DrawOverlayBitmap()
        {
            lock (_bitmapLock)
            {
                if (_overlayBitmap == null || _overlayBitmap.Width != _screenW || _overlayBitmap.Height != _screenH)
                {
                    _overlayBitmap?.Dispose();
                    _overlayBitmap = new Bitmap(_screenW, _screenH, PixelFormat.Format32bppArgb);
                }
                else
                {
                    using (Graphics g = Graphics.FromImage(_overlayBitmap)) g.Clear(Color.Transparent);
                }
                
                using (Graphics g = Graphics.FromImage(_overlayBitmap))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    
                    foreach (var tileEntry in _tiles)
                    {
                        var tile = tileEntry.Key;
                        float brightness = tileEntry.Value;
                        int sx = tile.bx * _tileSize, sy = tile.by * _tileSize;
                        int w = Math.Min(_tileSize, _screenW - sx), h = Math.Min(_tileSize, _screenH - sy);
                        
                        Color overlayColor = brightness > 0.5f ? Color.FromArgb(100, 0, 0, 0) : Color.FromArgb(100, 255, 255, 255);
                        using (var br = new SolidBrush(overlayColor)) g.FillRectangle(br, sx, sy, w, h);
                        
                        if (_borderWidth >= 1)
                        {
                            Color bColor = brightness > 0.5f ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(180, 0, 0, 0);
                            using (var pen = new Pen(bColor, _borderWidth)) g.DrawRectangle(pen, sx, sy, w - 1, h - 1);
                        }
                    }
                }
            }
        }
    
        private void UpdateLayeredWindowFromBitmap()
        {
            lock (_bitmapLock)
            {
                if (_overlayBitmap == null) return;
                
                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = _overlayBitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr hOld = SelectObject(hdcMem, hBitmap);

                Win32Point ptSrc = new Win32Point(0, 0);
                Win32Size sz = new Win32Size(_screenW, _screenH);
                Win32Point ptDest = new Win32Point(0, 0);
                
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

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