using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using static Vortice.Direct3D11.D3D11;

namespace TbEinkSuperFlushTurbo
{
    // D3D11_DRIVER_TYPE corresponding enum (Win32 native)
    enum DriverType : uint
    {
        Unknown = 0,
        Hardware = 1,
        Reference = 2,
        NullDriver = 3,
        Software = 4,
        Warp = 5
    }

    // Bounding area configuration structure, multiple adjacent tile bounding areas for suppressing scrolling refresh
    public struct BoundingAreaConfig
    {
        public int Width;       // Width of each bounding area (in tiles)
        public int Height;      // Height of each bounding area (in tiles)
        public int HistoryFrames; // Number of history frames
        public int ChangeThreshold; // Change threshold
        public int RefreshBlockThreshold; // Tile count threshold required to determine bounding area refresh

        public BoundingAreaConfig(int width, int height, int historyFrames, int changeThreshold, int refreshBlockThreshold)
        {
            Width = width;
            Height = height;
            HistoryFrames = historyFrames;
            ChangeThreshold = changeThreshold;
            RefreshBlockThreshold = refreshBlockThreshold;
        }
    }

    public class D3DCaptureAndCompute : IDisposable
    {
        public int TileSize { get; set; } // Tile side length, in pixels
        public int PixelDelta { get; set; } // Per-component threshold
        public uint AverageWindowSize { get; set; } // Average window size (in frames)
        public uint StableFramesRequired { get; set; } // Number of stable frames, balancing response speed and stability
        public uint AdditionalCooldownFrames { get; set; } // Additional cooldown frames to avoid excessive refresh
        public uint FirstRefreshExtraDelay { get; set; } // Extra delay frames for first refresh, used for -1 state tiles
        public int CaretCheckInterval { get; set; } // Text cursor check interval (ms)
        public int ImeCheckInterval { get; set; } // IME window check interval (ms)
        public int MouseExclusionRadiusFactor { get; set; } // Mouse exclusion area radius factor
        public uint ProtectionFrames { get; set; } // Protection period frames passed from MainForm

        // Bounding area configuration
        public BoundingAreaConfig BoundingArea { get; set; }

        public int ScreenWidth => _screenW;
        public int ScreenHeight => _screenH;
        public int TilesX => _tilesX;
        public int TilesY => _tilesY;

        // Added logical resolution properties
        public int LogicalScreenWidth => (int)(_screenW / _dpiScaleX);
        public int LogicalScreenHeight => (int)(_screenH / _dpiScaleY);
        public float DpiScaleX => _dpiScaleX;
        public float DpiScaleY => _dpiScaleY;

        // D3D objects
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _deskDup;
        private int _screenW, _screenH;
        private int _tilesX, _tilesY;
        private Format _screenFormat; // Stores the actual format of the screen

        // DPI and scaling related
        private float _dpiScaleX = 1.0f;
        private float _dpiScaleY = 1.0f;
        private float _dpiX = 96.0f;
        private float _dpiY = 96.0f;

        // Index of the selected display
        private int _targetScreenIndex = 0;

        // Screen textures
        private ID3D11Texture2D? _gpuTexCurr;
        private ID3D11Texture2D? _gpuTexPrev;

        // Format detection
        private bool _formatDetected = false;
        private Format _actualDesktopFormat = Format.B8G8R8A8_UNorm;

        // State buffer (core logic) - now each tile stores 4 uints of historical differences
        private ID3D11Buffer? _tileStateIn;  // u0: Previous frame state (input)
        private ID3D11UnorderedAccessView? _tileStateInUAV;
        private ID3D11Buffer? _tileStateOut; // u1: Current frame new state (output)
        private ID3D11UnorderedAccessView? _tileStateOutUAV;

        // Refresh list (output)
        private ID3D11Buffer? _refreshList; // u2: List of tile indices that need refresh
        private ID3D11UnorderedAccessView? _refreshListUAV;
        private ID3D11Buffer? _refreshCounter; // u3: Atomic counter for refresh list
        private ID3D11UnorderedAccessView? _refreshCounterUAV;
        private ID3D11Buffer? _refreshListReadback; // For reading refresh list from GPU
        private ID3D11Buffer? _refreshCounterReadback; // For reading counter from GPU
        private ID3D11Buffer? _tileStateInReadback; // For reading tile state from GPU

        // Brightness data buffer
        private ID3D11Buffer? _tileBrightness; // u4: Tile brightness data
        private ID3D11UnorderedAccessView? _tileBrightnessUAV;
        private ID3D11Buffer? _tileBrightnessReadback; // For reading brightness data from GPU

        // GPU-side state management buffers
        private ID3D11Buffer? _tileStableCountersBuffer; // u5
        private ID3D11UnorderedAccessView? _tileStableCountersUAV;
        private ID3D11Buffer? _tileProtectionExpiryBuffer; // u6
        private ID3D11UnorderedAccessView? _tileProtectionExpiryUAV;

        // Bounding area history frame buffer
        private ID3D11Buffer? _boundingAreaHistoryBuffer; // u7

        // --- Scroll suppression related resources ---
        private ID3D11Buffer? _boundingAreaTileChangeCountBuffer; // u7 (GPU-side counter)
        private ID3D11UnorderedAccessView? _boundingAreaTileChangeCountUAV;
        private ID3D11Buffer? _boundingAreaTileChangeCountReadback; // Readback for the counter
        private uint[]? _boundingAreaHistory_cpu; // CPU-side history for logic
        private int _boundingAreaCount;

        private ID3D11ComputeShader? _computeShader;
        private ID3D11Buffer? _paramBuffer;

        private Action<string>? _debugLogger; // Field to store the logger
        private bool _enableDetailedDebugLogs = false; // Controls whether to print detailed DEBUG logs

        // Async operation synchronization control
        private readonly SemaphoreSlim _captureSemaphore = new SemaphoreSlim(1, 1); // Prevent concurrent capture
        private int _isCapturing = 0; // Atomic flag to prevent reentry

        // DXGI capture optimization related
        private int _consecutiveTimeouts = 0; // Consecutive timeout count
        private int _consecutiveFailures = 0; // Consecutive failure count
        private int _captureAttemptCount = 0; // Capture attempt count
        private int _captureSuccessCount = 0; // Capture success count
        private DateTime _lastSuccessfulCapture = DateTime.MinValue; // Last successful capture time
        private int _baseTimeoutMs = 100; // Base timeout time
        private int _maxTimeoutMs = 500; // Maximum timeout time

        // Eink screen compatibility support
        private bool _useGdiCapture = false; // Whether to use GDI+ capture
        private bool _isEinkScreen = false; // Whether it is an eink screen
        private bool _forceDirectXCapture; // Whether to force DirectX capture (passed from MainForm)
        private double _detectedRefreshRate = 60.0; // Detected refresh rate
        private Bitmap? _gdiBitmap; // GDI+ bitmap for screen capture
        private Graphics? _gdiGraphics; // GDI+ graphics object
        private Rectangle _screenBounds; // Screen bounds

        private bool _isFirstFrame = true; // Flag to handle the first frame capture
        private bool _needsTextureRecreate = false; // Flag to indicate texture recreation is needed

        // Mouse and IME related
        private Point _lastMousePosition = new Point(-1, -1);
        private Rectangle _lastImeRect = Rectangle.Empty;
        private DateTime _lastImeCheck = DateTime.MinValue;
        // Added text cursor related fields
        private Point _lastCaretPosition = new Point(-1, -1);
        private DateTime _lastCaretCheck = DateTime.MinValue;
        private IntPtr _lastFocusWindow = IntPtr.Zero;
        // Added GUI thread information related fields
        private uint _lastGuiThread = 0;
        private Point _lastGuiCaretPosition = new Point(-1, -1);
        private DateTime _lastGuiCaretCheck = DateTime.MinValue;

        public D3DCaptureAndCompute(Action<string>? debugLogger, BoundingAreaConfig boundingArea, bool forceDirectXCapture = true) // Constructor now accepts a logger
        {
            _debugLogger = debugLogger;
            _forceDirectXCapture = forceDirectXCapture;
            BoundingArea = boundingArea; // Use configuration passed from MainForm
            Console.WriteLine("=== D3DCaptureAndCompute Constructor Started ===");
            _debugLogger?.Invoke("=== D3DCaptureAndCompute Constructor Started ===");
        }

        public D3DCaptureAndCompute(Action<string>? debugLogger, int tileSize, int pixelDelta, uint averageWindowSize, uint stableFramesRequired, uint additionalCooldownFrames, uint firstRefreshExtraDelay, int caretCheckInterval, int imeCheckInterval, int mouseExclusionRadiusFactor, BoundingAreaConfig boundingArea, bool forceDirectXCapture = true, uint protectionFrames = 0, int targetScreenIndex = 0) // Constructor with parameters
        {
            _debugLogger = debugLogger;
            TileSize = tileSize;
            PixelDelta = pixelDelta;
            AverageWindowSize = averageWindowSize;
            StableFramesRequired = stableFramesRequired;
            AdditionalCooldownFrames = additionalCooldownFrames;
            FirstRefreshExtraDelay = firstRefreshExtraDelay;
            CaretCheckInterval = caretCheckInterval;
            ImeCheckInterval = imeCheckInterval;
            MouseExclusionRadiusFactor = mouseExclusionRadiusFactor;
            _forceDirectXCapture = forceDirectXCapture;
            BoundingArea = boundingArea; // Use configuration passed from MainForm
            ProtectionFrames = protectionFrames;
            _targetScreenIndex = targetScreenIndex;

            Console.WriteLine("=== D3DCaptureAndCompute Constructor Started ===");
            _debugLogger?.Invoke("=== D3DCaptureAndCompute Constructor Started ===");

            // Detect system DPI settings (based on selected display)
            DetectSystemDpiSettings();

            // Create independent log file for debugging
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
                else
                    // Delete all D3D log files except the latest one
                    CleanupOldD3DLogFiles(logDir);
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"Failed to create debug log: {logEx.Message}");
            }

            try
            {
                _debugLogger?.Invoke("Creating D3D11 device...");
                var creationFlags = DeviceCreationFlags.BgraSupport;
                _debugLogger?.Invoke($"Device creation flags: {creationFlags}");
                Console.WriteLine($"Creating D3D11 device with flags: {creationFlags}");

                var result = D3D11CreateDevice(null, (Vortice.Direct3D.DriverType)DriverType.Hardware, creationFlags, Array.Empty<FeatureLevel>(), out _device, out _context);
                _debugLogger?.Invoke($"D3D11CreateDevice result: {result.Success}, HRESULT: 0x{result.Code:X8}");
                _debugLogger?.Invoke($"Result description: {result.Description}");

                if (!result.Success)
                {
                    throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Description} (0x{result.Code:X8})");
                }

                if (_device == null)
                {
                    throw new InvalidOperationException("_device is null after successful creation");
                }

                if (_context == null)
                {
                    throw new InvalidOperationException("_context is null after successful creation");
                }

