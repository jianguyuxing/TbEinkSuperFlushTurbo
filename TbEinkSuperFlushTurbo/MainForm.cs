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
using System.Text;
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
        private int _tileSize = 8; // Pixel side length of the tile, default 8 means 8*8 pixels
        private int _lastUsedTileSize = 0; // Record the last used tileSize for detecting changes
        private int _pixelDelta = 10;
        // Average window size for frame difference calculation (maximum supported: 2 frames)
        private const uint AVERAGE_WINDOW_SIZE = 2;
        private const uint STABLE_FRAMES_REQUIRED = 4;
        private const uint ADDITIONAL_COOLDOWN_FRAMES = 2;
        private const uint FIRST_REFRESH_EXTRA_DELAY = 1;

        public const int OVERLAY_DISPLAY_TIME = 100; // ms
        private int _pollInterval = 600; // ms detect period, configurable

        // Bounding area configuration for suppressing scrolling area refresh - when m frames in n frames change within a single area, tiles in the area are not refreshed
        private const int BOUNDING_AREA_WIDTH = 45;  // Width of each bounding area (in tiles)
        private const int BOUNDING_AREA_HEIGHT = 45; // Height of each bounding area (in tiles)
        private const int BOUNDING_AREA_HISTORY_FRAMES = 3; // Number of history frames
        private const int BOUNDING_AREA_CHANGE_THRESHOLD = 3; // Frame change threshold
        private const double BOUNDING_AREA_REFRESH_BLOCK_RATIO = 0.75; // Tile ratio threshold (suppress refresh when 75% of tiles change)
        private const int BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD = (int)(BOUNDING_AREA_WIDTH * BOUNDING_AREA_HEIGHT * BOUNDING_AREA_REFRESH_BLOCK_RATIO); // Tile change count threshold (calculated from ratio)

        private int PollTimerInterval => _pollInterval; // Use configurable poll interval
        private static uint ProtectionFrames => (uint)Math.Ceiling((double)OVERLAY_DISPLAY_TIME / 500) + ADDITIONAL_COOLDOWN_FRAMES; // Use default 500ms for protection calculation

        private const double RESET_THRESHOLD_PERCENT = 95;
        private bool _forceDirectXCapture;  // Force DirectX capture (read from config.json)

        public bool ForceDirectXCapture
        {
            get => _forceDirectXCapture;
            set
            {
                _forceDirectXCapture = value;
                Log($"ForceDirectXCapture set to: {_forceDirectXCapture}");
            }
        }

        // Find display index based on device name
        private int FindScreenIndexByDeviceName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return 0; // Default to primary display
                
            var allScreens = Screen.AllScreens;
            for (int i = 0; i < allScreens.Length; i++)
            {
                if (allScreens[i].DeviceName == deviceName)
                    return i;
            }
            
            Log($"Warning: Cannot find display with device name '{deviceName}', using index 0");
            return 0; // Use primary display if not found
        }

        // Get the device name of the current target display
        private string GetCurrentTargetDeviceName()
        {
            if (_targetScreenIndex >= 0 && _targetScreenIndex < Screen.AllScreens.Length)
            {
                return Screen.AllScreens[_targetScreenIndex].DeviceName;
            }
            return string.Empty;
        }

        // Detect if primary display switch has occurred
        private bool DetectPrimaryDisplayChange()
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (allScreens.Length == 0) return false;
                
                // Find the current primary display
                var currentPrimary = allScreens.FirstOrDefault(s => s.Primary);
                if (currentPrimary == null) return false;
                
                // Check if the currently selected display is still the primary display
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];
                    return targetScreen.Primary != currentPrimary.Primary || 
                           targetScreen.DeviceName != currentPrimary.DeviceName;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Log($"Failed to detect primary display switch: {ex.Message}");
                return false;
            }
        }

        // Handle display configuration changes (including primary display switch)
        private void HandleDisplayConfigurationChange()
        {
            try
            {
                Log("Starting to handle display configuration changes...");
                
                // 1. Record current target display device name (stable identifier)
                string originalDeviceName = _targetDisplayDeviceName ?? string.Empty;
                Log($"Original target display device name: '{originalDeviceName}'");
                
                // 2. Detect if it's a primary display switch
                bool isPrimaryChanged = DetectPrimaryDisplayChange();
                if (isPrimaryChanged)
                {
                    Log("Primary display switch detected");
                    _isPrimaryDisplayChanged = true;
                }
                
                // 3. Wait for system stabilization (primary display switch takes longer)
                int stabilizationDelay = isPrimaryChanged ? 2000 : 1000;
                Log($"Waiting for system stabilization: {stabilizationDelay}ms");
                Thread.Sleep(stabilizationDelay);
                
                // 4. Find the original display again (by device name)
                if (!string.IsNullOrEmpty(originalDeviceName))
                {
                    int newIndex = FindScreenIndexByDeviceName(originalDeviceName);
                    if (newIndex != _targetScreenIndex)
                    {
                        Log($"Display index remapped: {_targetScreenIndex} -> {newIndex}");
                        _targetScreenIndex = newIndex;
                    }
                }
                
                // 5. Re-acquire atomic DPI information for the target display
                var (dpiX, dpiY, success) = GetDisplayDpiAtomic(_targetScreenIndex);
                if (success)
                {
                    Log($"Successfully re-acquired DPI: {dpiX}x{dpiY}");
                    // Update DPI-related parameters
                    float scaleX = dpiX / 96.0f;
                    float scaleY = dpiY / 96.0f;
                    // D3d or other DPI-dependent components can be updated here
                }
                else
                {
                    Log("Failed to re-acquire DPI, using original parameters");
                }
                
                // 6. Update device name cache
                _targetDisplayDeviceName = GetCurrentTargetDeviceName();
                Log($"Updated target display device name: '{_targetDisplayDeviceName}'");
                
                // 7. Re-initialize display state recording
                RecordInitialDisplayState();
                
                Log("Display configuration change handling completed");
                
            }
            catch (Exception ex)
            {
                Log($"Failed to handle display configuration changes: {ex.Message}");
            }
        }
        // Hotkey related fields
        private const int TOGGLE_HOTKEY_ID = 9001;
        private Keys _toggleHotkey = Keys.None; // No hotkey by default
        private bool _isRecordingHotkey = false;
        private bool _isHotkeyRegistered = false;
        // Display selection related fields
        private int _targetScreenIndex = 0; // Use primary display by default
        private string? _targetDisplayDeviceName = null; // Device name of currently selected display, used for intelligent matching

        // Auto-stop feature for displays over 59Hz
        private int _stopOver59hz = 1; // Enabled by default (1=enabled, 0=disabled)

        // Display change monitoring related fields
        private string[]? _lastDisplaySignatures; // Store last detected display signatures
        private int _displayCheckCounter = 0; // Display detection counter
        private const int DISPLAY_CHECK_INTERVAL = 2; // Check once per second (assuming 500ms timer interval)
        private bool _isDisplayMonitoringEnabled = true; // Whether to enable display change monitoring
        private DateTime _lastDisplayChangeDetectionTime = DateTime.MinValue; // Last time display change was detected
        private const int DISPLAY_CHANGE_DEDUPLICATION_INTERVAL = 2000; // Deduplication interval: only respond once within 2 seconds
        private static bool _displayChangeMessageShown = false; // Whether display change popup is shown
        
        // Display identification related fields based on device name
        #pragma warning disable CS0414 // Suppress unused field warning
        private bool _isPrimaryDisplayChanged = false; // Flag indicating whether primary display switch occurred
        #pragma warning restore CS0414

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
        
        // Atomic DPI retrieval related APIs
        [DllImport("user32.dll")]
        private static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);
        [DllImport("user32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        
        private const int LOGPIXELSX = 88;
        private const int LOGPIXELSY = 90;
        
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        private const int MDT_EFFECTIVE_DPI = 0;
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        // Add P/Invoke declarations for CreateWindowEx and DestroyWindow
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private const int CARET_CHECK_INTERVAL = 400;
        private const int IME_CHECK_INTERVAL = 400;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int MOUSE_EXCLUSION_RADIUS_FACTOR = 2;
        private const int NOISE_DENSITY = 20;
        private const int NOISE_POINT_INTERVAL = 3;
        private const string OVERLAY_BASE_COLOR = "Black";

        // Tray icon related fields
#pragma warning disable CS0414 // Suppress unused field warning
        private bool _allowVisible = true;     // Allow form display (reserved field for future functionality expansion)
#pragma warning restore CS0414
        private bool _allowClose = false;      // Allow form closure

        // Hotkey trigger prompt related fields
#pragma warning disable CS0414 // Suppress unused field warning
        private bool _isTriggeredByHotkey = false; // Whether triggered by hotkey (reserved field for future functionality expansion)
#pragma warning restore CS0414

        public MainForm()
        {
            try
            {
                // Detect and set system language
                Localization.DetectAndSetLanguage();

                LoadConfig();
                InitLogFile();

                // Designer will automatically call InitializeComponent()
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

                // Register hotkey
                RegisterToggleHotkey();

                // Set window properties to support resizing by dragging borders
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.MaximizeBox = true;
                this.MinimizeBox = true;
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.ResizeRedraw, true);

                // Initialize tray icon
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
                MessageBox.Show($"Failed to create log file: {ex.Message}", Localization.GetText("WindowTitle"), MessageBoxButtons.OK, MessageBoxIcon.None);
            }
        }

        private void LoadConfig()
        {
            try
            {
                // First try to load the new JSON format configuration file
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
                    // Load hotkey configuration
                    if (root.TryGetProperty("ToggleHotkey", out JsonElement hotkeyElement))
                    {
                        _toggleHotkey = (Keys)hotkeyElement.GetInt32();
                    }
                    // Load auto-stop configuration for displays over 59Hz
                    if (root.TryGetProperty("stopOver59hz", out JsonElement stopOver59hzElement))
                    {
                        _stopOver59hz = Math.Max(0, Math.Min(1, stopOver59hzElement.GetInt32()));
                    }
                    // Load force DirectX capture configuration
                    if (root.TryGetProperty("ForceDirectXCapture", out JsonElement forceDirectXCaptureElement))
                    {
                        _forceDirectXCapture = forceDirectXCaptureElement.GetBoolean();
                    }
                }
                else
                {
                    // If JSON config file doesn't exist, try to load old text format config file
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
                        // Load display index configuration
                        if (lines.Length >= 4 && int.TryParse(lines[3], out int savedScreenIndex))
                        {
                            _targetScreenIndex = savedScreenIndex;
                        }
                    }

                    // Load hotkey configuration (old way)
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
                // Set default values
                _forceDirectXCapture = false;
            }
        }

        private void SaveConfig()
        {
            try
            {
                // Save as new JSON format configuration file (includes all configurations)
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

                // RELEASE mode fix: Ensure logging works in release mode as well
                // Use Trace instead of Debug because Trace is still effective in release mode
                System.Diagnostics.Trace.WriteLine(logEntry);

                // Force flush the log writer to ensure logs are written to file immediately
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
            Log("Detected display settings change event (SystemEvents.DisplaySettingsChanged)");
            AutoStopDueToDisplayChange(Localization.GetText("DisplaySettingsChange"));
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
                Log("Detected display change message (WM_DISPLAYCHANGE or WM_DPICHANGED)");
                AutoStopDueToDisplayChange(Localization.GetText("DisplayConfigurationChange"));
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == TOGGLE_HOTKEY_ID)
            {
                // Global hotkey triggered (only respond when not in recording state and no display change popup is shown)
                if (!_isRecordingHotkey && !_displayChangeMessageShown)
                {
                    _isTriggeredByHotkey = true;
                    ToggleCaptureState();
                    _isTriggeredByHotkey = false;
                }
                else if (_displayChangeMessageShown)
                {
                    Log("Ignoring hotkey trigger: Display change popup is currently showing");
                }
                return;
            }

            base.WndProc(ref m);
        }

        private float GetSystemDpiScale()
        {
            // Prioritize getting DPI settings for the specified display
            try
            {
                // Get information about all displays
                var allScreens = Screen.AllScreens;
                Log($"DPI detection: Total detected displays: {allScreens.Length}");

                // Check if target display index is valid
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];
                    Log($"DPI detection: Attempting to get DPI settings for display [{_targetScreenIndex}]");

                    // Try to create Graphics object for specific display to get its DPI
                    try
                    {
                        // Get the bounds rectangle of the display
                        var bounds = targetScreen.Bounds;
                        Log($"DPI detection: Display [{_targetScreenIndex}] bounds = ({bounds.Left}, {bounds.Top}, {bounds.Width}x{bounds.Height})");

                        // Create temporary window handle to get DPI for this display
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
                                    Log($"DPI detection: Successfully got display [{_targetScreenIndex}] DPI = {dpiX}, Scale = {scale:F2}");
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
                            Log($"DPI detection: Unable to create temporary window handle for display [{_targetScreenIndex}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"DPI detection: Exception occurred while getting display [{_targetScreenIndex}] DPI: {ex.Message}");
                        // If unable to get specific DPI, continue using other methods
                    }
                }
                else
                {
                    Log($"DPI detection: Target display index {_targetScreenIndex} out of range [0-{allScreens.Length - 1}]");
                }
            }
            catch (Exception ex)
            {
                Log($"DPI detection: Exception occurred while enumerating display information: {ex.Message}");
                // If unable to get specific display's DPI, continue using other methods
            }

            // Method 1: Use GetDpiForWindow (if window handle is valid)
            if (this.Handle != IntPtr.Zero)
            {
                uint windowDpi = GetDpiForWindow(this.Handle);
                if (windowDpi > 0)
                {
                    float scale = windowDpi / 96f;
                    Log($"DPI detection: Window DPI = {windowDpi}, Scale = {scale:F2}");
                    return scale;
                }
            }

            // Method 2: Use GetDpiForSystem
            uint systemDpi = GetDpiForSystem();
            if (systemDpi > 0)
            {
                float scale = systemDpi / 96f;
                Log($"DPI detection: System DPI = {systemDpi}, Scale = {scale:F2}");
                return scale;
            }

            // Method 3: Use Graphics object to detect primary display DPI
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                float dpiX = graphics.DpiX;
                float scale = dpiX / 96f;
                Log($"DPI detection: Primary Screen DPI = {dpiX}, Scale = {scale:F2}");
                return scale;
            }
        }

        // Atomically get DPI information for the specified display (to avoid misidentification)
        private (int dpiX, int dpiY, bool success) GetDisplayDpiAtomic(int screenIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    Log($"Atomic DPI acquisition failed: Display index {screenIndex} out of range");
                    return (96, 96, false);
                }

                var targetScreen = allScreens[screenIndex];
                string deviceName = targetScreen.DeviceName;
                
                Log($"Atomic DPI acquisition: Attempting to get DPI for display [{screenIndex}] '{deviceName}'");

                // Method 1: Use GetDpiForMonitor (recommended for Windows 8.1+)
                try
                {
                    var centerPoint = new Point(
                        targetScreen.Bounds.Left + targetScreen.Bounds.Width / 2,
                        targetScreen.Bounds.Top + targetScreen.Bounds.Height / 2
                    );
                    
                    IntPtr hMonitor = MonitorFromPoint(centerPoint, MONITOR_DEFAULTTONEAREST);
                    if (hMonitor != IntPtr.Zero)
                    {
                        uint dpiX, dpiY;
                        int result = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                        if (result == 0) // S_OK
                        {
                            Log($"Atomic DPI acquisition successful: Using GetDpiForMonitor, DPI = {dpiX}x{dpiY}");
                            return ((int)dpiX, (int)dpiY, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetDpiForMonitor failed: {ex.Message}, falling back to CreateDC method");
                }

                // Method 2: Use CreateDC to get display-specific HDC
                try
                {
                    IntPtr hdc = CreateDC(null, deviceName, null, IntPtr.Zero);
                    if (hdc != IntPtr.Zero)
                    {
                        try
                        {
                            int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                            int dpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                            Log($"Atomic DPI acquisition successful: Using CreateDC, DPI = {dpiX}x{dpiY}");
                            return (dpiX, dpiY, true);
                        }
                        finally
                        {
                            DeleteDC(hdc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"CreateDC method failed: {ex.Message}, falling back to Graphics method");
                }

                // Method 3: Fall back to Graphics method (currently used method)
                return GetDisplayDpiByGraphics(screenIndex);
            }
            catch (Exception ex)
            {
                Log($"Atomic DPI acquisition failed: {ex.Message}, using default value 96x96");
                return (96, 96, false);
            }
        }

        // Use Graphics object to get DPI (improved version of current method)
        private (int dpiX, int dpiY, bool success) GetDisplayDpiByGraphics(int screenIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                    return (96, 96, false);

                var targetScreen = allScreens[screenIndex];
                var bounds = targetScreen.Bounds;

                // Create temporary window handle to get DPI for this display
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
                            Log($"Graphics method DPI acquisition: {dpiX}x{dpiY}");
                            return ((int)dpiX, (int)dpiY, true);
                        }
                    }
                    finally
                    {
                        NativeMethods.DestroyWindow(tempHwnd);
                    }
                }
                
                return (96, 96, false);
            }
            catch (Exception ex)
            {
                Log($"Graphics method DPI acquisition failed: {ex.Message}");
                return (96, 96, false);
            }
        }

        // Get physical and logical resolutions of the specified display
        private (int physicalWidth, int physicalHeight, int logicalWidth, int logicalHeight) GetScreenResolutions(int screenIndex)
        {
            try
            {
                // Get target display
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    screenIndex = 0; // Use primary display by default
                }

                var targetScreen = allScreens[screenIndex];

                // Logical resolution: Screen.Bounds returns logical resolution (e.g. 2560x1440)
                int logicalWidth = targetScreen.Bounds.Width;
                int logicalHeight = targetScreen.Bounds.Height;

                // Physical resolution: Use EnumDisplaySettings to get real hardware resolution
                int physicalWidth = logicalWidth;
                int physicalHeight = logicalHeight;

                // Get device name
                string deviceName = targetScreen.DeviceName;

                // Try to get real physical resolution using EnumDisplaySettings
                NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));

                if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                {
                    physicalWidth = devMode.dmPelsWidth;
                    physicalHeight = devMode.dmPelsHeight;
                    // Simplify logging: only print detailed info if there's a difference
                    if (physicalWidth != logicalWidth || physicalHeight != logicalHeight)
                    {
                        Log($"DEVMODE obtained physical resolution: {physicalWidth}x{physicalHeight} (different from logical resolution)");
                    }
                }
                else
                {
                    Log($"DEVMODE failed, using logical resolution as physical resolution: {physicalWidth}x{physicalHeight}");
                }

                // Calculate DPI scaling ratio
                double scaleX = (double)physicalWidth / logicalWidth;
                double scaleY = (double)physicalHeight / logicalHeight;

                // Only print detailed info if there's scaling difference
                if (Math.Abs(scaleX - 1.0) > 0.01 || Math.Abs(scaleY - 1.0) > 0.01)
                {
                    Log($"Display [{screenIndex}] DPI scaling: {scaleX:F2}x{scaleY:F2}");
                }

                return (physicalWidth, physicalHeight, logicalWidth, logicalHeight);
            }
            catch (Exception ex)
            {
                Log($"Exception occurred while getting resolution: {ex.Message}");
                // Fallback to using Screen.Bounds as logical resolution
                Screen[] allScreens = Screen.AllScreens;
                var screen = screenIndex >= 0 && screenIndex < allScreens.Length ?
                           allScreens[screenIndex] : Screen.PrimaryScreen!;
                return (screen.Bounds.Width, screen.Bounds.Height, screen.Bounds.Width, screen.Bounds.Height);
            }
        }

        // Get the friendly name of the display
        private string GetScreenFriendlyName(int screenIndex)
        {
            try
            {
                var allScreens = Screen.AllScreens;
                if (screenIndex < 0 || screenIndex >= allScreens.Length)
                {
                    screenIndex = 0; // Use primary display by default
                }

                var targetScreen = allScreens[screenIndex];
                string deviceName = targetScreen.DeviceName.Replace("\\\\.\\", ""); // Remove prefix to match dropdown format

                // Use EnumDisplayDevices to get the display's friendly name
                NativeMethods.DISPLAY_DEVICE displayDevice = new NativeMethods.DISPLAY_DEVICE();
                displayDevice.cb = Marshal.SizeOf(displayDevice);

                if (NativeMethods.EnumDisplayDevices(targetScreen.DeviceName, 0, ref displayDevice, 0))
                {
                    // If friendly name is obtained, return it
                    if (!string.IsNullOrEmpty(displayDevice.DeviceString))
                    {
                        return displayDevice.DeviceString;
                    }
                }

                // If unable to get friendly name, return device name (matching dropdown format)
                string primaryMark = targetScreen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                return $"{deviceName}{primaryMark}";
            }
            catch (Exception ex)
            {
                Log($"Exception occurred while getting display friendly name: {ex.Message}");
                var targetScreen = Screen.AllScreens[screenIndex >= 0 && screenIndex < Screen.AllScreens.Length ? screenIndex : 0];
                string deviceName = targetScreen.DeviceName.Replace("\\\\.\\", "");
                string primaryMark = targetScreen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                return $"{deviceName}{primaryMark}";
            }
        }

        // Get display refresh rate using Windows API
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

                // Use EnumDisplaySettings to get current display mode
                NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(devMode);

                // ENUM_CURRENT_SETTINGS = -1, get current settings
                if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                {
                    // dmDisplayFrequency contains refresh rate (Hz)
                    if (devMode.dmDisplayFrequency > 0)
                    {
                        Log($"Successfully got display {screenIndex} refresh rate using Windows API: {devMode.dmDisplayFrequency}Hz");
                        return devMode.dmDisplayFrequency;
                    }
                }

                Log($"Unable to get display {screenIndex} refresh rate using Windows API");
                return 0.0;
            }
            catch (Exception ex)
            {
                Log($"Failed to get display {screenIndex} refresh rate using Windows API: {ex.Message}");
                return 0.0;
            }
        }

        // Get simplified unique identifier for display (device name preferred)
        private string GetDisplayUniqueId(int screenIndex, Screen screen)
        {
            try
            {
                // Use device name as unique identifier only
                string deviceName = screen.DeviceName;
                Log($"Display [{screenIndex}] using device name as identifier: {deviceName}");
                return deviceName;
            }
            catch (Exception ex)
            {
                Log($"Failed to get display [{screenIndex}] unique identifier: {ex.Message}, falling back to device name");
                return $"{screen.DeviceName}_fallback";
            }
        }



        // Generate display signature (includes index, name, resolution, DPI, refresh rate, etc.)
        private string GetDisplaySignature(int index, Screen screen)
        {
            try
            {
                // Get the unique hardware identifier of the display
                string uniqueId = GetDisplayUniqueId(index, screen);

                // Get DPI information (using the same method as the dropdown)
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

                // Get refresh rate
                double refreshRate = GetRefreshRateFromApi(index);

                // Calculate DPI percentage
                int dpiScalePercent = (int)(dpiX * 100 / 96);

                // Build signature: index:uniqueID:deviceName:resolution:DPI:refreshRate:primaryDisplayFlag
                return $"{index}:{uniqueId}:{screen.DeviceName}:{screen.Bounds.Width}x{screen.Bounds.Height}:{dpiScalePercent}:{refreshRate:F0}:{screen.Primary}";
            }
            catch (Exception ex)
            {
                Log($"Failed to generate display {index} signature: {ex.Message}");
                return $"{index}:error:{screen.DeviceName}:error:error:error:{screen.Primary}";
            }
        }

        // Record initial display state
        private void RecordInitialDisplayState()
        {
            try
            {
                var screens = Screen.AllScreens;
                _lastDisplaySignatures = new string[screens.Length];

                Log($"Record initial display state, found {screens.Length} displays:");
                for (int i = 0; i < screens.Length; i++)
                {
                    _lastDisplaySignatures[i] = GetDisplaySignature(i, screens[i]);
                    Log($"  Display [{i}] signature: {_lastDisplaySignatures[i]}");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to record initial display state: {ex.Message}");
                _lastDisplaySignatures = null;
            }
        }

        // Check for display changes
        private void CheckDisplayChanges()
        {
            if (!_isDisplayMonitoringEnabled || _lastDisplaySignatures == null)
                return;

            try
            {
                var currentScreens = Screen.AllScreens;

                // Check count changes
                if (currentScreens.Length != _lastDisplaySignatures.Length)
                {
                    Log($"Detected display count change: {_lastDisplaySignatures.Length} -> {currentScreens.Length}");
                    AutoStopDueToDisplayChange(Localization.GetText("DisplayCountChange"));
                    return;
                }

                // Check status of each display
                for (int i = 0; i < currentScreens.Length; i++)
                {
                    string currentSignature = GetDisplaySignature(i, currentScreens[i]);
                    if (currentSignature != _lastDisplaySignatures[i])
                    {
                        Log($"Detected display {i} configuration change:");
                        Log($"  Original signature: {_lastDisplaySignatures[i]}");
                        Log($"  New signature: {currentSignature}");

                        // Parse signature changes, especially focus on device name changes and primary display switching
                        var oldParts = _lastDisplaySignatures[i].Split(':');
                        var newParts = currentSignature.Split(':');

                        if (oldParts.Length >= 3 && newParts.Length >= 3)
                        {
                            string oldDeviceName = oldParts[2];
                            string newDeviceName = newParts[2];

                            if (oldDeviceName != newDeviceName)
                            {
                                Log($"  Device name change: {oldDeviceName} -> {newDeviceName}");
                            }

                            // Check if primary display switching
                            bool oldIsPrimary = oldParts.Length > 4 && oldParts[4] == "Primary";
                            bool newIsPrimary = newParts.Length > 4 && newParts[4] == "Primary";
                            if (oldIsPrimary != newIsPrimary)
                            {
                                Log($"  Primary display status change: {oldIsPrimary} -> {newIsPrimary}");
                            }
                        }

                        // Use new handling method
                        HandleDisplayConfigurationChange();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking display changes: {ex.Message}");
            }
        }

        // Update status bar to display current selected display information
        private void UpdateStatusBarDisplayInfo()
        {
            try
            {
                if (_targetScreenIndex >= 0 && _targetScreenIndex < Screen.AllScreens.Length)
                {
                    var screen = Screen.AllScreens[_targetScreenIndex];
                    string deviceName = screen.DeviceName.Replace("\\\\.\\", "");
                    string primaryMark = screen.Primary ? $" [{Localization.GetText("Primary")}]" : "";

                    // Get refresh rate
                    double refreshRate = GetRefreshRateFromApi(_targetScreenIndex);
                    string refreshInfo = refreshRate > 0 ? $" {refreshRate:F0}Hz" : "";

                    // Get resolution
                    var (physicalWidth, physicalHeight, logicalWidth, logicalHeight) = GetScreenResolutions(_targetScreenIndex);
                    string resolutionInfo = $"{physicalWidth}x{physicalHeight}";

                    // Get display friendly name
                    string friendlyName = GetScreenFriendlyName(_targetScreenIndex);
                    if (!string.IsNullOrEmpty(friendlyName))
                    {
                        deviceName = friendlyName;
                    }

                    // Update status bar text - display different formats based on program running status and language
                    string statusText;
                    if (_pollTimer?.Enabled == true)
                    {
                        // Running status
                        statusText = string.Format(Localization.GetText("StatusRunning"), deviceName, primaryMark, resolutionInfo, refreshInfo);
                    }
                    else
                    {
                        // Stopped status - only display "Status: Stopped"
                        statusText = Localization.GetText("StatusStopped");
                    }
                    lblInfo.Text = statusText;

                    Log($"Status bar updated: {statusText}");
                }
                else
                {
                    SafeUpdateStatusText(Localization.GetText("StatusStopped"));
                    Log($"Status bar reset to default state");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to update status bar display info: {ex.Message}");
                lblInfo.Text = Localization.GetText("StatusStopped");
            }
        }

        // Auto-stop due to display changes
        private void AutoStopDueToDisplayChange(string reason)
        {
            // Deduplication check: if display changes have been processed within the deduplication interval, ignore this detection
            var now = DateTime.Now;
            var timeSinceLastDetection = now - _lastDisplayChangeDetectionTime;
            if (timeSinceLastDetection.TotalMilliseconds < DISPLAY_CHANGE_DEDUPLICATION_INTERVAL)
            {
                Log($"Ignore duplicate display change detection ({reason}), only {timeSinceLastDetection.TotalMilliseconds:F0}ms since last detection");
                return;
            }

            // Check if display change popup is already being displayed
            if (_displayChangeMessageShown)
            {
                Log($"Ignore duplicate display change popup ({reason}), popup already displayed");
                return;
            }

            _lastDisplayChangeDetectionTime = now;
            _displayChangeMessageShown = true; // Mark popup as displayed
            Log($"Auto-stop refresh due to {reason}");

            try
            {
                // Ensure safe stop of refresh on UI thread
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() => AutoStopDueToDisplayChange(reason)));
                    return;
                }

                // Safely stop refresh
                if (_pollTimer?.Enabled == true)
                {
                    if (btnStop.Enabled)
                    {
                        btnStop.PerformClick();
                    }
                }

                // Safely update status to stopped
                SafeUpdateStatusText($"{Localization.GetText("StatusStopped")} - {reason}");

                // Display prompt message (select Chinese or English based on current language)
                this.BeginInvoke(new Action(() =>
                {
                    string title = Localization.GetText("WindowTitle"); // Use program name as title
                    string message = string.Format(Localization.GetText("DisplayChangeAutoStop"), reason);
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Reset flag immediately after popup closes
                    _displayChangeMessageShown = false;
                    Log("Display change popup closed, shortcut key functionality restored");
                }));

                // Completely reinitialize display related status
                this.Invoke(new Action(() =>
                {
                    Log("Starting to reinitialize display configuration...");

                    try
                    {
                        // Save device name of currently selected display for rematching
                        string previousDeviceName = _targetDisplayDeviceName ?? string.Empty;
                        int previousIndex = _targetScreenIndex;

                        Log($"Before reinitialization - Device name: '{previousDeviceName}', Index: {previousIndex}");

                        // 1. Repopulate display list (including intelligent matching logic)
                        PopulateDisplayList();

                        // 2. If there's a previous device name, try to rematch
                        if (!string.IsNullOrEmpty(previousDeviceName))
                        {
                            var screens = Screen.AllScreens;
                            bool foundMatch = false;

                            for (int i = 0; i < screens.Length; i++)
                            {
                                if (screens[i].DeviceName == previousDeviceName)
                                {
                                    Log($"Rematch successful: Found previous device '{previousDeviceName}', new index {i}");
                                    _targetScreenIndex = i;
                                    _targetDisplayDeviceName = previousDeviceName;
                                    comboDisplay.SelectedIndex = i;
                                    foundMatch = true;
                                    break;
                                }
                            }

                            if (!foundMatch)
                            {
                                Log($"Rematch failed: previous device '{previousDeviceName}' not found");
                            }
                        }

                        // 3. Record display signature again
                        RecordInitialDisplayState();

                        // 4. Update status bar to show current selected display info
                        UpdateStatusBarDisplayInfo();

                        // 5. Force re-detect refresh rate of current display
                        if (_targetScreenIndex >= 0)
                        {
                            double currentRefreshRate = GetRefreshRateFromApi(_targetScreenIndex);
                            Log($"After re-detection, current selected display [{_targetScreenIndex}] refresh rate: {currentRefreshRate}Hz");

                            // 6. If refresh rate exceeds limit, show warning
                            if (_stopOver59hz == 1 && currentRefreshRate > 59)
                            {
                                string warningMessage = string.Format(Localization.GetText("HighRefreshRateCurrentWarning"), currentRefreshRate);
                                MessageBox.Show(warningMessage, Localization.GetText("WindowTitle"),
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }

                        Log($"Display configuration reinitialized - Final selection: Index {_targetScreenIndex}, Device '{_targetDisplayDeviceName}'");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to reinitialize display configuration: {ex.Message}");
                        SafeUpdateStatusText($"{Localization.GetText("StatusStopped")} - Display configuration error");
                    }
                }));
            }
            catch (Exception ex)
            {
                Log($"Error when auto-stopping refresh: {ex.Message}");
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

            // Check if need to recreate OverlayForm (when tileSize changes)
            bool needRecreateOverlay = _overlayForm != null && _d3d != null && _overlayForm.TileSize != _d3d!.TileSize;
            if (needRecreateOverlay)
            {
                Log($"Detected OverlayForm tileSize change, recreating...");
                _overlayForm!.Close();
                _overlayForm.Dispose();
                _overlayForm = null;
            }

            if (_overlayForm == null)
            {
                Color overlayBaseColor = Color.FromName(OVERLAY_BASE_COLOR);

                // Get physical and logical resolutions
                var (physicalWidth, physicalHeight, logicalWidth, logicalHeight) = GetScreenResolutions(_targetScreenIndex);

                // Calculate scaling ratio from physical resolution to logical resolution
                double scaleX = (double)physicalWidth / logicalWidth;
                double scaleY = (double)physicalHeight / logicalHeight;

                Log($"Overlay creation: Physical resolution={physicalWidth}x{physicalHeight}, Logical resolution={logicalWidth}x{logicalHeight}, Scale ratio={scaleX:F2}x{scaleY:F2}");

                // Get target display position information
                var allScreens = Screen.AllScreens;
                var targetScreen = _targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length ?
                    allScreens[_targetScreenIndex] : Screen.PrimaryScreen!;
                var screenBounds = targetScreen.Bounds;

                _overlayForm = new OverlayForm(_d3d!.TileSize, logicalWidth, logicalHeight, NOISE_DENSITY, NOISE_POINT_INTERVAL, overlayBaseColor, Log, _targetScreenIndex, scaleX, scaleY)
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    TopMost = true,
                    Size = new Size(logicalWidth, logicalHeight),
                    Location = screenBounds.Location  // Ensure overlay displays on the correct monitor
                };
                _overlayForm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;

                _overlayForm.Show();
                Log($"Overlay displayed on monitor [{_targetScreenIndex}]: Position=({screenBounds.Left}, {screenBounds.Top}), Size={logicalWidth}x{logicalHeight}");
            }

            _overlayForm?.UpdateContent(tiles, brightnessData);
        }

        private float GetDpiScale()
        {
            // Try to get DPI settings for the specified display
            try
            {
                // Get information about all displays
                var allScreens = Screen.AllScreens;
                Log($"GetDpiScale: Total detected displays: {allScreens.Length}");

                // Check if target display index is valid
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];
                    Log($"GetDpiScale: Attempting to get DPI settings for display [{_targetScreenIndex}]");

                    // Try to create Graphics object for specific display to get its DPI
                    try
                    {
                        // Get the bounds rectangle of the display
                        var bounds = targetScreen.Bounds;
                        Log($"GetDpiScale: Display [{_targetScreenIndex}] bounds = ({bounds.Left}, {bounds.Top}, {bounds.Width}x{bounds.Height})");

                        // Create temporary window handle to get DPI for this display
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
                                    Log($"GetDpiScale: Successfully got display [{_targetScreenIndex}] DPI = {dpiX}, Scale = {scale:F2}");
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
                            Log($"GetDpiScale: Unable to create temporary window handle for display [{_targetScreenIndex}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GetDpiScale: Exception occurred while getting DPI for display [{_targetScreenIndex}]: {ex.Message}");
                        // If unable to get specific DPI, continue using window DPI
                    }
                }
                else
                {
                    Log($"GetDpiScale: Target display index {_targetScreenIndex} out of range [0-{allScreens.Length - 1}]");
                }
            }
            catch (Exception ex)
            {
                Log($"GetDpiScale: Exception occurred while enumerating display information: {ex.Message}");
                // If unable to get specific display's DPI, continue using window DPI
            }

            // Fallback to original implementation
            uint windowDpi = GetDpiForWindow(this.Handle);
            float fallbackScale = windowDpi / 96f;
            Log($"GetDpiScale: Fallback to window DPI = {windowDpi}, Scale = {fallbackScale:F2}");
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

        // Register hotkey
        private void RegisterToggleHotkey()
        {
            if (_isHotkeyRegistered)
            {
                UnregisterToggleHotkey();
            }

            try
            {
                // Extract virtual key code and modifiers
                Keys keyCode = _toggleHotkey & Keys.KeyCode;
                Keys modifiers = _toggleHotkey & Keys.Modifiers;

                int modFlags = 0;
                if ((modifiers & Keys.Control) == Keys.Control)
                    modFlags |= 0x0002; // MOD_CONTROL
                if ((modifiers & Keys.Alt) == Keys.Alt)
                    modFlags |= 0x0001; // MOD_ALT
                if ((modifiers & Keys.Shift) == Keys.Shift)
                    modFlags |= 0x0004; // MOD_SHIFT

                // Register system-level hotkey
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

        // Unregister hotkey
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
            // Check current focused window
            IntPtr foregroundWindow = GetForegroundWindow();
            bool isCurrentWindowFocused = (foregroundWindow == this.Handle);

            // Only show notification when current window is not focused
            bool shouldShowNotification = !isCurrentWindowFocused;

            if (_pollTimer?.Enabled != true)
            {
                // Start capture
                if (shouldShowNotification)
                {
                    ShowNotification(Localization.GetText("CaptureStartedTitle"), Localization.GetText("CaptureStartedMessage"));
                }
                btnStart.PerformClick();
            }
            else
            {
                // Stop capture
                if (shouldShowNotification)
                {
                    ShowNotification(Localization.GetText("CaptureStoppedTitle"), Localization.GetText("CaptureStoppedMessage"));
                }
                btnStop.PerformClick();
            }
        }

        // Show tray notification
        private void ShowNotification(string title, string message)
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(2000);
        }

        private void UpdateAdaptiveLayout()
        {
            // Completely remove adaptive layout logic as it causes issues in high DPI environments
            // Rely on WinForm's built-in DPI handling mechanism
        }

        // ==================== Designer Event Handlers ====================

        private void MainForm_Load(object? sender, EventArgs e)
        {
            // Update control texts to localized texts
            UpdateLocalizedTexts();

            // Update control values
            trackPixelDelta.Value = _pixelDelta;
            lblPixelDeltaValue.Text = _pixelDelta.ToString();
            trackTileSize.Value = _tileSize;
            lblTileSizeValue.Text = _tileSize.ToString();

            // If no hotkey is set, show prompt text
            if (_toggleHotkey == Keys.None)
            {
                txtToggleHotkey.Text = Localization.GetText("ClickButtonToSet");
            }
            else
            {
                txtToggleHotkey.Text = FormatShortcut(_toggleHotkey);
            }

            // Set initial button states
            SafeUpdateStatusText(Localization.GetText("StatusStopped"));

            // Populate display list
            PopulateDisplayList();

            // Execute adaptive layout
            UpdateAdaptiveLayout();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                UpdateAdaptiveLayout();
                AdjustStatusLabelProperties(); // Readjust status label when window size changes
            }
        }

        // Find display index with lowest refresh rate
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

                    // If get refresh rate fails (returns 0), give a default value of 60Hz for comparison
                    if (refreshRate <= 0)
                        refreshRate = 60.0;

                    Log($"Detected display {i} refresh rate: {refreshRate:F1}Hz");

                    // Select display with the lowest refresh rate
                    if (refreshRate < minRefreshRate)
                    {
                        minRefreshRate = refreshRate;
                        minIndex = i;
                    }
                }

                Log($"Selected display with lowest refresh rate: Index {minIndex}, Refresh rate {minRefreshRate:F1}Hz");
                return minIndex;
            }
            catch (Exception ex)
            {
                Log($"Failed to find display with lowest refresh rate: {ex.Message}, defaulting to index 0");
                return 0;
            }
        }

        private void PopulateDisplayList()
        {
            try
            {
                comboDisplay.Items.Clear();

                // Get all displays, using system default order (no special handling for primary display)
                var screens = Screen.AllScreens;

                // Add detailed display debug information
                Log($"System detected {screens.Length} displays:");
                for (int debugIdx = 0; debugIdx < screens.Length; debugIdx++)
                {
                    var debugScreen = screens[debugIdx];
                    Log($"  Display [{debugIdx}]: {debugScreen.DeviceName}, Primary: {debugScreen.Primary}, Bounds: {debugScreen.Bounds}");
                }
                Log($"Target display index from config file: {_targetScreenIndex}");

                for (int i = 0; i < screens.Length; i++)
                {
                    var screen = screens[i];

                    // Get display DPI information (using GetDpiForMonitor method)
                    uint dpiX = 96;
                    uint dpiY = 96;
                    try
                    {
                        // Get display center point
                        var bounds = screen.Bounds;
                        var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

                        // Get display handle
                        IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

                        if (hMonitor != IntPtr.Zero)
                        {
                            // Use GetDpiForMonitor to get DPI information (not using V2 version)
                            int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out dpiX, out dpiY);

                            if (result == 0)
                            {
                                Log($"Successfully got display {i} DPI: {dpiX}x{dpiY}");
                            }
                            else
                            {
                                Log($"Failed to get display {i} DPI, error code: 0x{result:X8}, using default 96 DPI");
                                dpiX = 96;
                                dpiY = 96;
                            }
                        }
                        else
                        {
                            Log($"Unable to get monitor handle for display {i}, using default 96 DPI");
                            dpiX = 96;
                            dpiY = 96;
                        }
                    }
                    catch (Exception dpiEx)
                    {
                        Log($"Failed to get display {i} DPI: {dpiEx.Message}, using default 96 DPI");
                        dpiX = 96;
                        dpiY = 96;
                    }

                    // Calculate DPI percentage (relative to standard 96 DPI)
                    int dpiScalePercent = (int)(dpiX * 100 / 96);

                    // Get refresh rate (using Windows API)
                    double refreshRate = GetRefreshRateFromApi(i);

                    // Get physical resolution (to avoid differences between release package and debug mode)
                    int physicalWidth = screen.Bounds.Width;
                    int physicalHeight = screen.Bounds.Height;

                    // If DPI scaling is not 100%, try to get real physical resolution
                    if (dpiScalePercent != 100)
                    {
                        try
                        {
                            // Use existing GetScreenResolutions method to get physical resolution
                            var (physicalResWidth, physicalResHeight, logicalResWidth, logicalResHeight) = GetScreenResolutions(i);
                            if (physicalResWidth > 0 && physicalResHeight > 0)
                            {
                                physicalWidth = physicalResWidth;
                                physicalHeight = physicalResHeight;
                                Log($"Display {i} DPI scaling {dpiScalePercent}%, using physical resolution: {physicalWidth}×{physicalHeight} (logical resolution {screen.Bounds.Width}×{screen.Bounds.Height})");
                            }
                        }
                        catch (Exception resEx)
                        {
                            Log($"Failed to get physical resolution for display {i}: {resEx.Message}, using logical resolution");
                        }
                    }
                    else
                    {
                        Log($"Display {i} DPI scaling 100%, physical resolution same as logical resolution: {physicalWidth}×{physicalHeight}");
                    }

                    // Build display name, including DPI and refresh rate information
                    string dpiInfo = $"{dpiScalePercent}%";
                    string refreshInfo = refreshRate > 0 ? $" {refreshRate:F0}Hz" : "";
                    string primaryMark = screen.Primary ? $" [{Localization.GetText("Primary")}]" : "";
                    // Use device name to ensure correct matching, but keep format concise
                    string deviceName = screen.DeviceName.Replace("\\\\.\\", ""); // Remove prefix to make display more concise
                    string displayName = $"{deviceName}{primaryMark}: {physicalWidth}×{physicalHeight} @ {dpiInfo}{refreshInfo}";
                    comboDisplay.Items.Add(displayName);
                }

                // Select display based on config file (using intelligent matching logic)
                if (comboDisplay.Items.Count > 0)
                {
                    int targetIndex = -1;

                    // Intelligent matching strategy: multi-level display matching
                    // 1. First try matching by device name (most reliable identifier)
                    if (!string.IsNullOrEmpty(_targetDisplayDeviceName))
                    {
                        Log($"Attempting to match display by device name '{_targetDisplayDeviceName}'");

                        for (int i = 0; i < screens.Length; i++)
                        {
                            if (screens[i].DeviceName == _targetDisplayDeviceName)
                            {
                                targetIndex = i;
                                Log($"Device name match successful: found display with device '{_targetDisplayDeviceName}', index {targetIndex}");
                                break;
                            }
                        }

                        if (targetIndex == -1)
                        {
                            Log($"Device name match failed: display with device '{_targetDisplayDeviceName}' not found");
                        }
                    }

                    // 2. If device name matching fails, try matching by refresh rate and resolution
                    if (targetIndex == -1 && _targetScreenIndex >= 0 && _targetScreenIndex < screens.Length)
                    {
                        var (prevWidth, prevHeight, prevLogicalWidth, prevLogicalHeight) = GetScreenResolutions(_targetScreenIndex);
                        double prevRefreshRate = GetRefreshRateFromApi(_targetScreenIndex);

                        Log($"Attempting to match display by refresh rate {prevRefreshRate}Hz and resolution {prevWidth}x{prevHeight}");

                        for (int i = 0; i < screens.Length; i++)
                        {
                            var (currWidth, currHeight, currLogicalWidth, currLogicalHeight) = GetScreenResolutions(i);
                            double currRefreshRate = GetRefreshRateFromApi(i);

                            // Match refresh rate and resolution
                            if (currRefreshRate == prevRefreshRate && currWidth == prevWidth && currHeight == prevHeight)
                            {
                                targetIndex = i;
                                Log($"Refresh rate and resolution match successful: found display with refresh rate {currRefreshRate}Hz resolution {currWidth}x{currHeight}, index {targetIndex}");
                                break;
                            }
                        }

                        if (targetIndex == -1)
                        {
                            Log($"Refresh rate and resolution match failed: display with refresh rate {prevRefreshRate}Hz resolution {prevWidth}x{prevHeight} not found");
                        }
                    }

                    // 3. If all else fails, match by index (if index is valid)
                    if (targetIndex == -1 && _targetScreenIndex >= 0 && _targetScreenIndex < comboDisplay.Items.Count)
                    {
                        targetIndex = _targetScreenIndex;
                        Log($"Using index matching: {_targetScreenIndex}");
                    }

                    // 4. Last resort, select display with lowest refresh rate
                    if (targetIndex == -1)
                    {
                        targetIndex = FindLowestRefreshRateDisplay(screens);
                        Log($"Using default matching: selected lowest refresh rate display index {targetIndex}");
                    }

                    // Apply final selection
                    comboDisplay.SelectedIndex = targetIndex;
                    _targetScreenIndex = targetIndex;

                    // Update device name record (used only at runtime)
                    if (targetIndex >= 0 && targetIndex < screens.Length)
                    {
                        _targetDisplayDeviceName = screens[targetIndex].DeviceName;
                        Log($"Final selection: index {targetIndex}, device {_targetDisplayDeviceName}");
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
                // Process only when selection actually changes
                if (comboDisplay.SelectedIndex != _targetScreenIndex)
                {
                    // Check if screen capture is running
                    if (_pollTimer != null && _pollTimer.Enabled)
                    {
                        string message = Localization.GetText("CannotSwitchDisplayWhileCapturing");
                        string title = Localization.GetText("WindowTitle");
                        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

                        // Restore original selection but don't block user operation
                        comboDisplay.SelectedIndex = _targetScreenIndex;
                        return;
                    }

                    // Only allow switching when screen capture is stopped
                    if (comboDisplay.SelectedIndex >= 0)
                    {
                        _targetScreenIndex = comboDisplay.SelectedIndex;
                        // Save device name of currently selected display for intelligent matching
                        var screens = Screen.AllScreens;
                        if (_targetScreenIndex < screens.Length)
                        {
                            _targetDisplayDeviceName = screens[_targetScreenIndex].DeviceName;
                            Log($"Display changed to index: {_targetScreenIndex}, device: {_targetDisplayDeviceName}");
                        }
                        SaveConfig(); // Save configuration to file

                        // Destroy existing overlay to ensure it's recreated on new monitor next time
                        if (_overlayForm != null)
                        {
                            _overlayForm.Close();
                            _overlayForm.Dispose();
                            _overlayForm = null;
                            Log($"Overlay destroyed, will be recreated on new monitor [{_targetScreenIndex}]");
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
                // Recording mode: capture key presses
                e.Handled = true;
                e.SuppressKeyPress = true;

                if (e.KeyCode == Keys.Escape)
                {
                    // ESC key cancels recording
                    CancelHotkeyRecording();
                    return;
                }

                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
                {
                    // Ignore standalone modifier keys
                    return;
                }

                // Get key combination
                Keys keyCombo = e.KeyData;

                // Update internal variables and display
                _toggleHotkey = keyCombo;
                string formattedShortcut = FormatShortcut(keyCombo);
                txtToggleHotkey.Text = formattedShortcut;

                Log($"Hotkey recorded: {formattedShortcut} - continue recording");
            }
            else if (e.KeyData == _toggleHotkey && _isHotkeyRegistered && !_isRecordingHotkey)
            {
                // Toggle running state (only respond when not in recording mode)
                e.Handled = true;
                e.SuppressKeyPress = true;
                ToggleCaptureState();
            }
        }

        private void btnStart_Click(object? sender, EventArgs e)
        {
            // Check if recording hotkey
            if (_isRecordingHotkey)
            {
                string message = Localization.GetText("CannotStartWhileRecordingHotkey");
                string title = Localization.GetText("WindowTitle"); // Use program name as title
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.None);
                return;
            }

            // Check display refresh rate (auto-stop function for over 59Hz)
            if (_stopOver59hz == 1)
            {
                double refreshRate = GetRefreshRateFromApi(_targetScreenIndex);
                if (refreshRate >= 59.0)
                {
                    string message = string.Format(Localization.GetText("HighRefreshRateWarning"), refreshRate);
                    string title = Localization.GetText("WindowTitle"); // Use program name as title
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Log($"Blocked start due to high refresh rate: {refreshRate:F1}Hz (stopOver59hz={_stopOver59hz})");
                    return;
                }
            }

            btnStart.Enabled = false;
            _cts = new CancellationTokenSource();
            _frameCounter = 0; // Reset frame counter on start

            // Disable settings modification
            trackPixelDelta.Enabled = false;
            trackTileSize.Enabled = false;
            // Note: gear button remains enabled, handle disable logic through click event interception

            SafeUpdateStatusText("Status: Initializing GPU capture...");
            Log("Initializing GPU capture...");

            // Record initial display state (for change detection)
            RecordInitialDisplayState();

            try
            {
                // Check if need to recreate D3D object (when tileSize changes)
                bool needRecreateD3D = _lastUsedTileSize != _tileSize;
                if (needRecreateD3D && _lastUsedTileSize > 0)
                {
                    Log($"Detected tileSize change: from {_lastUsedTileSize} to {_tileSize}, reinitializing D3D objects...");
                }
                
                // If D3D object exists and tileSize changed, or needs to be recreated
                if (_d3d != null && needRecreateD3D)
                {
                    _d3d?.Dispose();
                    _d3d = null;
                    
                    // Close existing overlayForm (if exists)
                    if (_overlayForm != null)
                    {
                        _overlayForm.Close();
                        _overlayForm.Dispose();
                        _overlayForm = null;
                    }
                }

                _d3d = new D3DCaptureAndCompute(DebugLogger, _tileSize, _pixelDelta, AVERAGE_WINDOW_SIZE, STABLE_FRAMES_REQUIRED, ADDITIONAL_COOLDOWN_FRAMES, FIRST_REFRESH_EXTRA_DELAY, CARET_CHECK_INTERVAL, IME_CHECK_INTERVAL, MOUSE_EXCLUSION_RADIUS_FACTOR,
                    new BoundingAreaConfig(
                        BOUNDING_AREA_WIDTH,
                        BOUNDING_AREA_HEIGHT,
                        BOUNDING_AREA_HISTORY_FRAMES,
                        BOUNDING_AREA_CHANGE_THRESHOLD,
                        BOUNDING_AREA_REFRESH_BLOCK_THRESHOLD), _forceDirectXCapture, ProtectionFrames, _targetScreenIndex);

                // Update last used tileSize
                _lastUsedTileSize = _tileSize;

                _pollTimer = new System.Windows.Forms.Timer
                {
                    Interval = _pollInterval
                };
                _pollTimer.Tick += async (ss, ee) =>
                {
                    try
                    {
                        // Check for display changes every 3 seconds (when counter reaches DISPLAY_CHECK_INTERVAL)
                        _displayCheckCounter++;
                        if (_displayCheckCounter >= DISPLAY_CHECK_INTERVAL)
                        {
                            _displayCheckCounter = 0;
                            CheckDisplayChanges();
                        }

                        if (_cts.Token.IsCancellationRequested || _d3d == null) return;

                        _frameCounter++; // Increment frame counter

                        // Periodically release memory pressure (every 100 frames, about 50 seconds)
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

                        // If critical error occurs, automatically stop capture
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

                // Get physical and logical resolutions
                var (physicalWidth, physicalHeight, logicalWidth, logicalHeight) = GetScreenResolutions(_targetScreenIndex);

                // Get display friendly name
                string screenFriendlyName = GetScreenFriendlyName(_targetScreenIndex);

                // Use unified DPI acquisition logic to ensure consistency with dropdown display
                // Get DPI for current display (using same method as dropdown)
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
                            Log($"Status bar using display {_targetScreenIndex} DPI: {dpiX}x{dpiY} (consistent with dropdown)");
                        }
                        else
                        {
                            Log($"Status bar DPI acquisition failed, error code: 0x{result:X8}, using default 96 DPI");
                            dpiX = 96;
                            dpiY = 96;
                        }
                    }
                    else
                    {
                        Log($"Status bar unable to get display {_targetScreenIndex} handle, using default 96 DPI");
                        dpiX = 96;
                        dpiY = 96;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Status bar DPI acquisition failed: {ex.Message}, using default 96 DPI");
                    dpiX = 96;
                    dpiY = 96;
                }

                // Recalculate logical resolution using unified DPI
                double scaleX = (double)dpiX / 96.0;
                double scaleY = (double)dpiY / 96.0;
                logicalWidth = (int)(physicalWidth / scaleX);
                logicalHeight = (int)(physicalHeight / scaleY);

                // Calculate DPI scaling ratio: physical resolution ÷ logical resolution
                double dpiScaleX = (double)physicalWidth / logicalWidth;
                double dpiScaleY = (double)physicalHeight / logicalHeight;
                double dpiScale = Math.Max(dpiScaleX, dpiScaleY); // Use larger scaling ratio
                int scalePercent = (int)(dpiScale * 100);
                // Customize status text format to avoid duplicate resolution text
                string statusRunning = Localization.GetText("StatusRunning");
                string statusPrefix = statusRunning;
                
                // Remove corresponding resolution part based on current language
                string resolutionSeparator = Localization.GetText("ResolutionSeparator");
                statusPrefix = statusRunning.Split(resolutionSeparator)[0];
                double refreshRate = GetRefreshRateFromApi(_targetScreenIndex);
                string refreshInfo = refreshRate > 0 ? $" {refreshRate:F0}Hz" : "";
                string statusText = string.Format(statusPrefix, screenFriendlyName, "", "", "");
                
                // Use localized strings to replace physical, logical, scale, tile size and other text
                string physical = Localization.GetText("Physical");
                string logical = Localization.GetText("Logical");
                string scale = Localization.GetText("Scale");
                string tileSize = Localization.GetText("TileSize");
                string pixels = Localization.GetText("Pixels");
                string resolution = Localization.GetText("Resolution");
                
                SafeUpdateStatusText($"{statusText} {resolution}：({physical}: {physicalWidth}x{physicalHeight}, {scale}: {scalePercent}%, {logical}: {logicalWidth}x{logicalHeight}), {refreshInfo}, {tileSize}: {_tileSize}x{_tileSize} {pixels}");
                btnStop.Enabled = true;
                Log($"GPU capture initialized successfully. Physical: {physicalWidth}x{physicalHeight}, Logical: {logicalWidth}x{logicalHeight} (DXGI), Scale: {scalePercent}%, DPI: {dpiScaleX:F2}x{dpiScaleY:F2}, Tile Size: {_tileSize}x{_tileSize} pixels");

                // After D3D initialization completes, repopulate display list to get refresh rate information
                this.Invoke(new Action(() =>
                {
                    PopulateDisplayList();
                }));
            }
            catch (Exception ex)
            {
                string errorMessage = $"Initialization failed: {ex.Message}";
                Log(errorMessage + "\n" + ex.StackTrace);
                MessageBox.Show(errorMessage, Localization.GetText("WindowTitle"), MessageBoxButtons.OK, MessageBoxIcon.None); // Use program name as title
                btnStart.Enabled = true;
                // Gear button remains enabled, no special handling needed
                SafeUpdateStatusText("Status: Failed");
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

            // Re-enable settings modification
            trackPixelDelta.Enabled = true;
            trackTileSize.Enabled = true;
            // Gear button remains enabled, no special handling needed
        }

        private void btnSettings_Click(object? sender, EventArgs e)
        {
            // Check if screen capture is running
            if (_pollTimer != null && _pollTimer.Enabled)
            {
                string message = Localization.GetText("CannotModifySettingsWhileRunning");
                string title = Localization.GetText("WindowTitle"); // Use program name as title
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var settingsForm = new SettingsForm(_stopOver59hz == 1))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    _stopOver59hz = settingsForm.StopOver59Hz ? 1 : 0;
                    SaveConfig();
                    Log($"Settings updated: Stop over 59Hz displays = {(settingsForm.StopOver59Hz ? "enabled" : "disabled")}");
                }
            }
        }

        private void trackPixelDelta_ValueChanged(object? sender, EventArgs e)
        {
            _pixelDelta = trackPixelDelta.Value;
            lblPixelDeltaValue.Text = _pixelDelta.ToString();
            SaveConfig();
        }

        private void trackTileSize_ValueChanged(object? sender, EventArgs e)
        {
            _tileSize = trackTileSize.Value;
            lblTileSizeValue.Text = _tileSize.ToString();
            SaveConfig();
        }

        private void btnHelpPixelDelta_Click(object? sender, EventArgs e)
        {
            // Display help content in appropriate language based on current language
            string helpText = Localization.GetText("PixelDiffThresholdHelpContent");

            MessageBox.Show(helpText, Localization.GetText("PixelDiffThresholdHelpTitle"), MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        private void btnHelpPixelDelta_Paint(object? sender, PaintEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            // Create circular region
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(0, 0, btn.Width - 1, btn.Height - 1);
                btn.Region = new Region(path);
            }

            // Draw circular background
            using (var backgroundBrush = new SolidBrush(btn.BackColor))
            {
                e.Graphics.FillEllipse(backgroundBrush, 0, 0, btn.Width - 1, btn.Height - 1);
            }

            using (var font = new Font(btn.Font.FontFamily, btn.Font.Size, btn.Font.Style))
            using (var brush = new SolidBrush(btn.BackColor == Color.FromArgb(135, 206, 235) ? Color.White : btn.ForeColor))
            {
                // Precisely measure text size
                var textSize = e.Graphics.MeasureString("?", font);
                // Calculate precise position for perfect centering, shift 2 pixels right within circle
                var x = (btn.Width - textSize.Width) / 2 + 2;
                var y = (btn.Height - textSize.Height) / 2;
                e.Graphics.DrawString("?", font, brush, x, y);
            }
        }

        private void btnHelpPixelDelta_MouseEnter(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.FromArgb(135, 206, 235); // Slightly darker light blue
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
                // If recording, prioritize recording logic
                // Stop recording, clear hotkey if no keys were input
                _isRecordingHotkey = false;
                btnToggleRecord.Text = "●";
                btnToggleRecord.ForeColor = Color.Red;
                btnToggleRecord.BackColor = Color.White;

                // If textbox shows hint text, user didn't input any keys
                if (txtToggleHotkey.Text == Localization.GetText("PressHotkeyCombination"))
                {
                    // User didn't input any keys, clear the hotkey
                    CancelHotkeyRecording();
                }
                else
                {
                    // User input keys, save the hotkey
                    SaveToggleHotkey();
                }
            }
            else
            {
                // If not recording, check if running
                if (_pollTimer != null && _pollTimer.Enabled)
                {
                    string message = Localization.GetText("CannotModifyHotkeyWhileRunning");
                    string title = Localization.GetText("WindowTitle");
                    MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.None);
                    return;
                }

                // Start recording
                _isRecordingHotkey = true;
                btnToggleRecord.Text = "✓"; // Changed to checkmark symbol
                btnToggleRecord.BackColor = Color.White;
                txtToggleHotkey.Text = Localization.GetText("PressHotkeyCombination");

                // Temporarily unregister current hotkey to avoid conflicts
                if (_isHotkeyRegistered)
                {
                    NativeMethods.UnregisterHotKey(this.Handle, TOGGLE_HOTKEY_ID);
                    _isHotkeyRegistered = false;
                }

                // Clear temp variables when starting recording to ensure fresh start each time
                _toggleHotkey = Keys.None;
            }
        }

        private void btnToggleRecord_Paint(object? sender, PaintEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var text = btn.Text;

            // Draw background
            e.Graphics.Clear(btn.BackColor);

            // Choose drawing method based on text content, ensure center alignment
            using (var font = new Font(btn.Font.FontFamily, btn.Font.Size, btn.Font.Style))
            {
                if (text == "●") // Circle dot - changed to ring + inner small dot
                {
                    // Check if it's the record button, use image if available
                    if (btn == btnToggleRecord)
                    {
                        DrawRecordButton(e.Graphics, btn.Width, btn.Height);
                    }
                    else
                    {
                        using (var brush = new SolidBrush(btn.ForeColor))
                        {
                            // Draw ring - reference square dot size, don't let ring fill entire button
                            int baseSize = Math.Min(btn.Width, btn.Height);
                            int outerDiameter = baseSize - 32; // Enlarge ring further, changed from -36 to -32
                            int ringThickness = 2; // Fixed ring thickness, no DPI scaling
                            int x = (btn.Width - outerDiameter) / 2;
                            int y = (btn.Height - outerDiameter) / 2;

                            // Draw outer ring (red)
                            using (var redBrush = new SolidBrush(Color.Red))
                            {
                                e.Graphics.FillEllipse(redBrush, x, y, outerDiameter, outerDiameter);
                            }

                            // Draw inner circle (white background, creating ring effect)
                            int innerDiameter = outerDiameter - (ringThickness * 2);
                            int innerX = x + ringThickness;
                            int innerY = y + ringThickness;
                            e.Graphics.FillEllipse(new SolidBrush(btn.BackColor), innerX, innerY, innerDiameter, innerDiameter);

                            // Draw center small dot - fixed size, no DPI scaling
                            int centerDiameter = innerDiameter - 12; // Reduce center dot size, maintain proportional coordination
                            int centerX = (btn.Width - centerDiameter) / 2;
                            int centerY = (btn.Height - centerDiameter) / 2;
                            e.Graphics.FillEllipse(brush, centerX, centerY, centerDiameter, centerDiameter);
                        }
                    }
                }
                else if (text == "✓") // Checkmark symbol
                {
                    // Draw green checkmark image instead of text
                    DrawGreenCheckmark(e.Graphics, btn.Width, btn.Height);
                }
                else // Other characters, use text drawing
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
            // Try to load custom image first
            try
            {
                string imagePath = Path.Combine(Application.StartupPath, "Resources", "checkmark.png");
                if (File.Exists(imagePath))
                {
                    using (var image = Image.FromFile(imagePath))
                    {
                        // Calculate center position
                        int x = (width - image.Width) / 2;
                        int y = (height - image.Height) / 2;

                        // Ensure image doesn't exceed button boundaries
                        if (image.Width <= width && image.Height <= height)
                        {
                            g.DrawImage(image, x, y, image.Width, image.Height);
                        }
                        else
                        {
                            // Scale down if image is too large
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
                // If image loading fails, continue with green block
                System.Diagnostics.Debug.WriteLine($"Failed to load checkmark image: {ex.Message}");
            }

            // If no image found or loading failed, use green block as fallback
            using (var brush = new SolidBrush(Color.Green))
            {
                // Draw centered green block
                int squareSize = Math.Min(width, height) / 2;
                int x = (width - squareSize) / 2;
                int y = (height - squareSize) / 2;
                g.FillRectangle(brush, x, y, squareSize, squareSize);
            }
        }

        private void DrawRecordButton(Graphics g, int width, int height)
        {
            // Try to load custom image first
            try
            {
                string imagePath = Path.Combine(Application.StartupPath, "Resources", "record_button.png");
                if (File.Exists(imagePath))
                {
                    using (var image = Image.FromFile(imagePath))
                    {
                        // Calculate center position
                        int x = (width - image.Width) / 2;
                        int y = (height - image.Height) / 2;

                        // Ensure image doesn't exceed button boundaries
                        if (image.Width <= width && image.Height <= height)
                        {
                            g.DrawImage(image, x, y, image.Width, image.Height);
                        }
                        else
                        {
                            // Scale down if image is too large
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
                // If image loading fails, continue with red dot
                System.Diagnostics.Debug.WriteLine($"Failed to load record button image: {ex.Message}");
            }

            // If no image found or loading failed, use red dot as fallback
            using (var brush = new SolidBrush(Color.Red))
            {
                // Draw centered red dot
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
                btn.BackColor = Color.LightGray; // Background turns gray on hover
            }
        }

        private void btnToggleRecord_MouseLeave(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.White; // Restore white on leave
            }
        }

        private void btnSettings_MouseEnter(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.DarkBlue; // Background becomes dark blue on hover
                btn.ForeColor = Color.White; // Gear text becomes white on hover
            }
        }

        private void btnSettings_MouseLeave(object? sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                btn.BackColor = Color.White; // Restore white background on leave
                btn.ForeColor = SystemColors.ControlText; // Restore system default text color on leave
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
            // Set tray icon
            _trayIcon.Icon = this.Icon ?? SystemIcons.Application;
            _trayIcon.Visible = true;
            _trayIcon.Text = Localization.GetText("TrayIconText");

            // Create tray menu
            ContextMenuStrip trayMenu = new ContextMenuStrip();

            // Show panel menu item
            ToolStripMenuItem showItem = new ToolStripMenuItem(Localization.GetText("ShowPanel"));
            showItem.Click += (sender, e) => ShowMainForm();
            trayMenu.Items.Add(showItem);

            // Separator
            trayMenu.Items.Add(new ToolStripSeparator());

            // Exit menu item
            ToolStripMenuItem exitItem = new ToolStripMenuItem(Localization.GetText("Exit"));
            exitItem.Click += (sender, e) => ExitApplication();
            trayMenu.Items.Add(exitItem);

            // Set tray icon context menu
            _trayIcon.ContextMenuStrip = trayMenu;

            // Set tray icon click events
            _trayIcon.MouseClick += TrayIcon_MouseClick;
            _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;
        }

        // Show main form
        private void ShowMainForm()
        {
            _allowVisible = true;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        // Exit application
        private void ExitApplication()
        {
            _allowClose = true;
            this.Close();
        }

        // Tray icon single click event
        private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainForm();
            }
        }

        // Tray icon double click event
        private void TrayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            ShowMainForm();
        }

        // Override OnResize method to handle minimize to taskbar
        protected override void OnResize(EventArgs e)
        {
            // When window is minimized, normally minimize to taskbar
            base.OnResize(e);
        }

        // Override OnFormClosing method to control form closing
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Distinguish closing reasons:
            // If user clicks close button, hide window but don't exit
            // If other reasons (like system shutdown), exit normally
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                // User clicked close button [X], hide window but keep tray icon
                e.Cancel = true;
                this.Hide(); // Hide window instead of minimize

                // Show tray balloon tip
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

        // Exit menu item click event
        private void exitToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            ExitApplication();
        }

        private void listBox_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void trackTileSize_Scroll(object sender, EventArgs e)
        {

        }

        private void lblTileSizeUnit_Click(object sender, EventArgs e)
        {

        }

        private void lblTileSizeValue_Click(object sender, EventArgs e)
        {

        }

        private void lblInfo_TextChanged(object? sender, EventArgs e)
        {
            AdjustStatusLabelProperties(); // Re-adjust label size when text changes
        }

        // Safely update status label text, ensuring thread safety and null handling
        private void SafeUpdateStatusText(string text)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SafeUpdateStatusText(text)));
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    text = Localization.GetText("StatusStopped");
                }

                lblInfo.Text = text;
                Log($"Status updated: {text}");
            }
            catch (Exception ex)
            {
                Log($"Status text update failed: {ex.Message}");
                // As fallback, set to default stopped state
                lblInfo.Text = Localization.GetText("StatusStopped");
            }
        }

        // Update localized texts
        private void UpdateLocalizedTexts()
        {
            // Update window title
            this.Text = Localization.GetText("WindowTitle");

            // Update label texts
            lblPixelDelta.Text = Localization.GetText("PixelColorDiff");
            lblTileSize.Text = Localization.GetText("DetectInterval");
            lblToggleHotkey.Text = Localization.GetText("ToggleHotkey");
            lblTileSizeUnit.Text = Localization.GetText("Pixels");
            btnHelpPixelDelta.Text = Localization.GetText("QuestionMark");
            lblDisplay.Text = Localization.GetText("DisplaySelection");

            // Update button texts
            btnStart.Text = Localization.GetText("Start");
            btnStop.Text = Localization.GetText("Stop");

            // Safely update status label - handle empty text and unexpected values
            try
            {
                string currentText = lblInfo.Text ?? "";

                // If current text is empty, set to default stopped state
                if (string.IsNullOrEmpty(currentText))
                {
                    lblInfo.Text = Localization.GetText("StatusStopped");
                    Log("Detected empty status text, reset to stopped state");
                }
                else
                {
                    // Update status label - use safer conversion logic
                    string newText = currentText switch
                    {
                        "Status: Stopped" => Localization.GetText("StatusStopped"),
                        "Status: Running" => Localization.GetText("StatusRunning"),
                        "Status: Initializing GPU capture..." => Localization.GetText("StatusInitializing"),
                        "Status: Failed" => Localization.GetText("StatusFailed"),
                        _ => currentText // Keep original text to avoid data loss
                    };

                    // Only update if text actually needs updating
                    if (newText != currentText && !string.IsNullOrEmpty(newText))
                    {
                        lblInfo.Text = newText;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Localized status text update failed: {ex.Message}");
                // As fallback, set to default stopped state
                lblInfo.Text = Localization.GetText("StatusStopped");
            }

            // Adjust status label properties to support word wrap
            AdjustStatusLabelProperties();
        }

        // Ensure status label can correctly display word wrap
        private void AdjustStatusLabelProperties()
        {
            try
            {
               Log($"AdjustStatusLabelProperties: Starting adjustment - panelBottom.Width={panelBottom.Width}, lblInfo.Text='{lblInfo.Text}'");

              // Ensure label is visible and has width
              lblInfo.Visible = true;
              lblInfo.AutoSize = true;

              // Declare variable for text measurement
              Size textSize;

              // Ensure width is not 0
              int labelWidth = panelBottom.Width - 10;
              Log($"AdjustStatusLabelProperties: panelBottom.Width={panelBottom.Width}, labelWidth={labelWidth}");
              if (labelWidth <= 0)
              {
                  // If panelBottom width is invalid, try using window width
                  labelWidth = this.ClientSize.Width - 20;
                  Log($"AdjustStatusLabelProperties: this.ClientSize.Width={this.ClientSize.Width}, labelWidth={labelWidth}");
                  if (labelWidth <= 0)
                  {
                      // If window width is also invalid, calculate minimum width required by text
                      textSize = TextRenderer.MeasureText(lblInfo.Text, lblInfo.Font);
                      labelWidth = textSize.Width + 20; // Add 20 pixels margin
                      Log($"AdjustStatusLabelProperties: Window width also invalid, using text minimum width {labelWidth}");
                  }
                  Log($"AdjustStatusLabelProperties: panelBottom.Width={panelBottom.Width} invalid, using window width {labelWidth}");
              }
              
              // Calculate actual size of text at specified width (including line breaks and line spacing)
              textSize = TextRenderer.MeasureText(lblInfo.Text, lblInfo.Font, new Size(labelWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
              
              // Calculate extra margin based on font size to ensure second line displays completely
              int extraMargin = (int)(lblInfo.Font.Size * 1.5); // Margin is 1.5 times font size
              
              // Set maximum width and height of the label
              lblInfo.MaximumSize = new Size(labelWidth, int.MaxValue); // Allow height to auto-adjust
              lblInfo.Size = new Size(labelWidth, textSize.Height + extraMargin); // Add dynamically calculated margin
              
              // Ensure panel bottom has enough space to display entire label
              if (panelBottom.Height < lblInfo.Height + 10) // Panel bottom needs additional 10 pixels margin
              {
                  panelBottom.Height = lblInfo.Height + 10;
                  Log($"AdjustStatusLabelProperties: Adjusted panelBottom.Height to {panelBottom.Height}");
              }
              
              // Adjust log list position to ensure it's not covered by status label
              listBox.Location = new Point(listBox.Location.X, lblInfo.Height + 5); // Log list is 5 pixels below status label
              listBox.Size = new Size(listBox.Size.Width, panelBottom.Height - lblInfo.Height - 10); // Adjust log list height to ensure it doesn't exceed panel bottom
              
              Log($"AdjustStatusLabelProperties: Final labelWidth={labelWidth}, labelHeight={lblInfo.Height}");

              // Add log information to check control properties
              Log($"AdjustStatusLabelProperties: lblInfo.ForeColor={lblInfo.ForeColor}, lblInfo.BackColor={lblInfo.BackColor}");
              Log($"AdjustStatusLabelProperties: lblInfo.TextAlign={lblInfo.TextAlign}, lblInfo.Font={lblInfo.Font}");
              Log($"AdjustStatusLabelProperties: lblInfo.Location={lblInfo.Location}, lblInfo.Size={lblInfo.Size}");
              Log($"AdjustStatusLabelProperties: lblInfo.Visible={lblInfo.Visible}, lblInfo.Enabled={lblInfo.Enabled}");

                // Handle empty text case
                if (string.IsNullOrEmpty(lblInfo.Text))
                {
                    lblInfo.Text = Localization.GetText("StatusStopped");
                    Log("AdjustStatusLabelProperties: Detected empty text, reset to stopped state");
                }

                // Force panel to re-layout
                panelBottom.PerformLayout();
            }
            catch (Exception ex)
            {
                Log($"Failed to adjust status label properties: {ex.Message}");
                // Ensure at least a reasonable default height
                lblInfo.Height = 25;
                Log($"Failed to adjust status label properties, set default height 25");
            }
        }
    }
}