                _debugLogger?.Invoke("D3D11 device and context created successfully");
                _debugLogger?.Invoke("Device created successfully");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Exception during D3D device creation: {ex.GetType().Name}: {ex.Message}");
                _debugLogger?.Invoke($"Exception HRESULT: 0x{ex.HResult:X8}");
                _debugLogger?.Invoke($"Exception StackTrace: {ex.StackTrace}");
                throw;
            }

            try
            {
                _debugLogger?.Invoke("Getting DXGI device and adapter...");
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                _debugLogger?.Invoke("DXGI device obtained successfully");

                using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
                _debugLogger?.Invoke("DXGI adapter obtained successfully");
                _debugLogger?.Invoke($"Adapter description: {adapter.Description}");

                // Get all outputs and log their details
                _debugLogger?.Invoke("Enumerating outputs...");
                var allOutputs = GetAllOutputs(adapter);
                if (allOutputs.Count == 0)
                {
                    throw new InvalidOperationException("No outputs found on adapter");
                }
                _debugLogger?.Invoke($"DEBUG: Found {allOutputs.Count} outputs.");
                for (int i = 0; i < allOutputs.Count; i++)
                {
                    _debugLogger?.Invoke($"Processing output {i}...");
                    var output = allOutputs[i];

                    try
                    {
                        var outputDesc = output.Description;
                        int width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
                        int height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
                        // Only print detailed output information in verbose mode
                        if (_enableDetailedDebugLogs)
                        {
                            _debugLogger?.Invoke($"DEBUG: Output {i}: Name='{outputDesc.DeviceName}', Physical Resolution: {width}x{height}");
                        }

                        // Get display friendly name
                        string friendlyName = GetFriendlyDisplayName(outputDesc.DeviceName);
                        if (!string.IsNullOrEmpty(friendlyName))
                        {
                            // Only print friendly name in verbose mode
                            if (_enableDetailedDebugLogs)
                            {
                                _debugLogger?.Invoke($"DEBUG: Output {i}: Friendly Name='{friendlyName}'");
                            }
                        }

                        // Calculate logical resolution (if applicable)
                        float logicalWidth = width / _dpiScaleX;
                        float logicalHeight = height / _dpiScaleY;
                        // Only print logical resolution information in verbose mode
                        if (_enableDetailedDebugLogs)
                        {
                            _debugLogger?.Invoke($"DEBUG: Output {i}: Logical Resolution (approx): {logicalWidth:F0}x{logicalHeight:F0} (based on DPI scale {_dpiScaleX:F2}x{_dpiScaleY:F2})");
                        }

                        // Try to get display mode list, but handle possible failures
                        try
                        {
                            _debugLogger?.Invoke($"Getting display mode list for output {i} with Format.B8G8R8A8_UNorm...");
                            var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);
                            int modeCount = 0;
                            foreach (var item in displayModeList)
                            {
                                modeCount++;
                            }
                            _debugLogger?.Invoke($"Successfully obtained display mode list for output {i}, total {modeCount} modes");

                            // Only print all display modes when detailed logs are enabled
                            if (_enableDetailedDebugLogs)
                            {
                                _debugLogger?.Invoke("Detailed display mode information:");
                                foreach (var mode in displayModeList)
                                {
                                    double refreshRate = (double)mode.RefreshRate.Numerator / mode.RefreshRate.Denominator;
                                    _debugLogger?.Invoke($"DEBUG:   Mode: {mode.Width}x{mode.Height}@{refreshRate:F2}Hz, Format:{mode.Format}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"Exception during GetDisplayModeList for output {i}: {ex.GetType().Name}: {ex.Message}");
                            _debugLogger?.Invoke($"Exception HRESULT: 0x{ex.HResult:X8}");
                            _debugLogger?.Invoke($"Exception StackTrace: {ex.StackTrace}");
                        }

                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"DEBUG: Failed to get description for output {i}: {ex.GetType().Name}: {ex.Message}");
                        _debugLogger?.Invoke($"DEBUG: Exception HRESULT: 0x{ex.HResult:X8}");
                    }
                }

                // Use system default order (Screen.AllScreens order), do not specially handle primary display
                // To match Windows Forms' Screen.AllScreens order, we sort according to Screen.AllScreens' device order
                var systemScreens = System.Windows.Forms.Screen.AllScreens;
                var sortedOutputs = new List<IDXGIOutput>();

                // Sort DXGI outputs according to Screen.AllScreens order
                foreach (var screen in systemScreens)
                {
                    var screenDeviceName = screen.DeviceName;
                    var matchingOutput = allOutputs.FirstOrDefault(output =>
                    {
                        try
                        {
                            return output.Description.DeviceName == screenDeviceName;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (matchingOutput != null)
                    {
                        sortedOutputs.Add(matchingOutput);
                    }
                }

                // Add any unmatched DXGI outputs (just in case)
                foreach (var output in allOutputs)
                {
                    if (!sortedOutputs.Contains(output))
                    {
                        sortedOutputs.Add(output);
                    }
                }

                // Add debug information to verify that DXGI outputs match Screen.AllScreens order
                _debugLogger?.Invoke($"DEBUG: Windows Forms Screen.AllScreens order:");
                for (int i = 0; i < systemScreens.Length; i++)
                {
                    _debugLogger?.Invoke($"  Screen [{i}]: {systemScreens[i].DeviceName}, Primary: {systemScreens[i].Primary}, Bounds: {systemScreens[i].Bounds}");
                }

                _debugLogger?.Invoke($"DEBUG: DXGI found {allOutputs.Count} outputs:");
                for (int i = 0; i < allOutputs.Count; i++)
                {
                    try
                    {
                        var desc = allOutputs[i].Description;
                        _debugLogger?.Invoke($"  DXGI output [{i}]: {desc.DeviceName}, Coordinates: {desc.DesktopCoordinates}");
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"  DXGI output [{i}]: Failed to get detailed information: {ex.Message}");
                    }
                }

                _debugLogger?.Invoke($"DEBUG: Matched DXGI output order (by Screen.AllScreens order):");
                for (int i = 0; i < sortedOutputs.Count; i++)
                {
                    try
                    {
                        var desc = sortedOutputs[i].Description;
                        _debugLogger?.Invoke($"  Matched [{i}]: {desc.DeviceName}, Coordinates: {desc.DesktopCoordinates}");
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"  Matched [{i}]: Failed to get detailed information: {ex.Message}");
                    }
                }

                // Select output based on target screen index
                int selectedScreenIndex = _targetScreenIndex;
                _debugLogger?.Invoke($"DEBUG: Target screen index parameter: {selectedScreenIndex}, Available outputs: {sortedOutputs.Count}");

                if (selectedScreenIndex < 0 || selectedScreenIndex >= sortedOutputs.Count)
                {
                    _debugLogger?.Invoke($"DEBUG: Invalid target screen index {selectedScreenIndex}, defaulting to primary screen (index 0).");
                    selectedScreenIndex = 0;
                }

                var selectedOutput = sortedOutputs[selectedScreenIndex];
                _debugLogger?.Invoke($"DEBUG: Selected output {selectedScreenIndex} for duplication.");

                // Print selected output and corresponding display information
                try
                {
                    var desc = selectedOutput.Description;
                    _debugLogger?.Invoke($"DEBUG: Selected output details - Device: {desc.DeviceName}, Coordinates: {desc.DesktopCoordinates}");
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"DEBUG: Failed to get selected output details: {ex.Message}");
                }

                // Re-detect DPI settings to ensure correct DPI for the selected display
                _debugLogger?.Invoke($"Re-detecting DPI settings for display {selectedScreenIndex}...");
                DetectSystemDpiSettings();

                // Detect if it's an eink screen
                _isEinkScreen = DetectEinkScreen(selectedOutput);

                // If DirectX capture is not forced, prioritize GDI+ capture.
                if (!_forceDirectXCapture)
                {
                    _debugLogger?.Invoke("Non-forced DirectX capture mode, will prioritize GDI+ capture");

                    // Directly use GDI+ capture, avoiding DuplicateOutput call
                    if (InitializeGdiCapture(selectedOutput))
                    {
                        _useGdiCapture = true;
                        _debugLogger?.Invoke("Switched to GDI+ capture mode");
                    }
                    else
                    {
                        _debugLogger?.Invoke("GDI+ capture initialization failed, will try DirectX desktop duplication");
                    }
                }

                // If GDI+ capture failed, try DirectX desktop duplication
                if (!_useGdiCapture)
                {
                    if (_forceDirectXCapture)
                    {
                        _debugLogger?.Invoke("Forcing DirectX capture mode .");
                    }

                    _debugLogger?.Invoke("Getting IDXGIOutput1 interface...");
                    using var output1 = selectedOutput.QueryInterface<IDXGIOutput1>();
                    _debugLogger?.Invoke("IDXGIOutput1 interface obtained successfully");

                    _debugLogger?.Invoke("Attempting to create desktop duplication...");
                    _debugLogger?.Invoke($"Device pointer: 0x{_device.NativePointer:X16}");

                    try
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        _deskDup = output1.DuplicateOutput(_device);
                        watch.Stop();

                        _debugLogger?.Invoke($"DuplicateOutput completed in {watch.ElapsedMilliseconds}ms");
                        _debugLogger?.Invoke($"Desktop duplication object created: 0x{_deskDup?.NativePointer:X16}");

                        if (_deskDup == null)
                        {
                            throw new InvalidOperationException("DuplicateOutput returned null object");
                        }

                        var desc = selectedOutput.Description;

                        // In DXGI mode, directly use DesktopCoordinates to ensure consistent image size with DXGI capture
                        _screenW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                        _debugLogger?.Invoke($"DEBUG: Using desktop coordinates for DXGI: {_screenW}x{_screenH}");
                        
                        // Cross-validation: Compare with System.Windows.Forms.Screen
                        try
                        {
                            var systemScreens2 = System.Windows.Forms.Screen.AllScreens;
                            var matchingScreen = systemScreens2.FirstOrDefault(s => s.DeviceName == desc.DeviceName);
                            if (matchingScreen != null)
                            {
                                var screenBounds = matchingScreen.Bounds;
                                _debugLogger?.Invoke($"DEBUG: System.Windows.Forms.Screen bounds: {screenBounds.Width}x{screenBounds.Height}");
                                
                                // If there is a significant difference, use Screen's bounds as reference
                                if (Math.Abs(screenBounds.Width - _screenW) > 50 || Math.Abs(screenBounds.Height - _screenH) > 50)
                                {
                                    _debugLogger?.Invoke($"DEBUG: Significant difference detected between DXGI and Screen bounds");
                                    _debugLogger?.Invoke($"DEBUG: DXGI: {_screenW}x{_screenH}, Screen: {screenBounds.Width}x{screenBounds.Height}");
                                    
                                    // Record but don't modify immediately, let subsequent texture detection handle the actual size
                                }
                            }
                            else
                            {
                                _debugLogger?.Invoke($"DEBUG: No matching Screen found for device: {desc.DeviceName}");
                            }
                        }
                        catch (Exception screenEx)
                        {
                            _debugLogger?.Invoke($"DEBUG: Error validating with Screen bounds: {screenEx.Message}");
                        }
                        _debugLogger?.Invoke($"DEBUG: Desktop coordinates details - Left:{desc.DesktopCoordinates.Left}, Top:{desc.DesktopCoordinates.Top}, Right:{desc.DesktopCoordinates.Right}, Bottom:{desc.DesktopCoordinates.Bottom}");
                        _debugLogger?.Invoke($"DEBUG: Output device: {desc.DeviceName}");

                        _debugLogger?.Invoke($"DEBUG: Successfully created desktop duplication. Screen size: {_screenW}x{_screenH}");
                        _debugLogger?.Invoke($"Desktop coordinates: Left={desc.DesktopCoordinates.Left}, Top={desc.DesktopCoordinates.Top}, Right={desc.DesktopCoordinates.Right}, Bottom={desc.DesktopCoordinates.Bottom}");
                    }
                    catch (Exception ex)
                    {
                        // Provide more detailed error information
                        _debugLogger?.Invoke($"DEBUG: DuplicateOutput failed: {ex.GetType().Name}: {ex.Message}");
                        _debugLogger?.Invoke($"DEBUG: HRESULT: 0x{ex.HResult:X8}");
                        _debugLogger?.Invoke($"DEBUG: StackTrace: {ex.StackTrace}");

                        // Check device status
                        try
                        {
                            _debugLogger?.Invoke($"Device is valid: {_device.NativePointer != 0}");
                            _debugLogger?.Invoke($"Output is valid: {output1.NativePointer != 0}");
                        }
                        catch (Exception devEx)
                        {
                            _debugLogger?.Invoke($"Failed to check device/output validity: {devEx.Message}");
                        }

                        // If it's an eink screen or parameter error, try alternative methods
                        if (_isEinkScreen || ex.HResult == unchecked((int)0x80070057)) // E_INVALIDARG
                        {
                            _debugLogger?.Invoke("Detected eink screen or parameter error, trying alternative capture methods...");
                            if (TryAlternativeCaptureMethods(selectedOutput))
                            {
                                _debugLogger?.Invoke("Alternative capture method successful, continuing initialization");
                            }
                            else
                            {
                                throw new InvalidOperationException(@"Desktop duplication failed and alternative methods cannot be used. Possible reasons:
1. Incompatible with eink screen's special display mode
2. Device does not support desktop duplication
3. Requested format or resolution is not supported
4. Multi-monitor configuration issues", ex);
                            }
                        }
                        else if (ex.HResult == unchecked((int)0x80070005)) // E_ACCESSDENIED
                        {
                            throw new InvalidOperationException(@"Desktop duplication was denied. Possible reasons:
1. Another application is using desktop duplication
2. Program does not have sufficient permissions
3. Need to run as administrator", ex);
                        }
                        else if (ex.HResult == unchecked((int)0x887A0001)) // DXGI_ERROR_UNSUPPORTED
                        {
                            throw new InvalidOperationException("The current hardware or driver does not support desktop duplication functionality", ex);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Failed to create desktop duplication: {ex.Message} (HRESULT: 0x{ex.HResult:X8})");
                        }
                    }
                }
                else
                {
                    // Using GDI+ capture mode, set screen size
                    var desc = selectedOutput.Description;

                    // Try to get real physical resolution using EnumDisplaySettings
                    NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));

                    bool usePhysicalResolution = NativeMethods.EnumDisplaySettings(desc.DeviceName, -1, ref devMode);
                    if (usePhysicalResolution)
                    {
                        _screenW = devMode.dmPelsWidth;
                        _screenH = devMode.dmPelsHeight;
                        _debugLogger?.Invoke($"DEBUG (GDI): Using physical resolution from DEVMODE: {_screenW}x{_screenH}");
                    }
                    else
                    {
                        _screenW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                        _debugLogger?.Invoke($"DEBUG (GDI): DEVMODE failed, using desktop coordinates: {_screenW}x{_screenH}");
                    }

                    _debugLogger?.Invoke($"GDI+ capture mode, final screen size: {_screenW}x{_screenH}");
                }

                // Get actual format of desktop duplication
                // Get format information in the first successfully acquired frame
                _screenFormat = Format.B8G8R8A8_UNorm; // Use default format first
                _debugLogger?.Invoke($"DEBUG: Using default format: {_screenFormat}, will detect actual format on first frame");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Exception during DXGI setup: {ex.GetType().Name}: {ex.Message}");
                _debugLogger?.Invoke($"HRESULT: 0x{ex.HResult:X8}");
                throw;
            }

            _tilesX = (_screenW + TileSize - 1) / TileSize;
            _tilesY = (_screenH + TileSize - 1) / TileSize;
            int tileCount = _tilesX * _tilesY;

            _debugLogger?.Invoke($"Creating textures for screen {_screenW}x{_screenH}");

            // Create textures using detected screen format
            // Support more common desktop formats
            var texFormat = _screenFormat;
            bool needConversion = false;

            // Check if format conversion is needed
            switch (texFormat)
            {
                case Format.B8G8R8A8_UNorm:
                case Format.R8G8B8A8_UNorm:
                    // Directly supported
                    _debugLogger?.Invoke($"DEBUG: Format {texFormat} is directly supported");
                    break;
                default:
                    // For other formats, we need conversion
                    _debugLogger?.Invoke($"DEBUG: Format {_screenFormat} requires conversion, using B8G8R8A8_UNorm");
                    texFormat = Format.B8G8R8A8_UNorm;
                    needConversion = true;
                    break;
            }

            _debugLogger?.Invoke($"Creating texture with format: {texFormat}, size: {_screenW}x{_screenH}");
            var texDesc = new Texture2DDescription { Width = (uint)_screenW, Height = (uint)_screenH, MipLevels = 1, ArraySize = 1, Format = texFormat, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, CPUAccessFlags = CpuAccessFlags.None };

            try
            {
                _debugLogger?.Invoke("Creating _gpuTexCurr...");
                _gpuTexCurr = _device.CreateTexture2D(texDesc);
                _debugLogger?.Invoke($"_gpuTexCurr created successfully: 0x{_gpuTexCurr?.NativePointer:X16}");

                _debugLogger?.Invoke("Creating _gpuTexPrev...");
                _gpuTexPrev = _device.CreateTexture2D(texDesc);
                _debugLogger?.Invoke($"_gpuTexPrev created successfully: 0x{_gpuTexPrev?.NativePointer:X16}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Texture creation failed: {ex.GetType().Name}: {ex.Message}");
                _debugLogger?.Invoke($"Texture creation HRESULT: 0x{ex.HResult:X8}");
                _debugLogger?.Invoke($"Texture description: Width={texDesc.Width}, Height={texDesc.Height}, Format={texDesc.Format}");
                throw;
            }

            _debugLogger?.Invoke($"DEBUG: Created textures with format: {texFormat}, conversion needed: {needConversion}");

            // --- Create new GPU buffers ---
            // The shader uses RWStructuredBuffer<uint> with values per tile for history
            // The actual number of history frames is determined by the AverageWindowSize parameter passed from MainForm
            // Note: Currently supports up to 2-frame average window size (AVERAGE_WINDOW_SIZE in MainForm)
            const int historyElementSize = sizeof(uint); // sizeof(uint)
            int historyArraySize = 2; // Maximum supported history frames (must match shader)

            var stateBufferDesc = new BufferDescription
            {
                ByteWidth = (uint)(historyElementSize * tileCount * historyArraySize),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = historyElementSize
            };
            _tileStateIn = _device.CreateBuffer(stateBufferDesc);
            _tileStateOut = _device.CreateBuffer(stateBufferDesc);

            var refreshListDesc = new BufferDescription
            {
                ByteWidth = (uint)(sizeof(uint) * tileCount),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = sizeof(uint)
            };
            _refreshList = _device.CreateBuffer(refreshListDesc);

            var counterDesc = new BufferDescription
            {
                ByteWidth = sizeof(uint),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferAllowRawViews
            };
            _refreshCounter = _device.CreateBuffer(counterDesc);

            // --- Create UAVs ---
            var structuredUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)(tileCount * historyArraySize) } };
            _tileStateInUAV = _device.CreateUnorderedAccessView(_tileStateIn, structuredUavDesc);
            _tileStateOutUAV = _device.CreateUnorderedAccessView(_tileStateOut, structuredUavDesc);

            var refreshListUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)tileCount } };
            _refreshListUAV = _device.CreateUnorderedAccessView(_refreshList, refreshListUavDesc);

            var counterUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.R32_Typeless, Buffer = { FirstElement = 0, NumElements = 1, Flags = BufferUnorderedAccessViewFlags.Raw } };
            _refreshCounterUAV = _device.CreateUnorderedAccessView(_refreshCounter, counterUavDesc);

            // --- Create readback buffers ---
            var readbackDesc = new BufferDescription { Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None };
            _refreshListReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(sizeof(uint) * tileCount) });
            _refreshCounterReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = sizeof(uint) });
            _tileStateInReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(historyElementSize * tileCount * historyArraySize) });

            // --- Create brightness data buffers ---
            var brightnessBufferDesc = new BufferDescription
            {
                ByteWidth = (uint)(sizeof(float) * tileCount),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = sizeof(float)
            };
            _tileBrightness = _device.CreateBuffer(brightnessBufferDesc);

            var brightnessUavDesc = new UnorderedAccessViewDescription
            {
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Format = Format.Unknown,
                Buffer = { FirstElement = 0, NumElements = (uint)tileCount }
            };
            _tileBrightnessUAV = _device.CreateUnorderedAccessView(_tileBrightness, brightnessUavDesc);

            _tileBrightnessReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(sizeof(float) * tileCount) });

            // --- Create and initialize GPU-side state management buffers ---
            var countersDesc = new BufferDescription
            {
                ByteWidth = (uint)(sizeof(int) * tileCount),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = sizeof(int)
            };
            var initialCounters = new int[tileCount];
            Array.Fill(initialCounters, -1);
            var countersHandle = GCHandle.Alloc(initialCounters, GCHandleType.Pinned);
            try
            {
                var countersSubresourceData = new SubresourceData(countersHandle.AddrOfPinnedObject());
                _tileStableCountersBuffer = _device.CreateBuffer(countersDesc, countersSubresourceData);
            }
            finally
            {
                countersHandle.Free();
            }

            // Use uint2 (8 bytes) instead of long (8 bytes)
            var expiryDesc = new BufferDescription
            {
                ByteWidth = (uint)(8 * tileCount), // sizeof(uint2) = 8
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 8 // sizeof(uint2)
            };
            var initialExpiry = new uint[tileCount * 2]; // Each tile needs 2 uint values
            Array.Fill(initialExpiry, 0U);
            var expiryHandle = GCHandle.Alloc(initialExpiry, GCHandleType.Pinned);
            try
            {
                var expirySubresourceData = new SubresourceData(expiryHandle.AddrOfPinnedObject());
                _tileProtectionExpiryBuffer = _device.CreateBuffer(expiryDesc, expirySubresourceData);
            }
            finally
            {
                expiryHandle.Free();
            }

            var countersUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)tileCount } };
            _tileStableCountersUAV = _device.CreateUnorderedAccessView(_tileStableCountersBuffer, countersUavDesc);

            var expiryUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)tileCount } };
            _tileProtectionExpiryUAV = _device.CreateUnorderedAccessView(_tileProtectionExpiryBuffer, expiryUavDesc);

            // --- Create bounding area history frame buffers ---
            // Calculate the number of bounding areas
            int boundingAreaCountX = (_tilesX + BoundingArea.Width - 1) / BoundingArea.Width;
            int boundingAreaCountY = (_tilesY + BoundingArea.Height - 1) / BoundingArea.Height;
            _boundingAreaCount = boundingAreaCountX * boundingAreaCountY;

            var boundingAreaHistoryDesc = new BufferDescription
            {
                ByteWidth = (uint)(sizeof(uint) * _boundingAreaCount),
                Usage = ResourceUsage.Default, // Will be updated by CPU
                BindFlags = BindFlags.ShaderResource, // Readonly for shader
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = sizeof(uint)
            };
            _boundingAreaHistoryBuffer = _device.CreateBuffer(boundingAreaHistoryDesc);
            _boundingAreaHistory_cpu = new uint[_boundingAreaCount];

            // Create shader resource view (SRV) for bounding area history buffer
            var boundingAreaHistorySrvDesc = new ShaderResourceViewDescription(
                _boundingAreaHistoryBuffer,
                Format.Unknown,
                0,
                (uint)_boundingAreaCount);
            // Note: We don't create SRV here temporarily because it will be created during each rendering

            // --- Create and initialize bounding area single-frame change count buffer ---
            var boundingAreaCountDesc = new BufferDescription
            {
                ByteWidth = (uint)(sizeof(uint) * _boundingAreaCount),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = sizeof(uint)
            };
            _boundingAreaTileChangeCountBuffer = _device.CreateBuffer(boundingAreaCountDesc);
            var boundingAreaCountUavDesc = new UnorderedAccessViewDescription(_boundingAreaTileChangeCountBuffer, Format.Unknown, 0, (uint)_boundingAreaCount);
            _boundingAreaTileChangeCountUAV = _device.CreateUnorderedAccessView(_boundingAreaTileChangeCountBuffer, boundingAreaCountUavDesc);
            _boundingAreaTileChangeCountReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(sizeof(uint) * _boundingAreaCount) });

            // --- Initialize history difference state buffer ---
            _context!.ClearUnorderedAccessView(_tileStateInUAV!, new Vortice.Mathematics.Int4(0, 0, 0, 0));

            var shaderPath = Path.Combine(AppContext.BaseDirectory, "ComputeShader.hlsl");
            var csBlob = Compiler.CompileFromFile(shaderPath, "CSMain", "cs_5_0", ShaderFlags.None, EffectFlags.None);
            _debugLogger?.Invoke($"Shader compiled successfully, bytecode size: {csBlob.Span.Length} bytes");

            // Check if compiled bytecode is valid
            if (csBlob.Span.Length == 0)
            {
                throw new InvalidOperationException("Shader compilation resulted in empty bytecode");
            }

            _computeShader = _device.CreateComputeShader(csBlob.Span);

            // --- Constant buffer size must be a multiple of 16 ---
            // 17 uints = 68 bytes, need to round up to multiple of 16 -> 80 bytes (5 x 16-byte blocks).
            _paramBuffer = _device.CreateBuffer(new BufferDescription(80, BindFlags.ConstantBuffer));

            // Initialize _gpuTexPrev as zero texture to ensure first frame has valid reference frame
            if (_device != null && _gpuTexPrev != null)
            {
                using var clearView = _device.CreateRenderTargetView(_gpuTexPrev);
                _context?.ClearRenderTargetView(clearView, new Vortice.Mathematics.Color4(0, 0, 0, 0.5f));
            }

            _debugLogger?.Invoke("DEBUG: Initialized _gpuTexPrev with zero data");

            _debugLogger?.Invoke("=== D3DCaptureAndCompute Constructor Completed Successfully ===");
        }

        // Public method: Analyze recent frame data (this method now only outputs configuration info, no longer performs frame data analysis)
        public void AnalyzeRecentFrames()
        {
            try
            {
                _debugLogger?.Invoke("=== Starting analysis of recent frame data ===");

                // Output current configuration information
                _debugLogger?.Invoke($"TileSize: {TileSize}, PixelDelta: {PixelDelta}");
                _debugLogger?.Invoke($"Screen dimensions: {_screenW}x{_screenH}");
                _debugLogger?.Invoke($"Tile count: {_tilesX}x{_tilesY} = {_tilesX * _tilesY} tiles");
                _debugLogger?.Invoke($"Pixel difference threshold: {PixelDelta} (relative threshold: {(float)PixelDelta / 256f * 100f:F3}%)");
                _debugLogger?.Invoke($"Average window size (AverageWindowSize): {AverageWindowSize} frames");

                _debugLogger?.Invoke("=== Frame difference analysis completed ===");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Failed to analyze frame data: {ex.Message}");
            }
        }

        public async Task<(List<(int bx, int by)> tiles, float[] brightnessData)> CaptureAndComputeOnceAsync(uint frameCounter, CancellationToken token)
        {
            var result = new List<(int, int)>();

            try
            {
                // Prevent concurrent capture - use atomic operation for quick check
                if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
                {
                    _debugLogger?.Invoke("DEBUG: Capture operation is already in progress, skipping this call");
                    return (result, new float[_tilesX * _tilesY]);
                }
                {
                    // Use semaphore to ensure thread safety
                    if (!await _captureSemaphore.WaitAsync(0, token))
                    {
                        _debugLogger?.Invoke("DEBUG: Failed to acquire capture lock, skipping this call");
                        return (result, new float[_tilesX * _tilesY]);
                    }

                    token.ThrowIfCancellationRequested();

                    if (_useGdiCapture)
                    {
                        // Using GDI+ capture mode
                        _debugLogger?.Invoke("Using GDI+ capture mode...");

                        // Use Task.Run to move GDI+ operations to background thread
                        bool captureSuccess = await CaptureScreenWithGdiAsync();

                        if (!captureSuccess)
                        {
                            _debugLogger?.Invoke("GDI+ capture failed");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        _debugLogger?.Invoke("GDI+ capture succeeded");
                    }
                    else
                    {
                        // Using DirectX desktop duplication mode - add parameter validation and timeout handling
                        if (_deskDup == null)
                        {
                            _debugLogger?.Invoke($"ERROR: _deskDup is null, cannot acquire frame");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Validate device state - ensure device is still valid
                        if (_device == null || _context == null)
                        {
                            _debugLogger?.Invoke($"ERROR: D3D11 device or context is null");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Dynamic timeout handling - adjust timeout based on previous capture success rate
                        uint timeoutMs = (uint)GetOptimizedTimeout();
                        var acquireResult = _deskDup.AcquireNextFrame(timeoutMs, out var frameInfo, out IDXGIResource desktopResource);

                        if (!acquireResult.Success)
                        {
                            // More detailed error handling and update statistics
                            _consecutiveFailures++;

                            // Handle differently based on error code
                            if (acquireResult.Code < 0) // General error case
                            {
                                if ((uint)acquireResult.Code == 0x887A0027) // DXGI_ERROR_WAIT_TIMEOUT
                                {
                                    _consecutiveTimeouts++;
                                    _debugLogger?.Invoke($"WARNING: AcquireNextFrame timed out after {timeoutMs}ms (consecutive timeouts: {_consecutiveTimeouts})");
                                    // Timeout is not a fatal error, can continue trying
                                    return (result, new float[_tilesX * _tilesY]);
                                }
                                else if ((uint)acquireResult.Code == 0x887A0026) // DXGI_ERROR_ACCESS_LOST
                                {
                                    _debugLogger?.Invoke($"ERROR: Desktop duplication access lost. Need to reinitialize. (consecutive failures: {_consecutiveFailures})");
                                    // Access lost is a serious error, need to reinitialize
                                    return (result, new float[_tilesX * _tilesY]);
                                }
                                else
                                {
                                    _debugLogger?.Invoke($"ERROR: AcquireNextFrame failed with code: 0x{(uint)acquireResult.Code:X8} (consecutive failures: {_consecutiveFailures})");
                                    return (result, new float[_tilesX * _tilesY]);
                                }
                            }
                            else
                            {
                                _debugLogger?.Invoke($"ERROR: AcquireNextFrame failed with code: {acquireResult.Code} (consecutive failures: {_consecutiveFailures})");
                                return (result, new float[_tilesX * _tilesY]);
                            }
                        }

                        // Validate the acquired resources are valid
                        if (desktopResource == null)
                        {
                            _debugLogger?.Invoke($"ERROR: AcquireNextFrame returned null desktopResource");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();

                        // Validate texture object is valid
                        if (tex == null)
                        {
                            _debugLogger?.Invoke($"ERROR: Failed to query ID3D11Texture2D interface from desktopResource");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Log desktopResource description
                        var desktopTexDesc = tex.Description;
                        _debugLogger?.Invoke($"DEBUG: desktopResource acquired. W:{desktopTexDesc.Width}, H:{desktopTexDesc.Height}, Format:{desktopTexDesc.Format}");

                        // Texture size validation - check if acquired desktop texture size is valid
                        if (desktopTexDesc.Width <= 0 || desktopTexDesc.Height <= 0)
                        {
                            _debugLogger?.Invoke($"ERROR: Invalid desktop texture dimensions: {desktopTexDesc.Width}x{desktopTexDesc.Height}");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Check if texture dimensions match screen dimensions (allowing some tolerance)
                        const int maxDimensionDifference = 10; // Maximum allowed dimension difference
                        if (Math.Abs((int)desktopTexDesc.Width - _screenW) > maxDimensionDifference ||
                            Math.Abs((int)desktopTexDesc.Height - _screenH) > maxDimensionDifference)
                        {
                            _debugLogger?.Invoke($"WARNING: Desktop texture size mismatch. Expected: {_screenW}x{_screenH}, Got: {desktopTexDesc.Width}x{desktopTexDesc.Height}");
                            
                            // Dynamically adjust internal dimensions to match actual texture size
                            _debugLogger?.Invoke($"DEBUG: Adjusting internal dimensions to match actual desktop texture: {desktopTexDesc.Width}x{desktopTexDesc.Height}");
                            _screenW = (int)desktopTexDesc.Width;
                            _screenH = (int)desktopTexDesc.Height;
                            
                            // Recalculate tile count
                            _tilesX = (_screenW + TileSize - 1) / TileSize;
                            _tilesY = (_screenH + TileSize - 1) / TileSize;
                            _debugLogger?.Invoke($"DEBUG: Recalculated tiles: {_tilesX}x{_tilesY} for new dimensions");
                            
                            // Mark texture needs to be recreated
                            _needsTextureRecreate = true;
                        }

                        // First time detecting actual format - add format validation
                        if (!_formatDetected)
                        {
                            _actualDesktopFormat = desktopTexDesc.Format;
                            _formatDetected = true;
                            _debugLogger?.Invoke($"DEBUG: Actual desktop format detected: {_actualDesktopFormat}");

                            // Format validation - ensure desktop format is compatible with expected format
                            if (!IsValidDesktopFormat(_actualDesktopFormat))
                            {
                                _debugLogger?.Invoke($"ERROR: Unsupported desktop format: {_actualDesktopFormat}. Falling back to B8G8R8A8_UNorm");
                                _actualDesktopFormat = Format.B8G8R8A8_UNorm; // Fallback to safe format
                            }

                            // If actual format doesn't match texture format, or texture needs to be recreated (size changed)
                            if (_gpuTexCurr != null && (_actualDesktopFormat != _gpuTexCurr.Description.Format || _needsTextureRecreate))
                            {
                                _debugLogger?.Invoke($"DEBUG: Recreating textures with actual format: {_actualDesktopFormat}");

                                // Use Task.Run to move texture recreation to background thread
                                await Task.Run(() =>
                                {
                                    // Release old texture in thread-safe manner
                                    var oldTexCurr = _gpuTexCurr;
                                    var oldTexPrev = _gpuTexPrev;
                                    _gpuTexCurr = null;
                                    _gpuTexPrev = null;

                                    // Delayed release to avoid immediate memory pressure
                                    if (oldTexCurr != null)
                                    {
                                        Task.Delay(100).ContinueWith(_ => oldTexCurr.Dispose());
                                    }
                                    if (oldTexPrev != null)
                                    {
                                        Task.Delay(100).ContinueWith(_ => oldTexPrev.Dispose());
                                    }

                                    // Create new texture to match actual format
                                    var newTexDesc = new Texture2DDescription
                                    {
                                        Width = (uint)_screenW,
                                        Height = (uint)_screenH,
                                        MipLevels = 1,
                                        ArraySize = 1,
                                        Format = _actualDesktopFormat,
                                        SampleDescription = new SampleDescription(1, 0),
                                        Usage = ResourceUsage.Default,
                                        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                                        CPUAccessFlags = CpuAccessFlags.None
                                    };

                                    if (_device != null)
                                    {
                                        _gpuTexCurr = _device.CreateTexture2D(newTexDesc);
                                        _gpuTexPrev = _device.CreateTexture2D(newTexDesc);
                                    }

                                    // Reinitialize to black
                                    if (_device != null && _gpuTexPrev != null)
                                    {
                                        using var clearView = _device.CreateRenderTargetView(_gpuTexPrev);
                                        _context?.ClearRenderTargetView(clearView, new Vortice.Mathematics.Color4(0, 0, 0, 0.5f));
                                    }
                                }, token);
                                
                                // Reset texture recreation flag
                                _needsTextureRecreate = false;
                            }
                        }

                        _debugLogger?.Invoke($"DEBUG: Copying with format: {desktopTexDesc.Format}");

                        // Validate target texture is valid
                        if (_gpuTexCurr == null)
                        {
                            _debugLogger?.Invoke($"ERROR: _gpuTexCurr is null, cannot copy resource");
                            _consecutiveFailures++;
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        try
                        {
                            _context!.CopyResource(_gpuTexCurr, tex);

                            // Add GPU synchronization point to ensure copy operation completes
                            _context.Flush();

                            // Successfully copied, updating statistics
                            _captureSuccessCount++;
                            _lastSuccessfulCapture = DateTime.Now;
                            _consecutiveFailures = 0;
                            _consecutiveTimeouts = 0;

                            _debugLogger?.Invoke($"DEBUG: Resource copied successfully");
                        }
                        catch (Exception copyEx)
                        {
                            _debugLogger?.Invoke($"ERROR: Failed to copy resource: {copyEx.Message}");
                            _consecutiveFailures++;
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Safely release frame
                        try
                        {
                            _deskDup.ReleaseFrame();
                        }
                        catch (Exception releaseEx)
                        {
                            _debugLogger?.Invoke($"WARNING: Failed to release frame: {releaseEx.Message}");
                            // Frame release failure is not a fatal error, continue processing
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                // Print debug information about mouse, cursor, and IME position
                LogCursorPositionInfo();

                // Handle the first frame to prevent initial full refresh
                if (_isFirstFrame)
                {
                    _context?.CopyResource(_gpuTexPrev!, _gpuTexCurr!);
                    _context?.Flush();
                    _isFirstFrame = false;
                    _debugLogger?.Invoke("DEBUG: First frame captured. Initializing previous texture and skipping comparison.");
                    return (result, new float[_tilesX * _tilesY]); // Return empty list with brightness data
                }

                // 1. GPU Computation
                _context?.ClearUnorderedAccessView(_refreshCounterUAV!, new Vortice.Mathematics.Int4(0));
                // Re-enable scrolling detection
                _context?.ClearUnorderedAccessView(_boundingAreaTileChangeCountUAV!, new Vortice.Mathematics.Int4(0));

                _context?.CSSetShader(_computeShader);

                // Ensure the number of parameters passed to the shader matches what's defined in HLSL
                uint[] cbData = new uint[20]; // Increase array size to accommodate new parameters
                cbData[0] = (uint)_screenW;
                cbData[1] = (uint)_screenH;
                cbData[2] = (uint)TileSize;
                cbData[3] = (uint)PixelDelta;
                cbData[4] = AverageWindowSize;
                cbData[5] = StableFramesRequired;
                cbData[6] = AdditionalCooldownFrames;
                cbData[7] = FirstRefreshExtraDelay;
                cbData[8] = frameCounter;
                cbData[9] = ProtectionFrames;
                cbData[10] = (uint)BoundingArea.Width;
                cbData[11] = (uint)BoundingArea.Height;
                cbData[12] = (uint)BoundingArea.HistoryFrames;
                cbData[13] = (uint)BoundingArea.ChangeThreshold;
                cbData[14] = (uint)BoundingArea.RefreshBlockThreshold; // New parameter added
                cbData[15] = 0; // padding1
                cbData[16] = 0; // padding2
                                // Remaining positions automatically initialized to 0
                _context?.UpdateSubresource(cbData, _paramBuffer!);
                _context?.CSSetConstantBuffer(0, _paramBuffer);

                using var srvPrev = _device!.CreateShaderResourceView(_gpuTexPrev!);
                using var srvCurr = _device!.CreateShaderResourceView(_gpuTexCurr!);
                using var srvHistory = _device.CreateShaderResourceView(_boundingAreaHistoryBuffer);
                _context?.CSSetShaderResource(0, srvPrev);
                _context?.CSSetShaderResource(1, srvCurr);
                _context?.CSSetShaderResource(2, srvHistory); // Bind as read-only resource

                _context?.CSSetUnorderedAccessView(0, _tileStateInUAV);
                _context?.CSSetUnorderedAccessView(1, _tileStateOutUAV);
                _context?.CSSetUnorderedAccessView(2, _refreshListUAV);
                _context?.CSSetUnorderedAccessView(3, _refreshCounterUAV);
                _context?.CSSetUnorderedAccessView(4, _tileBrightnessUAV);
                _context?.CSSetUnorderedAccessView(5, _tileStableCountersUAV);
                _context?.CSSetUnorderedAccessView(6, _tileProtectionExpiryUAV);
                // Re-enable scrolling detection
                _context?.CSSetUnorderedAccessView(7, _boundingAreaTileChangeCountUAV);

                _context?.Dispatch((uint)_tilesX, (uint)_tilesY, 1);

                _context?.CSSetShader(null);
                _context?.CSSetShaderResource(0, null);
                _context?.CSSetShaderResource(1, null);
                _context?.CSSetShaderResource(7, null);
                for (int i = 0; i <= 8; i++)
                {
                    if (i != 7) _context?.CSSetUnorderedAccessView((uint)i, null);
                }

                // 3. CPU-side scroll suppression logic processing
                _context?.CopyResource(_boundingAreaTileChangeCountReadback!, _boundingAreaTileChangeCountBuffer!);
                var map = _context!.Map(_boundingAreaTileChangeCountReadback!, 0, MapMode.Read);
                var changeCounts = new uint[_boundingAreaCount];

                unsafe
                {
                    fixed (uint* ptr = changeCounts)
                    {
                        Buffer.MemoryCopy((void*)map.DataPointer, ptr, changeCounts.Length * (long)sizeof(uint), changeCounts.Length * (long)sizeof(uint));
                    }
                }
                _context.Unmap(_boundingAreaTileChangeCountReadback!, 0);

                for (int i = 0; i < _boundingAreaCount; i++)
                {
                    bool isAreaChangedSignificantly = changeCounts[i] > 0;
                    uint historyIndex = frameCounter % (uint)BoundingArea.HistoryFrames;
                    uint mask = 1u << (int)(historyIndex & 31);

                    if (isAreaChangedSignificantly)
                    {
                        _boundingAreaHistory_cpu![i] |= mask;
                    }
                    else
                    {
                        _boundingAreaHistory_cpu![i] &= ~mask;
                    }
                }

                // Print bounding area history frame count and change frame count information
                if (_debugLogger != null)
                {
                    // Print all bounding area information (no longer limited to first 5)
                    for (int i = 0; i < _boundingAreaCount; i++)
                    {
                        uint historyData = _boundingAreaHistory_cpu![i];
                        uint changeCount = 0;

                        // Calculate historical change frame count
                        uint maxTests = Math.Min((uint)BoundingArea.HistoryFrames, 32);
                        for (uint j = 0; j < maxTests; j++)
                        {
                            if ((historyData & (1u << (int)j)) != 0)
                            {
                                changeCount++;
                            }
                        }

                        // Generate more friendly historical change pattern description
                        string patternDescription = "";
                        if (historyData == 0)
                        {
                            patternDescription = "No change";
                        }
                        else if (historyData == uint.MaxValue)
                        {
                            patternDescription = "Continuous change";
                        }
                        else if ((historyData & 0x55555555) == historyData)
                        {
                            patternDescription = "Even frame change";
                        }
                        else if ((historyData & 0xAAAAAAAA) == historyData)
                        {
                            patternDescription = "Odd frame change";
                        }
                        else
                        {
                            // Count consecutive changing frames
                            int maxConsecutive = 0;
                            int currentConsecutive = 0;
                            for (int bit = 0; bit < 32; bit++)
                            {
                                if ((historyData & (1u << bit)) != 0)
                                {
                                    currentConsecutive++;
                                    maxConsecutive = Math.Max(maxConsecutive, currentConsecutive);
                                }
                                else
                                {
                                    currentConsecutive = 0;
                                }
                            }

                            if (maxConsecutive > 3)
                            {
                                patternDescription = $"Continuous {maxConsecutive} frames change";
                            }
                            else
                            {
                                patternDescription = "Mixed change pattern";
                            }
                        }

                        // _debugLogger.Invoke($"DEBUG: BoundingArea {i}: History=0x{historyData:X8}, ChangeCount={changeCount}/{BoundingArea.HistoryFrames}, Pattern={patternDescription}");
                    }

                    // Print total bounding area count
                    _debugLogger.Invoke($"DEBUG: Total bounding areas: {_boundingAreaCount}");
                }

                // Write updated CPU history data back to GPU buffer
                _context!.UpdateSubresource(_boundingAreaHistory_cpu!, _boundingAreaHistoryBuffer!);

                // 4. Read back results
                _context?.CopyResource(_refreshCounterReadback!, _refreshCounter!);
                var counterMap = _context?.Map(_refreshCounterReadback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int refreshCount = counterMap.HasValue ? Marshal.ReadInt32(counterMap.Value.DataPointer) : 0;
                _context?.Unmap(_refreshCounterReadback!, 0);

                if (refreshCount > 0)
                {
                    _context?.CopyResource(_refreshListReadback!, _refreshList!);
                    var listMap = _context?.Map(_refreshListReadback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    if (listMap.HasValue)
                    {
                        for (int i = 0; i < refreshCount; i++)
                        {
                            uint tileIndex = (uint)Marshal.ReadInt32(listMap.Value.DataPointer, i * 4); // Read uint (4 bytes)
                            int bx = (int)(tileIndex % _tilesX);
                            int by = (int)(tileIndex / _tilesX);
                            result.Add((bx, by));
                        }
                        _context?.Unmap(_refreshListReadback!, 0);
                    }
                }

                // 5. State iteration: Copy current frame output state to next frame input state
                _context?.CopyResource(_tileStateIn!, _tileStateOut!);

                // 6. Texture iteration: Copy current frame texture to next frame's previous frame texture
                _context?.CopyResource(_gpuTexPrev!, _gpuTexCurr!);

                // 7. Read back brightness data
                float[] brightnessData = new float[_tilesX * _tilesY];
                _context?.CopyResource(_tileBrightnessReadback!, _tileBrightness!);
                var brightnessMap = _context?.Map(_tileBrightnessReadback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (brightnessMap.HasValue)
                {
                    unsafe
                    {
                        float* brightnessPtr = (float*)brightnessMap.Value.DataPointer.ToPointer();
                        for (int i = 0; i < _tilesX * _tilesY; i++)
                        {
                            brightnessData[i] = brightnessPtr[i];
                        }
                    }
                    _context?.Unmap(_tileBrightnessReadback!, 0);
                }

                // Add final sync point to ensure all GPU operations complete
                _context?.Flush();

                // Debug information: Record tile state statistics
                if (_debugLogger != null && result.Count == 0)
                {
                    // Read some tile states to understand detection progress
                    _context?.CopyResource(_tileStateInReadback!, _tileStateIn!);
                    var stateMap = _context?.Map(_tileStateInReadback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                    if (stateMap.HasValue)
                    {
                        const int historyElementSize = 16; // sizeof(uint4)

                        _debugLogger.Invoke($"DEBUG: Tile states (first 10 tiles):");
                        for (int i = 0; i < Math.Min(10, _tilesX * _tilesY); i++) // Only check first 10 tiles
                        {
                            // Read the uint4 for each tile
                            IntPtr dataPtr = stateMap.Value.DataPointer;
                            uint hx = (uint)Marshal.ReadInt32(dataPtr, i * historyElementSize + 0 * sizeof(uint));
                            uint hy = (uint)Marshal.ReadInt32(dataPtr, i * historyElementSize + 1 * sizeof(uint));
                            uint hz = (uint)Marshal.ReadInt32(dataPtr, i * historyElementSize + 2 * sizeof(uint));
                            uint hw = (uint)Marshal.ReadInt32(dataPtr, i * historyElementSize + 3 * sizeof(uint));
                            _debugLogger.Invoke($"  Tile {i}: History = ({hx}, {hy}, {hz}, {hw})");
                        }
                        _context?.Unmap(_tileStateInReadback!, 0);
                    }
                }

                return (result, brightnessData);
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Exception occurred during capture and compute: {ex.Message}");
                _debugLogger?.Invoke($"Exception type: {ex.GetType().Name}");
                _debugLogger?.Invoke($"Exception stack trace: {ex.StackTrace}");
                return (result, new float[_tilesX * _tilesY]);
            }
            finally
            {
                // Ensure semaphore release and reset flag
                _captureSemaphore.Release();
                Interlocked.Exchange(ref _isCapturing, 0);
            }
        }

        // Modified to return a list of all outputs for logging
        private List<IDXGIOutput> GetAllOutputs(IDXGIAdapter adapter)
        {
            var outputs = new List<IDXGIOutput>();
            _debugLogger?.Invoke("Starting adapter output enumeration...");

            for (uint i = 0; ; i++)
            {
                _debugLogger?.Invoke($"Trying to enumerate output {i}...");
                var enumResult = adapter.EnumOutputs(i, out IDXGIOutput? output);
                _debugLogger?.Invoke($"EnumOutputs({i}) result: {enumResult.Success}, HRESULT: 0x{enumResult.Code:X8}");

                if (enumResult.Success && output != null)
                {
                    outputs.Add(output);
                    _debugLogger?.Invoke($"Successfully added output {i}");
                }
                else
                {
                    _debugLogger?.Invoke($"No more outputs at index {i}");
                    break; // No more outputs
                }
            }

            _debugLogger?.Invoke($"Total outputs found: {outputs.Count}");
            return outputs;
        }

        // DPI detection method
        private void DetectSystemDpiSettings()
        {
            try
            {
                // Attempt to get DPI settings for the specified display
                float dpiX = 96.0f, dpiY = 96.0f;

                // Get all display information
                var allScreens = System.Windows.Forms.Screen.AllScreens;

                // Check if target display index is valid
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];

                    _debugLogger?.Invoke($"Attempting to get DPI settings for display {_targetScreenIndex} ({targetScreen.DeviceName})");
                    _debugLogger?.Invoke($"Display bounds: {targetScreen.Bounds}");

                    // Method 1: Use GetDpiForMonitor API to get accurate display DPI (preferred method)
                    try
                    {
                        var bounds = targetScreen.Bounds;
                        var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

                        // Get display handle
                        IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

                        if (hMonitor != IntPtr.Zero)
                        {
                            uint monitorDpiX, monitorDpiY;
                            int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out monitorDpiX, out monitorDpiY);

                            if (result == 0) // S_OK
                            {
                                dpiX = monitorDpiX;
                                dpiY = monitorDpiY;
                                _debugLogger?.Invoke($"Method 1 success: GetDpiForMonitor returned DPI {dpiX}x{dpiY}");
                            }
                            else
                            {
                                _debugLogger?.Invoke($"Method 1 failed: GetDpiForMonitor returned error code 0x{result:X8}, will try fallback method");
                            }
                        }
                        else
                        {
                            _debugLogger?.Invoke($"Method 1: Unable to get display handle");
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"Method 1 failed: {ex.Message}, will try fallback method");
                    }

                    // Method 2: If method 1 fails, use GetDpiForMonitor as fallback method
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            var bounds = targetScreen.Bounds;
                            var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

                            // Get display handle
                            IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

                            if (hMonitor != IntPtr.Zero)
                            {
                                uint monitorDpiX, monitorDpiY;
                                int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out monitorDpiX, out monitorDpiY);

                                if (result == 0) // S_OK
                                {
                                    dpiX = monitorDpiX;
                                    dpiY = monitorDpiY;
                                    _debugLogger?.Invoke($"Method 2 success: GetDpiForMonitor returned DPI {dpiX}x{dpiY}");
                                }
                                else
                                {
                                    _debugLogger?.Invoke($"Method 2 failed: GetDpiForMonitor returned error code 0x{result:X8}");
                                }
                            }
                            else
                            {
                                _debugLogger?.Invoke($"Method 2: Unable to get display handle");
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"Method 2 failed: {ex.Message}");
                        }
                    }

                    /*
                    // Method 3: If method 1 and method 2 fail, try creating Graphics object for specific display to get its DPI (commented out)
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            // Get the bounds rectangle of the display
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
                                        dpiX = graphics.DpiX;
                                        dpiY = graphics.DpiY;
                                        _debugLogger?.Invoke($"Method 3 success: Graphics.FromHwnd returned DPI {dpiX}x{dpiY}");
                                    }
                                }
                                finally
                                {
                                    NativeMethods.DestroyWindow(tempHwnd);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"Method 3 failed: {ex.Message}");
                        }
                    }
                    
                    // Method 4: If all methods fail, try calculating DPI using Screen.Bounds and EnumDisplaySettings (commented out)
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            // Get device name
                            string deviceName = targetScreen.DeviceName;

                            _debugLogger?.Invoke($"Method 4: Attempting to get display information via EnumDisplaySettings...");
                            _debugLogger?.Invoke($"Method 4: Screen.Bounds = {targetScreen.Bounds}");
                            _debugLogger?.Invoke($"Method 4: DeviceName = {deviceName}");

                            // Use EnumDisplaySettings to get actual physical resolution
                            NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                            devMode.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));

                            if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                            {
                                int physicalWidth = devMode.dmPelsWidth;
                                int physicalHeight = devMode.dmPelsHeight;
                                int logicalWidth = targetScreen.Bounds.Width;
                                int logicalHeight = targetScreen.Bounds.Height;

                                _debugLogger?.Invoke($"Method 4: DEVMODE physical resolution = {physicalWidth}x{physicalHeight}");
                                _debugLogger?.Invoke($"Method 4: Screen.Bounds logical resolution = {logicalWidth}x{logicalHeight}");
                                
                                // Calculate DPI scaling ratio
                                float scaleX = (float)physicalWidth / logicalWidth;
                                float scaleY = (float)physicalHeight / logicalHeight;
                                
                                dpiX = 96.0f * scaleX;
                                dpiY = 96.0f * scaleY;
                                
                                _debugLogger?.Invoke($"Method 4: Calculated scaling ratio = {scaleX:F2}x{scaleY:F2}");
                                _debugLogger?.Invoke($"Method 4 success: Final DPI = {dpiX}x{dpiY}");
                            }
                            else
                            {
                                _debugLogger?.Invoke($"Method 4: EnumDisplaySettings failed");
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"Method 4 failed: {ex.Message}");
                        }
                    }
                    */
                }

                // If unable to get specific display's DPI, use primary display's DPI
                if (dpiX == 96.0f && dpiY == 96.0f)
                {
                    using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        dpiX = graphics.DpiX;
                        dpiY = graphics.DpiY;
                        _debugLogger?.Invoke($"Fallback to primary display DPI: {dpiX}x{dpiY}");
                    }
                }

                _dpiX = dpiX;
                _dpiY = dpiY;
                _dpiScaleX = _dpiX / 96.0f;
                _dpiScaleY = _dpiY / 96.0f;

                _debugLogger?.Invoke($"Final DPI settings: {_dpiX}x{_dpiY}, scaling ratio: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"DPI detection failed: {ex.Message}");
                _debugLogger?.Invoke("Will use default DPI settings (96x96)");

                // Use default values
                _dpiX = 96.0f;
                _dpiY = 96.0f;
                _dpiScaleX = 1.0f;
                _dpiScaleY = 1.0f;
            }
        }

        // Get correct screen bounds (DXGI already returns physical resolution)
        private Rectangle GetCorrectScreenBounds(Rectangle dxgiBounds)
        {
            // DXGI's DesktopCoordinates returns physical resolution, no conversion needed
            // Previously incorrectly multiplied by DPI scaling ratio

            _debugLogger?.Invoke($"DXGI returned screen bounds: {dxgiBounds}");
            _debugLogger?.Invoke($"System DPI: {_dpiX}x{_dpiY}, scaling ratio: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
            _debugLogger?.Invoke($"Physical resolution: {dxgiBounds.Width}x{dxgiBounds.Height}");

            // Validate screen bounds - Fixed: Allow negative coordinates (normal in multi-monitor environment)
            if (dxgiBounds.Width <= 0 || dxgiBounds.Height <= 0 ||
                dxgiBounds.Width > 16384 || dxgiBounds.Height > 16384) // Maximum reasonable resolution limit
            {
                _debugLogger?.Invoke($"DEBUG: Invalid DXGI screen bounds parameters: {dxgiBounds}");
                // Return a safe default bounds
                return new Rectangle(0, 0, 1920, 1080);
            }

            // Calculate logical resolution for reference
            int logicalWidth = (int)(dxgiBounds.Width / _dpiScaleX);
            int logicalHeight = (int)(dxgiBounds.Height / _dpiScaleY);

            _debugLogger?.Invoke($"Logical resolution: {logicalWidth}x{logicalHeight}");
            _debugLogger?.Invoke($"Screen capture will use physical resolution: {dxgiBounds.Width}x{dxgiBounds.Height}");

            return dxgiBounds;
        }

        // E-ink screen detection and GDI+ capture method
        private bool DetectEinkScreen(IDXGIOutput output)
        {
            try
            {
                _debugLogger?.Invoke("Starting e-ink screen feature detection...");

                var desc = output.Description;
                string deviceName = desc.DeviceName.ToLower();

                // Get display friendly name
                string friendlyName = GetFriendlyDisplayName(desc.DeviceName);
                if (!string.IsNullOrEmpty(friendlyName))
                {
                    _debugLogger?.Invoke($"Display friendly name: '{friendlyName}'");
                }

                // Detect eink keywords in device name
                bool isEink = deviceName.Contains("eink") || deviceName.Contains("e-ink") ||
                             deviceName.Contains("epd") || deviceName.Contains("electronic paper");

                if (isEink)
                {
                    _debugLogger?.Invoke($"Detected eink screen: {desc.DeviceName}");
                    return true;
                }

                // Detect refresh rate features
                try
                {
                    var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);
                    if (displayModeList.Any())
                    {
                        var primaryMode = displayModeList[0];
                        double refreshRate = (double)primaryMode.RefreshRate.Numerator / primaryMode.RefreshRate.Denominator;
                        _detectedRefreshRate = refreshRate;

                        _debugLogger?.Invoke($"Detected refresh rate: {refreshRate:F2}Hz");
                        _debugLogger?.Invoke($"Physical resolution: {desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left}x{desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top}");

                        // E-ink screens typically have lower refresh rates (below 59Hz)
                        if (refreshRate < 59.0)
                        {
                            _debugLogger?.Invoke($"Low refresh rate ({refreshRate:F2}Hz) may be e-ink screen");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"Refresh rate detection failed: {ex.Message}");
                }

                _debugLogger?.Invoke($"No e-ink screen characteristics detected: {desc.DeviceName}");
                return false;
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"E-ink screen detection failed: {ex.Message}");
                return false;
            }
        }

        // Get display friendly name
        private string GetFriendlyDisplayName(string deviceName)
        {
            try
            {
                NativeMethods.DISPLAY_DEVICE deviceInfo = new NativeMethods.DISPLAY_DEVICE();
                deviceInfo.cb = Marshal.SizeOf(deviceInfo);

                // Try to get display device information
                if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref deviceInfo, 0))
                {
                    // If successful, return device string (usually friendly name)
                    return deviceInfo.DeviceString;
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Failed to get display friendly name: {ex.Message}");
            }

            // If unable to get friendly name, return empty string
            return string.Empty;
        }

        private bool InitializeGdiCapture(IDXGIOutput output)
        {
            try
            {
                _debugLogger?.Invoke("Initializing GDI+ screen capture...");

                var desc = output.Description;
                var dxgiBounds = new Rectangle(
                    desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Top,
                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top
                );
                // Use correct screen bounds (DXGI has returned physical resolution)
                _screenBounds = GetCorrectScreenBounds(dxgiBounds);

                _screenW = _screenBounds.Width;
                _screenH = _screenBounds.Height;

                _debugLogger?.Invoke($"DXGI screen bounds: {dxgiBounds}");
                _debugLogger?.Invoke($"Physical screen bounds: {_screenBounds}");
                _debugLogger?.Invoke($"DPI scaling ratio: {_dpiScaleX:F2}x{_dpiScaleY:F2}");

                // Validate screen size reasonableness to prevent memory overflow
                long totalPixels = (long)_screenW * _screenH;
                long memoryRequired = totalPixels * 4; // 4 bytes per pixel for 32bpp
                long maxMemory = 512 * 1024 * 1024; // 512MB limit

                if (memoryRequired > maxMemory || _screenW > 16384 || _screenH > 16384)
                {
                    _debugLogger?.Invoke($"DEBUG: Screen size too large or memory requirement too high: {_screenW}x{_screenH}, memory required: {memoryRequired} bytes");
                    return false;
                }

                // Create GDI+ bitmap - use physical size
                try
                {
                    // RELEASE mode fix: Add stricter parameter validation
                    if (_screenW <= 0 || _screenH <= 0 || _screenW > 16384 || _screenH > 16384)
                    {
                        _debugLogger?.Invoke($"GDI+ bitmap size invalid: {_screenW}x{_screenH}");
                        return false;
                    }

                    _gdiBitmap = new Bitmap(_screenW, _screenH, PixelFormat.Format32bppArgb);
                    if (_gdiBitmap == null)
                    {
                        _debugLogger?.Invoke("GDI+ bitmap creation failed: bitmap is null");
                        return false;
                    }

                    _gdiGraphics = Graphics.FromImage(_gdiBitmap);
                    if (_gdiGraphics == null)
                    {
                        _debugLogger?.Invoke("GDI+ graphics object creation failed: graphics object is null");
                        _gdiBitmap.Dispose();
                        _gdiBitmap = null;
                        return false;
                    }
                }
                catch (OutOfMemoryException ex)
                {
                    _debugLogger?.Invoke($"GDI+ bitmap creation failed - insufficient memory: {ex.Message}");
                    return false;
                }
                catch (ArgumentException ex)
                {
                    _debugLogger?.Invoke($"GDI+ bitmap creation failed - invalid argument: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"GDI+ bitmap creation failed - unknown error: {ex.Message}");
                    return false;
                }

                // Set GDI+ DPI to match system settings
                try
                {
                    _gdiGraphics.PageUnit = GraphicsUnit.Pixel;
                    _debugLogger?.Invoke($"GDI+ bitmap created successfully, physical size: {_screenW}x{_screenH}");
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"GDI+ settings failed: {ex.Message}");
                    CleanupGdiObjects();
                    return false;
                }

                _debugLogger?.Invoke("GDI+ capture initialization successful");
                return true;
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"GDI+ capture initialization failed: {ex.Message}");
                _debugLogger?.Invoke($"Exception details: {ex}");
                return false;
            }
        }

        private async Task<bool> CaptureScreenWithGdiAsync()
        {
            const int MAX_RETRY_COUNT = 3;
            const int RETRY_DELAY_MS = 50;

            try
            {
                if (_gdiGraphics == null || _gdiBitmap == null)
                {
                    _debugLogger?.Invoke("GDI+ capture object not initialized");
                    return false;
                }

                // Unified coordinate system: use local coordinates relative to target display (0,0)
                // GDI+ capture should start from the display's top-left corner (0,0), not virtual screen absolute coordinates
                Rectangle localScreenBounds = new Rectangle(0, 0, _screenW, _screenH);

                // Add detailed debugging information, compare Screen.AllScreens and DXGI information
                try
                {
                    var allScreens = System.Windows.Forms.Screen.AllScreens;
                    if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                    {
                        var targetScreen = allScreens[_targetScreenIndex];
                        _debugLogger?.Invoke($"DEBUG: Screen.AllScreens[{_targetScreenIndex}] - Device: {targetScreen.DeviceName}, Primary: {targetScreen.Primary}, Bounds: {targetScreen.Bounds}");
                    }
                    else
                    {
                        _debugLogger?.Invoke($"DEBUG: Target display index {_targetScreenIndex} exceeds Screen.AllScreens range");
                    }
                }
                catch (Exception screenEx)
                {
                    _debugLogger?.Invoke($"DEBUG: Unable to get Screen.AllScreens information: {screenEx.Message}");
                }

                _debugLogger?.Invoke($"Starting GDI+ screen capture, local coordinates: {localScreenBounds}");
                _debugLogger?.Invoke($"Original virtual coordinates: {_screenBounds}");
                _debugLogger?.Invoke($"Target display index: {_targetScreenIndex}");
                _debugLogger?.Invoke($"DPI scaling: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
                _debugLogger?.Invoke($"CopyFromScreen parameters: Source({_screenBounds.X},{_screenBounds.Y}) -> Target(0,0) Size({localScreenBounds.Width}x{localScreenBounds.Height})");

                // Validate capture parameter validity
                if (localScreenBounds.Width <= 0 || localScreenBounds.Height <= 0)
                {
                    _debugLogger?.Invoke($"DEBUG: Invalid screen resolution: {localScreenBounds.Width}x{localScreenBounds.Height}");
                    return false;
                }

                // Validate screen bounds parameters - Fixed: allow negative coordinates (normal in multi-monitor environment)
                if (_screenBounds.Width != _screenW || _screenBounds.Height != _screenH ||
                    _screenBounds.Width <= 0 || _screenBounds.Height <= 0)
                {
                    _debugLogger?.Invoke($"DEBUG: Invalid screen bounds parameters: Bounds={_screenBounds}, W={_screenW}, H={_screenH}");
                    return false;
                }

                // Validate GDI+ bitmap size
                if (_gdiBitmap.Width != _screenW || _gdiBitmap.Height != _screenH)
                {
                    _debugLogger?.Invoke($"DEBUG: GDI+ bitmap size mismatch - Actual: {_gdiBitmap.Width}x{_gdiBitmap.Height}, Expected: {_screenW}x{_screenH}");
                    return false;
                }

                // Use local coordinates for capture to avoid coordinate system confusion
                Rectangle safeCaptureBounds = localScreenBounds;

                bool captureSuccess = false;
                int retryCount = 0;

                // Implement retry mechanism
                while (retryCount < MAX_RETRY_COUNT && !captureSuccess)
                {
                    try
                    {
                        // Use GDI+ to capture screen - unified use of target display's local coordinates
                        // Start from target display's absolute position, capture to Bitmap's (0,0) position
                        // RELEASE mode fix: add additional null checks and exception handling
                        if (_gdiGraphics == null || _gdiBitmap == null)
                        {
                            _debugLogger?.Invoke("GDI+ object released before capture");
                            return false;
                        }

                        await Task.Run(() =>
                        {
                            try
                            {
                                // Ensure all objects are still valid when used
                                if (_gdiGraphics != null && _gdiBitmap != null)
                                {
                                    _gdiGraphics.CopyFromScreen(
                                        _screenBounds.X,        // Source coordinates: target display's X position in virtual screen
                                        _screenBounds.Y,        // Source coordinates: target display's Y position in virtual screen  
                                        0, 0,                   // Target coordinates: Bitmap's top-left corner
                                        safeCaptureBounds.Size, // Size: complete display size
                                        CopyPixelOperation.SourceCopy);
                                }
                            }
                            catch (Exception innerEx)
                            {
                                // RELEASE mode: catch all exceptions to prevent crashes
                                _debugLogger?.Invoke($"GDI+ capture internal exception: {innerEx.Message}");
                                throw; // Re-throw for retry mechanism handling
                            }
                        });

                        captureSuccess = true;
                        _debugLogger?.Invoke($"CopyFromScreen completed, capture area: {safeCaptureBounds.X},{safeCaptureBounds.Y} -> 0,0 Size: {safeCaptureBounds.Width}x{safeCaptureBounds.Height}");
                    }
                    catch (Exception ex) // RELEASE mode: catch all exception types
                    {
                        retryCount++;
                        _debugLogger?.Invoke($"GDI+ capture failed, retrying ({retryCount}/{MAX_RETRY_COUNT}): {ex.Message}");

                        if (retryCount < MAX_RETRY_COUNT)
                        {
                            await Task.Delay(RETRY_DELAY_MS);
                        }
                    }
                }

                if (!captureSuccess)
                {
                    _debugLogger?.Invoke("DEBUG: GDI+ capture failed multiple times, aborting this capture");
                    return false;
                }

                // Validate captured bitmap size
                if (_gdiBitmap.Width != _screenW || _gdiBitmap.Height != _screenH)
                {
                    _debugLogger?.Invoke($"Warning: Bitmap size mismatch - Actual: {_gdiBitmap.Width}x{_gdiBitmap.Height}, Expected: {_screenW}x{_screenH}");
                }

                // RELEASE mode fix: add additional bitmap lock safety check
                if (_gdiBitmap == null)
                {
                    _debugLogger?.Invoke("GDI+ bitmap object is null, cannot lock");
                    return false;
                }

                // Copy GDI+ bitmap data to D3D texture
                BitmapData? bitmapData = null;
                try
                {
                    bitmapData = _gdiBitmap.LockBits(new Rectangle(0, 0, _screenW, _screenH),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                    if (bitmapData == null || bitmapData.Scan0 == IntPtr.Zero)
                    {
                        _debugLogger?.Invoke("Bitmap data lock failed or scan pointer is null");
                        return false;
                    }

                    _debugLogger?.Invoke($"Bitmap data lock successful, Stride: {bitmapData.Stride}, scan line size: {bitmapData.Stride * _screenH}");

                    // RELEASE mode fix: add D3D object null check
                    if (_device == null || _context == null)
                    {
                        _debugLogger?.Invoke("D3D device or context is null, cannot update texture");
                        return false;
                    }

                    // Update D3D texture
                    var texDesc = new Texture2DDescription
                    {
                        Width = (uint)_screenW,
                        Height = (uint)_screenH,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None
                    };

                    // Create new texture if it doesn't exist or size doesn't match
                    if (_gpuTexCurr == null || _gpuTexCurr.Description.Width != texDesc.Width || _gpuTexCurr.Description.Height != texDesc.Height)
                    {
                        // Thread-safe release of old texture
                        var oldTex = _gpuTexCurr;
                        _gpuTexCurr = null;

                        if (oldTex != null)
                        {
                            _debugLogger?.Invoke("Releasing old texture, creating new texture");
                            // Delayed release to avoid immediate memory pressure - fix async warning
                            var _ = Task.Delay(50).ContinueWith(t => oldTex.Dispose());
                        }

                        try
                        {
                            _gpuTexCurr = _device.CreateTexture2D(texDesc);
                            _debugLogger?.Invoke($"D3D texture created successfully: {_screenW}x{_screenH}");
                        }
                        catch (Exception texEx)
                        {
                            _debugLogger?.Invoke($"D3D texture creation failed: {texEx.Message}");
                            return false;
                        }
                    }

                    // RELEASE mode fix: safe texture data update
                    try
                    {
                        var box = new MappedSubresource
                        {
                            DataPointer = bitmapData.Scan0,
                            RowPitch = (uint)bitmapData.Stride,
                            DepthPitch = (uint)(bitmapData.Stride * _screenH)
                        };

                        if (box.DataPointer != IntPtr.Zero && _gpuTexCurr != null)
                        {
                            _context.UpdateSubresource(_gpuTexCurr, 0, null, box.DataPointer, box.RowPitch, box.DepthPitch);
                            _debugLogger?.Invoke($"D3D texture updated successfully, RowPitch: {box.RowPitch}");
                        }
                        else
                        {
                            _debugLogger?.Invoke("Texture data pointer is null or texture object is null");
                            return false;
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _debugLogger?.Invoke($"D3D texture update failed: {updateEx.Message}");
                        return false;
                    }

                    return true;
                }
                finally
                {
                    // RELEASE mode fix: safe bitmap unlock
                    if (bitmapData != null)
                    {
                        try
                        {
                            _gdiBitmap.UnlockBits(bitmapData);
                        }
                        catch (Exception unlockEx)
                        {
                            _debugLogger?.Invoke($"Bitmap unlock failed: {unlockEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"GDI+ screen capture failed: {ex.Message}");
                _debugLogger?.Invoke($"Exception details: {ex}");
                return false;
            }
        }

        private bool TryAlternativeCaptureMethods(IDXGIOutput? output)
        {
            _debugLogger?.Invoke("Attempting alternative capture methods...");

            // Check if output is null
            if (output == null)
            {
                _debugLogger?.Invoke("Output is null, cannot attempt alternative capture methods");
                return false;
            }

            // Method 1: Try using different display modes
            try
            {
                _debugLogger?.Invoke("Attempting different display modes...");
                var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);

                if (displayModeList.Any())
                {
                    // Try using the first supported display mode
                    var mode = displayModeList[0];
                    double refreshRate = (double)mode.RefreshRate.Numerator / mode.RefreshRate.Denominator;
                    _debugLogger?.Invoke($"Attempting display mode: {mode.Width}x{mode.Height}@{refreshRate:F2}Hz");

                    // Additional display mode attempt logic can be added here
                    return false; // Temporarily return false, continue trying other methods
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Display mode attempt failed: {ex.Message}");
            }

            // Method 2: Try GDI+ capture
            if (InitializeGdiCapture(output))
            {
                _useGdiCapture = true;
                _debugLogger?.Invoke("Switching to GDI+ capture mode");
                return true;
            }

            return false;
        }

        // Get current mouse position
        private Point GetMousePosition()
        {
            Point point = new Point(-1, -1);
            try
            {
                bool result = NativeMethods.GetCursorPos(out point);
                _debugLogger?.Invoke($"Get mouse position: ({point.X}, {point.Y}), call result: {result}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Failed to get mouse position: {ex.Message}");
            }
            return point;
        }

        // Get text caret position
        private Point GetCaretPosition()
        {
            try
            {
                // Check text caret position every CaretCheckInterval milliseconds to avoid frequent API calls
                if (DateTime.Now - _lastCaretCheck > TimeSpan.FromMilliseconds(CaretCheckInterval))
                {

                    _lastCaretCheck = DateTime.Now;

                    // Get current focus window
                    IntPtr focusWindow = NativeMethods.GetFocus();
                    _debugLogger?.Invoke($"[Caret] Focus window handle: {focusWindow}");

                    // Try to use GetGUIThreadInfo for more comprehensive information
                    uint guiThread = 0;
                    Point guiCaretPos = new Point(-1, -1);
                    bool guiInfoAvailable = false;
                    IntPtr foregroundWindow = IntPtr.Zero; // Moved outside try block

                    try
                    {
                        NativeMethods.GUITHREADINFO guiThreadInfo = new NativeMethods.GUITHREADINFO();
                        guiThreadInfo.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO));

                        foregroundWindow = NativeMethods.GetForegroundWindow();
                        _debugLogger?.Invoke($"[Caret] Foreground window handle: {foregroundWindow}");

                        uint processId; uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out processId);
                        _debugLogger?.Invoke($"[Caret] Foreground window thread ID: {foregroundThread}");

                        if (NativeMethods.GetGUIThreadInfo(foregroundThread, ref guiThreadInfo))
                        {
                            guiThread = foregroundThread;
                            _debugLogger?.Invoke($"[Caret] GetGUIThreadInfo success: hwndCaret={guiThreadInfo.hwndCaret}, rcCaret=({guiThreadInfo.rcCaret.Left},{guiThreadInfo.rcCaret.Top},{guiThreadInfo.rcCaret.Right},{guiThreadInfo.rcCaret.Bottom})");

                            if (guiThreadInfo.hwndCaret != IntPtr.Zero)
                            {
                                // Use caret position from GUI thread info
                                guiCaretPos = new Point(guiThreadInfo.rcCaret.Left, guiThreadInfo.rcCaret.Bottom);
                                bool convertResult = NativeMethods.ClientToScreen(guiThreadInfo.hwndCaret, ref guiCaretPos);
                                _debugLogger?.Invoke($"[Caret] GUI thread caret position conversion result: ({guiCaretPos.X}, {guiCaretPos.Y}), conversion result: {convertResult}");
                                guiInfoAvailable = true;
                                _lastGuiCaretCheck = DateTime.Now;
                            }
                            else
                            {
                                _debugLogger?.Invoke($"[Caret] No valid caret window handle in GUI thread info");
                            }
                        }
                        else
                        {
                            _debugLogger?.Invoke($"[Caret] GetGUIThreadInfo failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"[Caret] Failed to get GUI thread info: {ex.Message}");
                        _debugLogger?.Invoke($"[Caret] Exception details: {ex}");
                    }

                    // Update last recorded caret position
                    _lastFocusWindow = focusWindow;
                    _lastGuiThread = guiThread;
                    _lastGuiCaretPosition = guiCaretPos;
                    _lastCaretPosition = guiInfoAvailable ? guiCaretPos : new Point(-1, -1);
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[Caret] Failed to get text caret position: {ex.Message}");
                _debugLogger?.Invoke($"[Caret] Exception details: {ex}");
                _lastCaretPosition = new Point(-1, -1);
            }
            return _lastCaretPosition;
        }

        // Log cursor, mouse and IME position information
        private void LogCursorPositionInfo()
        {
            try
            {
                // Get mouse position
                Point mousePos = GetMousePosition();
                _lastMousePosition = mousePos;

                // Get text cursor position
                Point caretPos = GetCaretPosition();
                _lastCaretPosition = caretPos;

                // Check IME position every ImeCheckInterval milliseconds to avoid frequent API calls
                if (DateTime.Now - _lastImeCheck > TimeSpan.FromMilliseconds(ImeCheckInterval))
                {
                    _lastImeCheck = DateTime.Now;
                    // Get IME window position
                    Rectangle imeRect = GetImeWindowRect();
                    _lastImeRect = imeRect;
                }

                // Only output detailed information in debug mode
                _debugLogger?.Invoke($"[CursorInfo] Mouse: ({mousePos.X}, {mousePos.Y}), Caret: ({caretPos.X}, {caretPos.Y}), ImeRect: {_lastImeRect}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[CursorInfo] Failed to record cursor info: {ex.Message}");
            }
        }

        // Get IME window position
        private Rectangle GetImeWindowRect()
        {
            try
            {
                // Get current focus window
                IntPtr focusWindow = NativeMethods.GetFocus();
                if (focusWindow == IntPtr.Zero)
                {
                    return Rectangle.Empty;
                }

                // Get IME window handle
                IntPtr imeWindow = NativeMethods.ImmGetDefaultIMEWnd(focusWindow);
                if (imeWindow == IntPtr.Zero)
                {
                    return Rectangle.Empty;
                }

                // Get candidate window position
                NativeMethods.RECT imeRect;
                int result = NativeMethods.SendMessage(imeWindow, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETCANDIDATEPOS, out imeRect);
                if (result != 0)
                {
                    return imeRect.ToRectangle();
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[IME] Failed to get IME window position: {ex.Message}");
            }
            return Rectangle.Empty;
        }

        // Clean up old D3D log files, keeping the latest one
        private void CleanupOldD3DLogFiles(string logDirectory)
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "d3d_debug_*.log");
                if (logFiles.Length > 1)
                {
                    // Sort by creation time, get all files except the latest
                    var filesToDelete = logFiles
                        .Select(f => new { Path = f, Info = new FileInfo(f) })
                        .OrderByDescending(f => f.Info.CreationTime)
                        .Skip(1) // Skip the latest file
                        .Select(f => f.Path);

                    // Delete old files
                    foreach (var file in filesToDelete)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore delete failures
                        }
                    }
                }
            }
            catch
            {
                // Ignore cleanup failures
            }
        }

        /// <summary>
        /// Get the refresh rate of the current primary display.
        /// </summary>
        /// <returns>Refresh rate of the primary display, returns 0.0 if failed to get.</returns>
        public double GetCurrentPrimaryDisplayRefreshRate()
        {
            try
            {
                var result = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory);
                if (result.Failure)
                {
                    _debugLogger?.Invoke("ERROR: GetCurrentPrimaryDisplayRefreshRate - Failed to create DXGI Factory.");
                    return 0.0;
                }

                using (factory)
                {
                    // Check if factory is null to avoid CS8602 warning
                    if (factory == null)
                    {
                        _debugLogger?.Invoke("ERROR: GetCurrentPrimaryDisplayRefreshRate - Factory is null.");
                        return 0.0;
                    }
                    var adapterResult = factory.EnumAdapters1(0, out var adapter);
                    if (adapterResult.Failure || adapter == null)
                    {
                        _debugLogger?.Invoke("ERROR: GetCurrentPrimaryDisplayRefreshRate - Failed to enumerate primary adapter.");
                        return 0.0;
                    }

                    if (adapter == null)
                    {
                        _debugLogger?.Invoke("ERROR: GetCurrentPrimaryDisplayRefreshRate - Adapter is null after enumeration.");
                        return 0.0;
                    }

                    using (adapter)
                    {
                        var outputResult = adapter.EnumOutputs(0, out var output);
                        if (outputResult.Failure)
                        {
                            _debugLogger?.Invoke("ERROR: GetCurrentPrimaryDisplayRefreshRate - Failed to enumerate primary output.");
                            return 0.0;
                        }

                        using (output)
                        {
                            var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);
                            if (displayModeList != null && displayModeList.Any())
                            {
                                var currentMode = displayModeList[0];
                                // Add null check to eliminate CS8602 warning
                                if (currentMode.RefreshRate.Denominator != 0 && currentMode.RefreshRate.Numerator != 0)
                                {
                                    double refreshRate = (double)currentMode.RefreshRate.Numerator / currentMode.RefreshRate.Denominator;
                                    _debugLogger?.Invoke($"DEBUG: Current primary display refresh rate: {refreshRate:F2}Hz");
                                    return refreshRate;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"ERROR: Failed to get current primary display refresh rate: {ex.Message}");
            }

            _debugLogger?.Invoke("WARN: Could not determine primary display refresh rate, returning 0.0.");
            return 0.0;
        }

        // RELEASE mode fix: Add dedicated GDI+ object cleanup method
        private void CleanupGdiObjects()
        {
            try
            {
                // Safely cleanup GDI+ graphics objects
                if (_gdiGraphics != null)
                {
                    try
                    {
                        _gdiGraphics.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"GDI+ graphics object cleanup failed: {ex.Message}");
                    }
                    _gdiGraphics = null;
                }

                // Safely cleanup GDI+ bitmap objects
                if (_gdiBitmap != null)
                {
                    try
                    {
                        _gdiBitmap.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"GDI+ bitmap object cleanup failed: {ex.Message}");
                    }
                    _gdiBitmap = null;
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"Exception during GDI+ object cleanup: {ex.Message}");
            }
        }

        // DXGI format validation method - Check if desktop format is supported
        private bool IsValidDesktopFormat(Format format)
        {
            // Define list of supported desktop formats
            var supportedFormats = new[]
            {
                Format.B8G8R8A8_UNorm,     // Most common desktop format
                Format.B8G8R8A8_UNorm_SRgb, // sRGB version
                Format.R8G8B8A8_UNorm,     // RGBA format
                Format.R8G8B8A8_UNorm_SRgb, // sRGB version
                Format.B8G8R8X8_UNorm,     // No alpha channel format
                Format.B8G8R8X8_UNorm_SRgb, // sRGB version
                Format.R10G10B10A2_UNorm,  // 10-bit color depth format
                Format.R16G16B16A16_UNorm  // 16-bit color depth format (less common)
            };

            return supportedFormats.Contains(format);
        }

        // Dynamic timeout optimization - Adjust timeout based on capture history
        private int GetOptimizedTimeout()
        {
            _captureAttemptCount++;

            // Calculate success rate
            double successRate = _captureAttemptCount > 0 ? (double)_captureSuccessCount / _captureAttemptCount : 1.0;

            // If recent successful capture exists, use base timeout
            if ((DateTime.Now - _lastSuccessfulCapture).TotalSeconds < 5.0 && successRate > 0.8)
            {
                _consecutiveTimeouts = 0;
                _consecutiveFailures = 0;
                return _baseTimeoutMs;
            }

            // If consecutive failures, gradually increase timeout
            if (_consecutiveFailures > 3)
            {
                int increasedTimeout = Math.Min(_baseTimeoutMs + (_consecutiveFailures - 3) * 50, _maxTimeoutMs);
                _debugLogger?.Invoke($"WARNING: Increasing timeout to {increasedTimeout}ms due to {_consecutiveFailures} consecutive failures");
                return increasedTimeout;
            }

            // If consecutive timeouts, increase timeout
            if (_consecutiveTimeouts > 2)
            {
                int increasedTimeout = Math.Min(_baseTimeoutMs + _consecutiveTimeouts * 25, _maxTimeoutMs);
                _debugLogger?.Invoke($"WARNING: Increasing timeout to {increasedTimeout}ms due to {_consecutiveTimeouts} consecutive timeouts");
                return increasedTimeout;
            }

            return _baseTimeoutMs;
        }

        public void Dispose()
        {
            _computeShader?.Dispose();
            _paramBuffer?.Dispose();

            // Release state buffers
            _tileStateInUAV?.Dispose();
            _tileStateIn?.Dispose();
            _tileStateOutUAV?.Dispose();
            _tileStateOut?.Dispose();

            // Release refresh list buffers
            _refreshListUAV?.Dispose();
            _refreshList?.Dispose();
            _refreshCounterUAV?.Dispose();
            _refreshCounter?.Dispose();
            _refreshListReadback?.Dispose();
            _refreshCounterReadback?.Dispose();
            _tileStateInReadback?.Dispose();

            // Release brightness buffers
            _tileBrightnessUAV?.Dispose();
            _tileBrightness?.Dispose();
            _tileBrightnessReadback?.Dispose();

            // Release GPU-side state management buffers
            _tileStableCountersUAV?.Dispose();
            _tileStableCountersBuffer?.Dispose();
            _tileProtectionExpiryUAV?.Dispose();
            _tileProtectionExpiryBuffer?.Dispose();

            _gpuTexCurr?.Dispose();
            _gpuTexPrev?.Dispose();
            _deskDup?.Dispose();
            _context?.Dispose();
            _device?.Dispose();

            // Release GDI+ resources
            CleanupGdiObjects();
        }
    }
}
