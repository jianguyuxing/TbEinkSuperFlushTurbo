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
    // D3D11_DRIVER_TYPE 对应的枚举（Win32 原生）
    enum DriverType : uint
    {
        Unknown = 0,
        Hardware = 1,
        Reference = 2,
        NullDriver = 3,
        Software = 4,
        Warp = 5
    }

    // 合围区域配置结构,多个相邻区块的合围区域用于抑制滚动刷新
    public struct BoundingAreaConfig
    {
        public int Width;       // 每个合围区域宽度（区块单位）
        public int Height;      // 每个合围区域高度（区块单位）
        public int HistoryFrames; // 历史帧数
        public int ChangeThreshold; // 变化阈值
        public int RefreshBlockThreshold; // 判定合围区域刷新所需的区块数阈值

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
        public int TileSize { get; set; } //区块尺寸边长，单位是像素
        public int PixelDelta { get; set; } // Per-component threshold
        public uint AverageWindowSize { get; set; } // 平均窗口大小（帧数）
        public uint StableFramesRequired { get; set; } // 稳定帧数，平衡响应速度和稳定性
        public uint AdditionalCooldownFrames { get; set; } // 额外冷却帧数，避免过度刷新
        public uint FirstRefreshExtraDelay { get; set; } // 首次刷新额外延迟帧数，用于-1状态区块
        public int CaretCheckInterval { get; set; } // 文本光标检查间隔（毫秒）
        public int ImeCheckInterval { get; set; } // 输入法窗口检查间隔（毫秒）
        public int MouseExclusionRadiusFactor { get; set; } // 鼠标排除区域半径因子
        public uint ProtectionFrames { get; set; } // 从MainForm传入的保护期帧数

        // 合围区域配置
        public BoundingAreaConfig BoundingArea { get; set; }

        public int ScreenWidth => _screenW;
        public int ScreenHeight => _screenH;
        public int TilesX => _tilesX;
        public int TilesY => _tilesY;

        // 添加逻辑分辨率属性
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
        private Format _screenFormat; // 存储屏幕的实际格式

        // DPI和缩放相关
        private float _dpiScaleX = 1.0f;
        private float _dpiScaleY = 1.0f;
        private float _dpiX = 96.0f;
        private float _dpiY = 96.0f;

        // 选定显示器的索引
        private int _targetScreenIndex = 0;

        // 屏幕纹理
        private ID3D11Texture2D? _gpuTexCurr;
        private ID3D11Texture2D? _gpuTexPrev;

        // 格式检测
        private bool _formatDetected = false;
        private Format _actualDesktopFormat = Format.B8G8R8A8_UNorm;

        // 状态缓冲区 (核心逻辑) - 现在每个图块存储4个uint的历史差异
        private ID3D11Buffer? _tileStateIn;  // u0: 上一帧的状态 (输入)
        private ID3D11UnorderedAccessView? _tileStateInUAV;
        private ID3D11Buffer? _tileStateOut; // u1: 当前帧的新状态 (输出)
        private ID3D11UnorderedAccessView? _tileStateOutUAV;

        // 刷新列表 (输出)
        private ID3D11Buffer? _refreshList; // u2: 需要刷新的图块索引列表
        private ID3D11UnorderedAccessView? _refreshListUAV;
        private ID3D11Buffer? _refreshCounter; // u3: 刷新列表的原子计数器
        private ID3D11UnorderedAccessView? _refreshCounterUAV;
        private ID3D11Buffer? _refreshListReadback; // 用于从 GPU 读回刷新列表
        private ID3D11Buffer? _refreshCounterReadback; // 用于从 GPU 读回计数器
        private ID3D11Buffer? _tileStateInReadback; // 用于从 GPU 读回图块状态

        // 亮度数据缓冲区
        private ID3D11Buffer? _tileBrightness; // u4: 瓦片亮度数据
        private ID3D11UnorderedAccessView? _tileBrightnessUAV;
        private ID3D11Buffer? _tileBrightnessReadback; // 用于从 GPU 读回亮度数据

        // GPU端状态管理缓冲区
        private ID3D11Buffer? _tileStableCountersBuffer; // u5
        private ID3D11UnorderedAccessView? _tileStableCountersUAV;
        private ID3D11Buffer? _tileProtectionExpiryBuffer; // u6
        private ID3D11UnorderedAccessView? _tileProtectionExpiryUAV;

        // 合围区域历史帧缓冲区
        private ID3D11Buffer? _boundingAreaHistoryBuffer; // u7

        // --- 滚动抑制相关资源 ---
        private ID3D11Buffer? _boundingAreaTileChangeCountBuffer; // u7 (GPU-side counter)
        private ID3D11UnorderedAccessView? _boundingAreaTileChangeCountUAV;
        private ID3D11Buffer? _boundingAreaTileChangeCountReadback; // Readback for the counter
        private uint[]? _boundingAreaHistory_cpu; // CPU-side history for logic
        private int _boundingAreaCount;

        private ID3D11ComputeShader? _computeShader;
        private ID3D11Buffer? _paramBuffer;

        private Action<string>? _debugLogger; // Field to store the logger
        private bool _enableDetailedDebugLogs = false; // 控制是否打印详细的DEBUG日志

        // 异步操作同步控制
        private readonly SemaphoreSlim _captureSemaphore = new SemaphoreSlim(1, 1); // 防止并发捕获
        private int _isCapturing = 0; // 原子标志，防止重入

        // DXGI捕获优化相关
        private int _consecutiveTimeouts = 0; // 连续超时计数
        private int _consecutiveFailures = 0; // 连续失败计数
        private int _captureAttemptCount = 0; // 捕获尝试次数
        private int _captureSuccessCount = 0; // 捕获成功次数
        private DateTime _lastSuccessfulCapture = DateTime.MinValue; // 上次成功捕获时间
        private int _baseTimeoutMs = 100; // 基础超时时间
        private int _maxTimeoutMs = 500; // 最大超时时间

        // Eink屏幕兼容性支持
        private bool _useGdiCapture = false; // 是否使用GDI+捕获
        private bool _isEinkScreen = false; // 是否为eink屏幕
        private bool _forceDirectXCapture; // 是否强制使用DirectX捕获（从MainForm传入）
        private double _detectedRefreshRate = 60.0; // 检测到的刷新率
        private Bitmap? _gdiBitmap; // GDI+位图用于屏幕捕获
        private Graphics? _gdiGraphics; // GDI+图形对象
        private Rectangle _screenBounds; // 屏幕边界

        private bool _isFirstFrame = true; // Flag to handle the first frame capture

        // 鼠标和输入法相关
        private Point _lastMousePosition = new Point(-1, -1);
        private Rectangle _lastImeRect = Rectangle.Empty;
        private DateTime _lastImeCheck = DateTime.MinValue;
        // 添加文本光标相关字段
        private Point _lastCaretPosition = new Point(-1, -1);
        private DateTime _lastCaretCheck = DateTime.MinValue;
        private IntPtr _lastFocusWindow = IntPtr.Zero;
        // 添加GUI线程信息相关字段
        private uint _lastGuiThread = 0;
        private Point _lastGuiCaretPosition = new Point(-1, -1);
        private DateTime _lastGuiCaretCheck = DateTime.MinValue;

        public D3DCaptureAndCompute(Action<string>? debugLogger, BoundingAreaConfig boundingArea, bool forceDirectXCapture = true) // Constructor now accepts a logger
        {
            _debugLogger = debugLogger;
            _forceDirectXCapture = forceDirectXCapture;
            BoundingArea = boundingArea; // 使用从MainForm传入的配置
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
            BoundingArea = boundingArea; // 使用从MainForm传入的配置
            ProtectionFrames = protectionFrames;
            _targetScreenIndex = targetScreenIndex;

            Console.WriteLine("=== D3DCaptureAndCompute Constructor Started ===");
            _debugLogger?.Invoke("=== D3DCaptureAndCompute Constructor Started ===");

            // 检测系统DPI设置（根据选择的显示器）
            DetectSystemDpiSettings();

            // 创建独立的日志文件用于调试
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
                else
                    // 删除除最新日志文件外的所有D3D日志文件
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
                        // 仅在详细模式下打印详细的输出信息
                        if (_enableDetailedDebugLogs)
                        {
                            _debugLogger?.Invoke($"DEBUG: Output {i}: Name='{outputDesc.DeviceName}', Physical Resolution: {width}x{height}");
                        }

                        // 获取显示器友好名称
                        string friendlyName = GetFriendlyDisplayName(outputDesc.DeviceName);
                        if (!string.IsNullOrEmpty(friendlyName))
                        {
                            // 仅在详细模式下打印友好名称
                            if (_enableDetailedDebugLogs)
                            {
                                _debugLogger?.Invoke($"DEBUG: Output {i}: Friendly Name='{friendlyName}'");
                            }
                        }

                        // 计算逻辑分辨率（如果适用）
                        float logicalWidth = width / _dpiScaleX;
                        float logicalHeight = height / _dpiScaleY;
                        // 仅在详细模式下打印逻辑分辨率信息
                        if (_enableDetailedDebugLogs)
                        {
                            _debugLogger?.Invoke($"DEBUG: Output {i}: Logical Resolution (approx): {logicalWidth:F0}x{logicalHeight:F0} (based on DPI scale {_dpiScaleX:F2}x{_dpiScaleY:F2})");
                        }

                        // 尝试获取显示模式列表，但要处理可能的失败
                        try
                        {
                            _debugLogger?.Invoke($"Getting display mode list for output {i} with Format.B8G8R8A8_UNorm...");
                            var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);
                            int modeCount = 0;
                            foreach (var item in displayModeList)
                            {
                                modeCount++;
                            }
                            _debugLogger?.Invoke($"成功获取输出 {i} 的显示模式列表，共 {modeCount} 个模式");

                            // 仅在启用详细日志时打印所有显示模式
                            if (_enableDetailedDebugLogs)
                            {
                                _debugLogger?.Invoke("详细显示模式信息:");
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

                // 使用系统默认顺序（Screen.AllScreens顺序），不特殊处理主显示器
                // 为了匹配Windows Forms的Screen.AllScreens顺序，我们按照Screen.AllScreens的设备顺序排序
                var screens = System.Windows.Forms.Screen.AllScreens;
                var sortedOutputs = new List<IDXGIOutput>();

                // 按照Screen.AllScreens的顺序来排序DXGI输出
                foreach (var screen in screens)
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

                // 添加任何未匹配的DXGI输出（以防万一）
                foreach (var output in allOutputs)
                {
                    if (!sortedOutputs.Contains(output))
                    {
                        sortedOutputs.Add(output);
                    }
                }

                // 添加调试信息以验证DXGI输出与Screen.AllScreens的顺序匹配
                _debugLogger?.Invoke($"DEBUG: Windows Forms Screen.AllScreens 顺序:");
                for (int i = 0; i < screens.Length; i++)
                {
                    _debugLogger?.Invoke($"  Screen [{i}]: {screens[i].DeviceName}, 主显示器: {screens[i].Primary}, 边界: {screens[i].Bounds}");
                }

                _debugLogger?.Invoke($"DEBUG: DXGI发现 {allOutputs.Count} 个输出:");
                for (int i = 0; i < allOutputs.Count; i++)
                {
                    try
                    {
                        var desc = allOutputs[i].Description;
                        _debugLogger?.Invoke($"  DXGI输出 [{i}]: {desc.DeviceName}, 坐标: {desc.DesktopCoordinates}");
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"  DXGI输出 [{i}]: 无法获取详细信息: {ex.Message}");
                    }
                }

                _debugLogger?.Invoke($"DEBUG: 匹配后的DXGI输出顺序（按Screen.AllScreens顺序）:");
                for (int i = 0; i < sortedOutputs.Count; i++)
                {
                    try
                    {
                        var desc = sortedOutputs[i].Description;
                        _debugLogger?.Invoke($"  匹配后 [{i}]: {desc.DeviceName}, 坐标: {desc.DesktopCoordinates}");
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"  匹配后 [{i}]: 无法获取详细信息: {ex.Message}");
                    }
                }

                // Select output based on target screen index
                int selectedScreenIndex = _targetScreenIndex;
                _debugLogger?.Invoke($"DEBUG: 目标显示器索引参数: {selectedScreenIndex}, 可用输出数量: {sortedOutputs.Count}");

                if (selectedScreenIndex < 0 || selectedScreenIndex >= sortedOutputs.Count)
                {
                    _debugLogger?.Invoke($"DEBUG: Invalid target screen index {selectedScreenIndex}, defaulting to primary screen (index 0).");
                    selectedScreenIndex = 0;
                }

                var selectedOutput = sortedOutputs[selectedScreenIndex];
                _debugLogger?.Invoke($"DEBUG: Selected output {selectedScreenIndex} for duplication.");

                // 打印选中输出和对应的显示器信息
                try
                {
                    var desc = selectedOutput.Description;
                    _debugLogger?.Invoke($"DEBUG: 选中输出详细信息 - 设备: {desc.DeviceName}, 坐标: {desc.DesktopCoordinates}");
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"DEBUG: 无法获取选中输出详细信息: {ex.Message}");
                }

                // 重新检测DPI设置，确保使用选中显示器的正确DPI
                _debugLogger?.Invoke($"重新检测显示器 {selectedScreenIndex} 的DPI设置...");
                DetectSystemDpiSettings();

                // 检测是否为eink屏幕
                _isEinkScreen = DetectEinkScreen(selectedOutput);

                // If DirectX capture is not forced, prioritize GDI+ capture.
                if (!_forceDirectXCapture)
                {
                    _debugLogger?.Invoke("非强制DirectX捕获模式，将优先尝试GDI+捕获");

                    // Directly use GDI+ capture, avoiding DuplicateOutput call
                    if (InitializeGdiCapture(selectedOutput))
                    {
                        _useGdiCapture = true;
                        _debugLogger?.Invoke("已切换到GDI+捕获模式");
                    }
                    else
                    {
                        _debugLogger?.Invoke("GDI+捕获初始化失败，将尝试DirectX桌面复制");
                    }
                }

                // 如果GDI+捕获失败，尝试DirectX桌面复制
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

                        // DXGI模式下直接使用DesktopCoordinates，确保与DXGI捕获的图像尺寸一致
                        _screenW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                        _debugLogger?.Invoke($"DEBUG: Using desktop coordinates for DXGI: {_screenW}x{_screenH}");

                        _debugLogger?.Invoke($"DEBUG: Successfully created desktop duplication. Screen size: {_screenW}x{_screenH}");
                        _debugLogger?.Invoke($"Desktop coordinates: Left={desc.DesktopCoordinates.Left}, Top={desc.DesktopCoordinates.Top}, Right={desc.DesktopCoordinates.Right}, Bottom={desc.DesktopCoordinates.Bottom}");
                    }
                    catch (Exception ex)
                    {
                        // 提供更详细的错误信息
                        _debugLogger?.Invoke($"DEBUG: DuplicateOutput failed: {ex.GetType().Name}: {ex.Message}");
                        _debugLogger?.Invoke($"DEBUG: HRESULT: 0x{ex.HResult:X8}");
                        _debugLogger?.Invoke($"DEBUG: StackTrace: {ex.StackTrace}");

                        // 检查设备状态
                        try
                        {
                            _debugLogger?.Invoke($"Device is valid: {_device.NativePointer != 0}");
                            _debugLogger?.Invoke($"Output is valid: {output1.NativePointer != 0}");
                        }
                        catch (Exception devEx)
                        {
                            _debugLogger?.Invoke($"Failed to check device/output validity: {devEx.Message}");
                        }

                        // 如果是eink屏幕或参数错误，尝试替代方法
                        if (_isEinkScreen || ex.HResult == unchecked((int)0x80070057)) // E_INVALIDARG
                        {
                            _debugLogger?.Invoke("检测到eink屏幕或参数错误，尝试替代捕获方法...");
                            if (TryAlternativeCaptureMethods(selectedOutput))
                            {
                                _debugLogger?.Invoke("替代捕获方法成功，继续初始化");
                            }
                            else
                            {
                                throw new InvalidOperationException(@"桌面复制失败且无法使用替代方法。可能原因：
1. eink屏幕的特殊显示模式不兼容
2. 设备不支持桌面复制
3. 请求的格式或分辨率不受支持
4. 多显示器配置问题", ex);
                            }
                        }
                        else if (ex.HResult == unchecked((int)0x80070005)) // E_ACCESSDENIED
                        {
                            throw new InvalidOperationException(@"桌面复制被拒绝。可能原因：
1. 另一个应用程序正在使用桌面复制
2. 程序没有足够的权限
3. 需要以管理员权限运行", ex);
                        }
                        else if (ex.HResult == unchecked((int)0x887A0001)) // DXGI_ERROR_UNSUPPORTED
                        {
                            throw new InvalidOperationException("当前硬件或驱动程序不支持桌面复制功能", ex);
                        }
                        else
                        {
                            throw new InvalidOperationException($"创建桌面复制失败: {ex.Message} (HRESULT: 0x{ex.HResult:X8})");
                        }
                    }
                }
                else
                {
                    // 使用GDI+捕获模式，设置屏幕尺寸
                    var desc = selectedOutput.Description;

                    // 尝试使用EnumDisplaySettings获取真实的物理分辨率
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

                    _debugLogger?.Invoke($"GDI+捕获模式，最终屏幕尺寸: {_screenW}x{_screenH}");
                }

                // 获取桌面复制的实际格式
                // 在第一个成功获取的帧中获取格式信息
                _screenFormat = Format.B8G8R8A8_UNorm; // 先使用默认格式
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

            // 使用检测到的屏幕格式创建纹理
            // 支持更多常见的桌面格式
            var texFormat = _screenFormat;
            bool needConversion = false;

            // 检查格式是否需要转换
            switch (texFormat)
            {
                case Format.B8G8R8A8_UNorm:
                case Format.R8G8B8A8_UNorm:
                    // 直接支持
                    _debugLogger?.Invoke($"DEBUG: Format {texFormat} is directly supported");
                    break;
                default:
                    // 对于其他格式，我们需要转换
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

            // --- 创建新的 GPU 缓冲区 ---
            // The shader uses RWStructuredBuffer<uint> with values per tile for history
            // The actual number of history frames is determined by the AverageWindowSize parameter passed from MainForm
            // Note: Currently supports up to 4-frame average window size (AVERAGE_WINDOW_SIZE in MainForm)
            const int historyElementSize = sizeof(uint); // sizeof(uint)
            int historyArraySize = 4; // Maximum supported history frames (must match shader)

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

            // --- 创建 UAVs ---
            var structuredUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)(tileCount * historyArraySize) } };
            _tileStateInUAV = _device.CreateUnorderedAccessView(_tileStateIn, structuredUavDesc);
            _tileStateOutUAV = _device.CreateUnorderedAccessView(_tileStateOut, structuredUavDesc);

            var refreshListUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.Unknown, Buffer = { FirstElement = 0, NumElements = (uint)tileCount } };
            _refreshListUAV = _device.CreateUnorderedAccessView(_refreshList, refreshListUavDesc);

            var counterUavDesc = new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = Format.R32_Typeless, Buffer = { FirstElement = 0, NumElements = 1, Flags = BufferUnorderedAccessViewFlags.Raw } };
            _refreshCounterUAV = _device.CreateUnorderedAccessView(_refreshCounter, counterUavDesc);

            // --- 创建读回缓冲区 ---
            var readbackDesc = new BufferDescription { Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None };
            _refreshListReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(sizeof(uint) * tileCount) });
            _refreshCounterReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = sizeof(uint) });
            _tileStateInReadback = _device.CreateBuffer(readbackDesc with { ByteWidth = (uint)(historyElementSize * tileCount * historyArraySize) });

            // --- 创建亮度数据缓冲区 ---
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

            // --- 创建并初始化GPU端状态管理缓冲区 ---
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

            // 使用 uint2 (8字节) 替代 long (8字节)
            var expiryDesc = new BufferDescription
            {
                ByteWidth = (uint)(8 * tileCount), // sizeof(uint2) = 8
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 8 // sizeof(uint2)
            };
            var initialExpiry = new uint[tileCount * 2]; // 每个图块需要2个uint值
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

            // --- 创建合围区域历史帧缓冲区 ---
            // 计算合围区域的数量
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

            // 创建合围区域历史缓冲区的着色器资源视图 (SRV)
            var boundingAreaHistorySrvDesc = new ShaderResourceViewDescription(
                _boundingAreaHistoryBuffer,
                Format.Unknown,
                0,
                (uint)_boundingAreaCount);
            // 注意：我们暂时不在这创建SRV，因为它将在每次渲染时创建

            // --- 创建并初始化合围区域单帧变化计数缓冲区 ---
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

            // --- 初始化历史差异状态缓冲区 ---
            _context!.ClearUnorderedAccessView(_tileStateInUAV!, new Vortice.Mathematics.Int4(0, 0, 0, 0));

            var shaderPath = Path.Combine(AppContext.BaseDirectory, "ComputeShader.hlsl");
            var csBlob = Compiler.CompileFromFile(shaderPath, "CSMain", "cs_5_0", ShaderFlags.None, EffectFlags.None);
            _debugLogger?.Invoke($"Shader compiled successfully, bytecode size: {csBlob.Span.Length} bytes");

            // 检查编译后的字节码是否有效
            if (csBlob.Span.Length == 0)
            {
                throw new InvalidOperationException("Shader compilation resulted in empty bytecode");
            }

            _computeShader = _device.CreateComputeShader(csBlob.Span);

            // --- 常量缓冲区大小必须是16的倍数 ---
            // 17个uint = 68字节，需要向上取整到16的倍数 -> 80字节 (5个16字节块)。
            _paramBuffer = _device.CreateBuffer(new BufferDescription(80, BindFlags.ConstantBuffer));

            // 初始化 _gpuTexPrev 为零纹理，确保第一帧有有效的参考帧
            if (_device != null && _gpuTexPrev != null)
            {
                using var clearView = _device.CreateRenderTargetView(_gpuTexPrev);
                _context?.ClearRenderTargetView(clearView, new Vortice.Mathematics.Color4(0, 0, 0, 0.5f));
            }

            _debugLogger?.Invoke("DEBUG: Initialized _gpuTexPrev with zero data");

            _debugLogger?.Invoke("=== D3DCaptureAndCompute Constructor Completed Successfully ===");
        }

        // 公共方法：分析最近帧数据 (此方法现在仅用于输出配置信息，不再进行帧数据分析)
        public void AnalyzeRecentFrames()
        {
            try
            {
                _debugLogger?.Invoke("=== 开始分析最近帧数据 ===");

                // 输出当前配置信息
                _debugLogger?.Invoke($"TileSize: {TileSize}, PixelDelta: {PixelDelta}");
                _debugLogger?.Invoke($"屏幕尺寸: {_screenW}x{_screenH}");
                _debugLogger?.Invoke($"图块数量: {_tilesX}x{_tilesY} = {_tilesX * _tilesY} 个图块");
                _debugLogger?.Invoke($"像素差异阈值: {PixelDelta} (相对阈值: {(float)PixelDelta / 256f * 100f:F3}%)");
                _debugLogger?.Invoke($"平均窗口大小 (AverageWindowSize): {AverageWindowSize} 帧");

                _debugLogger?.Invoke("=== 帧差异分析完成 ===");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"分析帧数据失败: {ex.Message}");
            }
        }

        public async Task<(List<(int bx, int by)> tiles, float[] brightnessData)> CaptureAndComputeOnceAsync(uint frameCounter, CancellationToken token)
        {
            var result = new List<(int, int)>();

            try
            {
                // 防止并发捕获 - 使用原子操作快速检查
                if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
                {
                    _debugLogger?.Invoke("DEBUG: 捕获操作正在执行，跳过本次调用");
                    return (result, new float[_tilesX * _tilesY]);
                }
                {
                    // 使用信号量确保线程安全
                    if (!await _captureSemaphore.WaitAsync(0, token))
                    {
                        _debugLogger?.Invoke("DEBUG: 无法获取捕获锁，跳过本次调用");
                        return (result, new float[_tilesX * _tilesY]);
                    }

                    token.ThrowIfCancellationRequested();

                    if (_useGdiCapture)
                    {
                        // 使用GDI+捕获模式
                        _debugLogger?.Invoke("使用GDI+捕获模式...");

                        // 使用Task.Run将GDI+操作移至后台线程
                        bool captureSuccess = await CaptureScreenWithGdiAsync();

                        if (!captureSuccess)
                        {
                            _debugLogger?.Invoke("GDI+捕获失败");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        _debugLogger?.Invoke("GDI+捕获成功");
                    }
                    else
                    {
                        // 使用DirectX桌面复制模式 - 添加参数验证和超时处理
                        if (_deskDup == null)
                        {
                            _debugLogger?.Invoke($"ERROR: _deskDup is null, cannot acquire frame");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // 验证设备状态 - 确保设备仍然有效
                        if (_device == null || _context == null)
                        {
                            _debugLogger?.Invoke($"ERROR: D3D11 device or context is null");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // 动态超时处理 - 根据之前的捕获成功率调整超时时间
                        uint timeoutMs = (uint)GetOptimizedTimeout();
                        var acquireResult = _deskDup.AcquireNextFrame(timeoutMs, out var frameInfo, out IDXGIResource desktopResource);

                        if (!acquireResult.Success)
                        {
                            // 更详细的错误处理，并更新统计信息
                            _consecutiveFailures++;

                            // 根据错误码进行不同的处理
                            if (acquireResult.Code < 0) // 一般错误情况
                            {
                                if ((uint)acquireResult.Code == 0x887A0027) // DXGI_ERROR_WAIT_TIMEOUT
                                {
                                    _consecutiveTimeouts++;
                                    _debugLogger?.Invoke($"WARNING: AcquireNextFrame timed out after {timeoutMs}ms (consecutive timeouts: {_consecutiveTimeouts})");
                                    // 超时不是致命错误，可以继续尝试
                                    return (result, new float[_tilesX * _tilesY]);
                                }
                                else if ((uint)acquireResult.Code == 0x887A0026) // DXGI_ERROR_ACCESS_LOST
                                {
                                    _debugLogger?.Invoke($"ERROR: Desktop duplication access lost. Need to reinitialize. (consecutive failures: {_consecutiveFailures})");
                                    // 访问丢失是严重错误，需要重新初始化
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

                        // 验证获取的资源是否有效
                        if (desktopResource == null)
                        {
                            _debugLogger?.Invoke($"ERROR: AcquireNextFrame returned null desktopResource");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();

                        // 验证纹理对象是否有效
                        if (tex == null)
                        {
                            _debugLogger?.Invoke($"ERROR: Failed to query ID3D11Texture2D interface from desktopResource");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // Log desktopResource description
                        var desktopTexDesc = tex.Description;
                        _debugLogger?.Invoke($"DEBUG: desktopResource acquired. W:{desktopTexDesc.Width}, H:{desktopTexDesc.Height}, Format:{desktopTexDesc.Format}");

                        // 纹理尺寸验证 - 检查获取的桌面纹理尺寸是否有效
                        if (desktopTexDesc.Width <= 0 || desktopTexDesc.Height <= 0)
                        {
                            _debugLogger?.Invoke($"ERROR: Invalid desktop texture dimensions: {desktopTexDesc.Width}x{desktopTexDesc.Height}");
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        // 检查纹理尺寸是否与屏幕尺寸匹配（允许一定的容差）
                        const int maxDimensionDifference = 10; // 允许的最大尺寸差异
                        if (Math.Abs((int)desktopTexDesc.Width - _screenW) > maxDimensionDifference ||
                            Math.Abs((int)desktopTexDesc.Height - _screenH) > maxDimensionDifference)
                        {
                            _debugLogger?.Invoke($"WARNING: Desktop texture size mismatch. Expected: {_screenW}x{_screenH}, Got: {desktopTexDesc.Width}x{desktopTexDesc.Height}");
                            // 不立即返回失败，继续处理但记录警告
                        }

                        // 首次检测实际格式 - 添加格式验证
                        if (!_formatDetected)
                        {
                            _actualDesktopFormat = desktopTexDesc.Format;
                            _formatDetected = true;
                            _debugLogger?.Invoke($"DEBUG: Actual desktop format detected: {_actualDesktopFormat}");

                            // 格式验证 - 确保桌面格式与预期格式兼容
                            if (!IsValidDesktopFormat(_actualDesktopFormat))
                            {
                                _debugLogger?.Invoke($"ERROR: Unsupported desktop format: {_actualDesktopFormat}. Falling back to B8G8R8A8_UNorm");
                                _actualDesktopFormat = Format.B8G8R8A8_UNorm; // 回退到安全格式
                            }

                            // 如果实际格式与纹理格式不匹配，需要重新创建纹理
                            if (_gpuTexCurr != null && _actualDesktopFormat != _gpuTexCurr.Description.Format)
                            {
                                _debugLogger?.Invoke($"DEBUG: Recreating textures with actual format: {_actualDesktopFormat}");

                                // 使用Task.Run将纹理重建操作移至后台线程
                                await Task.Run(() =>
                                {
                                    // 线程安全地释放旧纹理
                                    var oldTexCurr = _gpuTexCurr;
                                    var oldTexPrev = _gpuTexPrev;
                                    _gpuTexCurr = null;
                                    _gpuTexPrev = null;

                                    // 延迟释放以避免立即内存压力
                                    if (oldTexCurr != null)
                                    {
                                        Task.Delay(100).ContinueWith(_ => oldTexCurr.Dispose());
                                    }
                                    if (oldTexPrev != null)
                                    {
                                        Task.Delay(100).ContinueWith(_ => oldTexPrev.Dispose());
                                    }

                                    // 创建新纹理匹配实际格式
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

                                    // 重新初始化为黑色
                                    if (_device != null && _gpuTexPrev != null)
                                    {
                                        using var clearView = _device.CreateRenderTargetView(_gpuTexPrev);
                                        _context?.ClearRenderTargetView(clearView, new Vortice.Mathematics.Color4(0, 0, 0, 0.5f));
                                    }
                                }, token);
                            }
                        }

                        _debugLogger?.Invoke($"DEBUG: Copying with format: {desktopTexDesc.Format}");

                        // 验证目标纹理是否有效
                        if (_gpuTexCurr == null)
                        {
                            _debugLogger?.Invoke($"ERROR: _gpuTexCurr is null, cannot copy resource");
                            _consecutiveFailures++;
                            return (result, new float[_tilesX * _tilesY]);
                        }

                        try
                        {
                            _context!.CopyResource(_gpuTexCurr, tex);

                            // 添加GPU同步点，确保复制操作完成
                            _context.Flush();

                            // 成功复制，更新统计信息
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

                        // 安全地释放帧
                        try
                        {
                            _deskDup.ReleaseFrame();
                        }
                        catch (Exception releaseEx)
                        {
                            _debugLogger?.Invoke($"WARNING: Failed to release frame: {releaseEx.Message}");
                            // 释放帧失败不是致命错误，继续处理
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                // 打印鼠标、光标和输入法位置的调试信息
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

                // 1. GPU 计算
                _context?.ClearUnorderedAccessView(_refreshCounterUAV!, new Vortice.Mathematics.Int4(0));
                // Re-enable scrolling detection
                _context?.ClearUnorderedAccessView(_boundingAreaTileChangeCountUAV!, new Vortice.Mathematics.Int4(0));

                _context?.CSSetShader(_computeShader);

                // 确保传递给着色器的参数数量与 HLSL 中定义的匹配
                uint[] cbData = new uint[20]; // 增加数组大小以容纳新参数
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
                cbData[14] = (uint)BoundingArea.RefreshBlockThreshold; // 新增参数
                cbData[15] = 0; // padding1
                cbData[16] = 0; // padding2
                                // 剩余位置自动初始化为0
                _context?.UpdateSubresource(cbData, _paramBuffer!);
                _context?.CSSetConstantBuffer(0, _paramBuffer);

                using var srvPrev = _device!.CreateShaderResourceView(_gpuTexPrev!);
                using var srvCurr = _device!.CreateShaderResourceView(_gpuTexCurr!);
                using var srvHistory = _device.CreateShaderResourceView(_boundingAreaHistoryBuffer);
                _context?.CSSetShaderResource(0, srvPrev);
                _context?.CSSetShaderResource(1, srvCurr);
                _context?.CSSetShaderResource(2, srvHistory); // 作为只读资源绑定

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

                // 3. CPU端处理滚动抑制逻辑
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

                // 打印合围区域历史帧数和变化帧数信息
                if (_debugLogger != null)
                {
                    // 打印所有合围区域的信息（不再限制前5个）
                    for (int i = 0; i < _boundingAreaCount; i++)
                    {
                        uint historyData = _boundingAreaHistory_cpu![i];
                        uint changeCount = 0;

                        // 计算历史变化帧数
                        uint maxTests = Math.Min((uint)BoundingArea.HistoryFrames, 32);
                        for (uint j = 0; j < maxTests; j++)
                        {
                            if ((historyData & (1u << (int)j)) != 0)
                            {
                                changeCount++;
                            }
                        }

                        // 生成更友好的历史变化模式描述
                        string patternDescription = "";
                        if (historyData == 0)
                        {
                            patternDescription = "无变化";
                        }
                        else if (historyData == uint.MaxValue)
                        {
                            patternDescription = "持续变化";
                        }
                        else if ((historyData & 0x55555555) == historyData)
                        {
                            patternDescription = "偶数帧变化";
                        }
                        else if ((historyData & 0xAAAAAAAA) == historyData)
                        {
                            patternDescription = "奇数帧变化";
                        }
                        else
                        {
                            // 统计连续变化的帧数
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
                                patternDescription = $"连续{maxConsecutive}帧变化";
                            }
                            else
                            {
                                patternDescription = "混合变化模式";
                            }
                        }

                        _debugLogger.Invoke($"DEBUG: BoundingArea {i}: History=0x{historyData:X8}, ChangeCount={changeCount}/{BoundingArea.HistoryFrames}, Pattern={patternDescription}");
                    }

                    // 打印总共有多少个合围区域
                    _debugLogger.Invoke($"DEBUG: Total bounding areas: {_boundingAreaCount}");
                }

                // 将更新后的CPU历史数据写回到GPU缓冲区
                _context!.UpdateSubresource(_boundingAreaHistory_cpu!, _boundingAreaHistoryBuffer!);

                // 4. 读回结果
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

                // 5. 状态迭代：将当前帧的输出状态复制到下一帧的输入状态
                _context?.CopyResource(_tileStateIn!, _tileStateOut!);

                // 6. 纹理迭代：将当前帧的纹理复制到下一帧的上一帧纹理
                _context?.CopyResource(_gpuTexPrev!, _gpuTexCurr!);

                // 7. 读回亮度数据
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

                // 添加最终同步点，确保所有GPU操作完成
                _context?.Flush();

                // 调试信息：记录图块状态统计
                if (_debugLogger != null && result.Count == 0)
                {
                    // 读取一些图块的状态来了解检测情况
                    _context?.CopyResource(_tileStateInReadback!, _tileStateIn!);
                    var stateMap = _context?.Map(_tileStateInReadback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                    if (stateMap.HasValue)
                    {
                        const int historyElementSize = 16; // sizeof(uint4)

                        _debugLogger.Invoke($"DEBUG: Tile states (first 10 tiles):");
                        for (int i = 0; i < Math.Min(10, _tilesX * _tilesY); i++) // 只检查前10个图块
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
                _debugLogger?.Invoke($"捕获和计算过程中发生异常: {ex.Message}");
                _debugLogger?.Invoke($"异常类型: {ex.GetType().Name}");
                _debugLogger?.Invoke($"异常堆栈: {ex.StackTrace}");
                return (result, new float[_tilesX * _tilesY]);
            }
            finally
            {
                // 确保释放信号量和重置标志
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

        // DPI检测方法
        private void DetectSystemDpiSettings()
        {
            try
            {
                // 尝试获取指定显示器的DPI设置
                float dpiX = 96.0f, dpiY = 96.0f;

                // 获取所有显示器信息
                var allScreens = System.Windows.Forms.Screen.AllScreens;

                // 检查目标显示器索引是否有效
                if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                {
                    var targetScreen = allScreens[_targetScreenIndex];

                    _debugLogger?.Invoke($"尝试获取显示器 {_targetScreenIndex} ({targetScreen.DeviceName}) 的DPI设置");
                    _debugLogger?.Invoke($"显示器边界: {targetScreen.Bounds}");

                    // 方法1: 使用GetDpiForMonitor API获取准确的显示器DPI（首选方法）
                    try
                    {
                        var bounds = targetScreen.Bounds;
                        var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

                        // 获取显示器句柄
                        IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

                        if (hMonitor != IntPtr.Zero)
                        {
                            uint monitorDpiX, monitorDpiY;
                            int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out monitorDpiX, out monitorDpiY);

                            if (result == 0) // S_OK
                            {
                                dpiX = monitorDpiX;
                                dpiY = monitorDpiY;
                                _debugLogger?.Invoke($"方法1成功: GetDpiForMonitor 返回 DPI {dpiX}x{dpiY}");
                            }
                            else
                            {
                                _debugLogger?.Invoke($"方法1: GetDpiForMonitor 失败，错误码: 0x{result:X8}，将尝试备用方法");
                            }
                        }
                        else
                        {
                            _debugLogger?.Invoke($"方法1: 无法获取显示器句柄");
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"方法1失败: {ex.Message}，将尝试备用方法");
                    }

                    // 方法2: 如果方法1失败，使用GetDpiForMonitor作为备用方法
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            var bounds = targetScreen.Bounds;
                            var centerPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

                            // 获取显示器句柄
                            IntPtr hMonitor = NativeMethods.MonitorFromPoint(centerPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

                            if (hMonitor != IntPtr.Zero)
                            {
                                uint monitorDpiX, monitorDpiY;
                                int result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_Effective_DPI, out monitorDpiX, out monitorDpiY);

                                if (result == 0) // S_OK
                                {
                                    dpiX = monitorDpiX;
                                    dpiY = monitorDpiY;
                                    _debugLogger?.Invoke($"方法2成功: GetDpiForMonitor 返回 DPI {dpiX}x{dpiY}");
                                }
                                else
                                {
                                    _debugLogger?.Invoke($"方法2: GetDpiForMonitor 失败，错误码: 0x{result:X8}");
                                }
                            }
                            else
                            {
                                _debugLogger?.Invoke($"方法2: 无法获取显示器句柄");
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"方法2失败: {ex.Message}");
                        }
                    }

                    /*
                    // 方法3: 如果方法1和方法2失败，尝试为特定显示器创建Graphics对象以获取其DPI（已注释掉）
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            // 获取显示器的边界矩形
                            var bounds = targetScreen.Bounds;
                            
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
                                        dpiX = graphics.DpiX;
                                        dpiY = graphics.DpiY;
                                        _debugLogger?.Invoke($"方法3成功: Graphics.FromHwnd 返回 DPI {dpiX}x{dpiY}");
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
                            _debugLogger?.Invoke($"方法3失败: {ex.Message}");
                        }
                    }
                    
                    // 方法4: 如果所有方法都失败，尝试使用Screen.Bounds和EnumDisplaySettings计算DPI（已注释掉）
                    if (dpiX == 96.0f && dpiY == 96.0f)
                    {
                        try
                        {
                            // 获取设备名称
                            string deviceName = targetScreen.DeviceName;
                            
                            _debugLogger?.Invoke($"方法4: 尝试通过EnumDisplaySettings获取显示器信息...");
                            _debugLogger?.Invoke($"方法4: Screen.Bounds = {targetScreen.Bounds}");
                            _debugLogger?.Invoke($"方法4: DeviceName = {deviceName}");
                            
                            // 使用EnumDisplaySettings获取真实的物理分辨率
                            NativeMethods.DEVMODE devMode = new NativeMethods.DEVMODE();
                            devMode.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
                            
                            if (NativeMethods.EnumDisplaySettings(deviceName, -1, ref devMode))
                            {
                                int physicalWidth = devMode.dmPelsWidth;
                                int physicalHeight = devMode.dmPelsHeight;
                                int logicalWidth = targetScreen.Bounds.Width;
                                int logicalHeight = targetScreen.Bounds.Height;
                                
                                _debugLogger?.Invoke($"方法4: DEVMODE 物理分辨率 = {physicalWidth}x{physicalHeight}");
                                _debugLogger?.Invoke($"方法4: Screen.Bounds 逻辑分辨率 = {logicalWidth}x{logicalHeight}");
                                
                                // 计算DPI缩放比例
                                float scaleX = (float)physicalWidth / logicalWidth;
                                float scaleY = (float)physicalHeight / logicalHeight;
                                
                                dpiX = 96.0f * scaleX;
                                dpiY = 96.0f * scaleY;
                                
                                _debugLogger?.Invoke($"方法4: 计算缩放比例 = {scaleX:F2}x{scaleY:F2}");
                                _debugLogger?.Invoke($"方法4成功: 最终DPI = {dpiX}x{dpiY}");
                            }
                            else
                            {
                                _debugLogger?.Invoke($"方法4: EnumDisplaySettings 失败");
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogger?.Invoke($"方法4失败: {ex.Message}");
                        }
                    }
                    */
                }

                // 如果未能获取到特定显示器的DPI，则使用主显示器的DPI
                if (dpiX == 96.0f && dpiY == 96.0f)
                {
                    using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        dpiX = graphics.DpiX;
                        dpiY = graphics.DpiY;
                        _debugLogger?.Invoke($"回退到主显示器DPI: {dpiX}x{dpiY}");
                    }
                }

                _dpiX = dpiX;
                _dpiY = dpiY;
                _dpiScaleX = _dpiX / 96.0f;
                _dpiScaleY = _dpiY / 96.0f;

                _debugLogger?.Invoke($"最终DPI设置: {_dpiX}x{_dpiY}, 缩放比例: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"DPI检测失败: {ex.Message}");
                _debugLogger?.Invoke("将使用默认DPI设置 (96x96)");

                // 使用默认值
                _dpiX = 96.0f;
                _dpiY = 96.0f;
                _dpiScaleX = 1.0f;
                _dpiScaleY = 1.0f;
            }
        }

        // 获取正确的屏幕边界（DXGI已经返回物理分辨率）
        private Rectangle GetCorrectScreenBounds(Rectangle dxgiBounds)
        {
            // DXGI的DesktopCoordinates返回的就是物理分辨率，不需要转换
            // 之前错误地乘以了DPI缩放比例

            _debugLogger?.Invoke($"DXGI返回的屏幕边界: {dxgiBounds}");
            _debugLogger?.Invoke($"系统DPI: {_dpiX}x{_dpiY}, 缩放比例: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
            _debugLogger?.Invoke($"物理分辨率: {dxgiBounds.Width}x{dxgiBounds.Height}");

            // 验证屏幕边界有效性 - 修正：允许负坐标（多显示器环境正常）
            if (dxgiBounds.Width <= 0 || dxgiBounds.Height <= 0 ||
                dxgiBounds.Width > 16384 || dxgiBounds.Height > 16384) // 最大合理分辨率限制
            {
                _debugLogger?.Invoke($"DEBUG: 无效的DXGI屏幕边界参数: {dxgiBounds}");
                // 返回一个安全的默认边界
                return new Rectangle(0, 0, 1920, 1080);
            }

            // 计算逻辑分辨率供参考
            int logicalWidth = (int)(dxgiBounds.Width / _dpiScaleX);
            int logicalHeight = (int)(dxgiBounds.Height / _dpiScaleY);

            _debugLogger?.Invoke($"逻辑分辨率: {logicalWidth}x{logicalHeight}");
            _debugLogger?.Invoke($"屏幕捕获将使用物理分辨率: {dxgiBounds.Width}x{dxgiBounds.Height}");

            return dxgiBounds;
        }

        // Eink屏幕检测和GDI+捕获方法
        private bool DetectEinkScreen(IDXGIOutput output)
        {
            try
            {
                _debugLogger?.Invoke("开始检测eink屏幕特性...");

                var desc = output.Description;
                string deviceName = desc.DeviceName.ToLower();

                // 获取显示器友好名称
                string friendlyName = GetFriendlyDisplayName(desc.DeviceName);
                if (!string.IsNullOrEmpty(friendlyName))
                {
                    _debugLogger?.Invoke($"显示器友好名称: '{friendlyName}'");
                }

                // 检测设备名称中的eink关键词
                bool isEink = deviceName.Contains("eink") || deviceName.Contains("e-ink") ||
                             deviceName.Contains("epd") || deviceName.Contains("electronic paper");

                if (isEink)
                {
                    _debugLogger?.Invoke($"检测到eink屏幕: {desc.DeviceName}");
                    return true;
                }

                // 检测刷新率特性
                try
                {
                    var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);
                    if (displayModeList.Any())
                    {
                        var primaryMode = displayModeList[0];
                        double refreshRate = (double)primaryMode.RefreshRate.Numerator / primaryMode.RefreshRate.Denominator;
                        _detectedRefreshRate = refreshRate;

                        _debugLogger?.Invoke($"检测到刷新率: {refreshRate:F2}Hz");
                        _debugLogger?.Invoke($"物理分辨率: {desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left}x{desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top}");

                        // eink屏幕通常有较低的刷新率（低于59Hz）
                        if (refreshRate < 59.0)
                        {
                            _debugLogger?.Invoke($"低刷新率({refreshRate:F2}Hz)可能为eink屏幕");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"刷新率检测失败: {ex.Message}");
                }

                _debugLogger?.Invoke($"未检测到eink屏幕特性: {desc.DeviceName}");
                return false;
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"eink屏幕检测失败: {ex.Message}");
                return false;
            }
        }

        // 获取显示器的友好名称
        private string GetFriendlyDisplayName(string deviceName)
        {
            try
            {
                NativeMethods.DISPLAY_DEVICE deviceInfo = new NativeMethods.DISPLAY_DEVICE();
                deviceInfo.cb = Marshal.SizeOf(deviceInfo);

                // 尝试获取显示器设备信息
                if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref deviceInfo, 0))
                {
                    // 如果获取成功，返回设备字符串（通常是友好名称）
                    return deviceInfo.DeviceString;
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"获取显示器友好名称失败: {ex.Message}");
            }

            // 如果无法获取友好名称，返回空字符串
            return string.Empty;
        }

        private bool InitializeGdiCapture(IDXGIOutput output)
        {
            try
            {
                _debugLogger?.Invoke("初始化GDI+屏幕捕获...");

                var desc = output.Description;
                var dxgiBounds = new Rectangle(
                    desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Top,
                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top
                );
                // 使用正确的屏幕边界（DXGI已返回物理分辨率）
                _screenBounds = GetCorrectScreenBounds(dxgiBounds);

                _screenW = _screenBounds.Width;
                _screenH = _screenBounds.Height;

                _debugLogger?.Invoke($"DXGI屏幕边界: {dxgiBounds}");
                _debugLogger?.Invoke($"物理屏幕边界: {_screenBounds}");
                _debugLogger?.Invoke($"DPI缩放比例: {_dpiScaleX:F2}x{_dpiScaleY:F2}");

                // 验证屏幕尺寸合理性，防止内存溢出
                long totalPixels = (long)_screenW * _screenH;
                long memoryRequired = totalPixels * 4; // 4 bytes per pixel for 32bpp
                long maxMemory = 512 * 1024 * 1024; // 512MB limit

                if (memoryRequired > maxMemory || _screenW > 16384 || _screenH > 16384)
                {
                    _debugLogger?.Invoke($"DEBUG: 屏幕尺寸过大或内存需求过高: {_screenW}x{_screenH}, 需要内存: {memoryRequired} bytes");
                    return false;
                }

                // 创建GDI+位图 - 使用物理尺寸
                try
                {
                    // RELEASE模式修复：添加更严格的参数验证
                    if (_screenW <= 0 || _screenH <= 0 || _screenW > 16384 || _screenH > 16384)
                    {
                        _debugLogger?.Invoke($"GDI+位图尺寸无效: {_screenW}x{_screenH}");
                        return false;
                    }

                    _gdiBitmap = new Bitmap(_screenW, _screenH, PixelFormat.Format32bppArgb);
                    if (_gdiBitmap == null)
                    {
                        _debugLogger?.Invoke("GDI+位图创建失败：位图为null");
                        return false;
                    }

                    _gdiGraphics = Graphics.FromImage(_gdiBitmap);
                    if (_gdiGraphics == null)
                    {
                        _debugLogger?.Invoke("GDI+图形对象创建失败：图形对象为null");
                        _gdiBitmap.Dispose();
                        _gdiBitmap = null;
                        return false;
                    }
                }
                catch (OutOfMemoryException ex)
                {
                    _debugLogger?.Invoke($"GDI+位图创建失败 - 内存不足: {ex.Message}");
                    return false;
                }
                catch (ArgumentException ex)
                {
                    _debugLogger?.Invoke($"GDI+位图创建失败 - 参数错误: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"GDI+位图创建失败 - 未知错误: {ex.Message}");
                    return false;
                }

                // 设置GDI+的DPI以匹配系统设置
                try
                {
                    _gdiGraphics.PageUnit = GraphicsUnit.Pixel;
                    _debugLogger?.Invoke($"GDI+位图创建成功，物理尺寸: {_screenW}x{_screenH}");
                }
                catch (Exception ex)
                {
                    _debugLogger?.Invoke($"GDI+设置失败: {ex.Message}");
                    CleanupGdiObjects();
                    return false;
                }

                _debugLogger?.Invoke("GDI+捕获初始化成功");
                return true;
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"GDI+捕获初始化失败: {ex.Message}");
                _debugLogger?.Invoke($"异常详情: {ex}");
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
                    _debugLogger?.Invoke("GDI+捕获对象未初始化");
                    return false;
                }

                // 统一坐标系统：使用相对于目标显示器的本地坐标(0,0)
                // GDI+捕获应该从显示器的左上角(0,0)开始，而不是使用虚拟屏幕的绝对坐标
                Rectangle localScreenBounds = new Rectangle(0, 0, _screenW, _screenH);

                // 添加详细的调试信息，对比Screen.AllScreens和DXGI的信息
                try
                {
                    var allScreens = System.Windows.Forms.Screen.AllScreens;
                    if (_targetScreenIndex >= 0 && _targetScreenIndex < allScreens.Length)
                    {
                        var targetScreen = allScreens[_targetScreenIndex];
                        _debugLogger?.Invoke($"DEBUG: Screen.AllScreens[{_targetScreenIndex}] - 设备: {targetScreen.DeviceName}, 主显示器: {targetScreen.Primary}, 边界: {targetScreen.Bounds}");
                    }
                    else
                    {
                        _debugLogger?.Invoke($"DEBUG: 目标显示器索引 {_targetScreenIndex} 超出 Screen.AllScreens 范围");
                    }
                }
                catch (Exception screenEx)
                {
                    _debugLogger?.Invoke($"DEBUG: 无法获取 Screen.AllScreens 信息: {screenEx.Message}");
                }

                _debugLogger?.Invoke($"开始GDI+屏幕捕获，本地坐标: {localScreenBounds}");
                _debugLogger?.Invoke($"原始虚拟坐标: {_screenBounds}");
                _debugLogger?.Invoke($"目标显示器索引: {_targetScreenIndex}");
                _debugLogger?.Invoke($"DPI缩放: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
                _debugLogger?.Invoke($"CopyFromScreen参数: 源({_screenBounds.X},{_screenBounds.Y}) -> 目标(0,0) 尺寸({localScreenBounds.Width}x{localScreenBounds.Height})");

                // 验证捕获参数有效性
                if (localScreenBounds.Width <= 0 || localScreenBounds.Height <= 0)
                {
                    _debugLogger?.Invoke($"DEBUG: 无效的屏幕分辨率: {localScreenBounds.Width}x{localScreenBounds.Height}");
                    return false;
                }

                // 验证屏幕边界参数 - 修正：允许负坐标（多显示器环境正常）
                if (_screenBounds.Width != _screenW || _screenBounds.Height != _screenH ||
                    _screenBounds.Width <= 0 || _screenBounds.Height <= 0)
                {
                    _debugLogger?.Invoke($"DEBUG: 无效的屏幕边界参数: Bounds={_screenBounds}, W={_screenW}, H={_screenH}");
                    return false;
                }

                // 验证GDI+位图尺寸
                if (_gdiBitmap.Width != _screenW || _gdiBitmap.Height != _screenH)
                {
                    _debugLogger?.Invoke($"DEBUG: GDI+位图尺寸不匹配 - 实际: {_gdiBitmap.Width}x{_gdiBitmap.Height}, 期望: {_screenW}x{_screenH}");
                    return false;
                }

                // 使用本地坐标进行捕获，避免坐标系统混乱
                Rectangle safeCaptureBounds = localScreenBounds;

                bool captureSuccess = false;
                int retryCount = 0;

                // 实现重试机制
                while (retryCount < MAX_RETRY_COUNT && !captureSuccess)
                {
                    try
                    {
                        // 使用GDI+捕获屏幕 - 统一使用目标显示器的本地坐标
                        // 从目标显示器的绝对位置开始，捕获到Bitmap的(0,0)位置
                        // RELEASE模式修复：添加额外的空值检查和异常处理
                        if (_gdiGraphics == null || _gdiBitmap == null)
                        {
                            _debugLogger?.Invoke("GDI+对象在捕获前被释放");
                            return false;
                        }

                        await Task.Run(() =>
                        {
                            try
                            {
                                // 确保所有对象在使用时仍然有效
                                if (_gdiGraphics != null && _gdiBitmap != null)
                                {
                                    _gdiGraphics.CopyFromScreen(
                                        _screenBounds.X,        // 源坐标：目标显示器在虚拟屏幕中的X位置
                                        _screenBounds.Y,        // 源坐标：目标显示器在虚拟屏幕中的Y位置  
                                        0, 0,                   // 目标坐标：Bitmap的左上角
                                        safeCaptureBounds.Size, // 尺寸：显示器的完整尺寸
                                        CopyPixelOperation.SourceCopy);
                                }
                            }
                            catch (Exception innerEx)
                            {
                                // RELEASE模式：捕获所有异常，防止闪退
                                _debugLogger?.Invoke($"GDI+捕获内部异常: {innerEx.Message}");
                                throw; // 重新抛出以便重试机制处理
                            }
                        });

                        captureSuccess = true;
                        _debugLogger?.Invoke($"CopyFromScreen完成，捕获区域: {safeCaptureBounds.X},{safeCaptureBounds.Y} -> 0,0 大小: {safeCaptureBounds.Width}x{safeCaptureBounds.Height}");
                    }
                    catch (Exception ex) // RELEASE模式：捕获所有异常类型
                    {
                        retryCount++;
                        _debugLogger?.Invoke($"GDI+捕获失败，正在重试 ({retryCount}/{MAX_RETRY_COUNT}): {ex.Message}");

                        if (retryCount < MAX_RETRY_COUNT)
                        {
                            await Task.Delay(RETRY_DELAY_MS);
                        }
                    }
                }

                if (!captureSuccess)
                {
                    _debugLogger?.Invoke("DEBUG: GDI+捕获多次失败，放弃本次捕获");
                    return false;
                }

                // 验证捕获的位图尺寸
                if (_gdiBitmap.Width != _screenW || _gdiBitmap.Height != _screenH)
                {
                    _debugLogger?.Invoke($"警告: 位图尺寸不匹配 - 实际: {_gdiBitmap.Width}x{_gdiBitmap.Height}, 期望: {_screenW}x{_screenH}");
                }

                // RELEASE模式修复：添加额外的位图锁定安全检查
                if (_gdiBitmap == null)
                {
                    _debugLogger?.Invoke("GDI+位图对象为空，无法锁定");
                    return false;
                }

                // 将GDI+位图数据复制到D3D纹理
                BitmapData? bitmapData = null;
                try
                {
                    bitmapData = _gdiBitmap.LockBits(new Rectangle(0, 0, _screenW, _screenH),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                    if (bitmapData == null || bitmapData.Scan0 == IntPtr.Zero)
                    {
                        _debugLogger?.Invoke("位图数据锁定失败或扫描指针为空");
                        return false;
                    }

                    _debugLogger?.Invoke($"位图数据锁定成功，Stride: {bitmapData.Stride}, 扫描线大小: {bitmapData.Stride * _screenH}");

                    // RELEASE模式修复：添加D3D对象空值检查
                    if (_device == null || _context == null)
                    {
                        _debugLogger?.Invoke("D3D设备或上下文为空，无法更新纹理");
                        return false;
                    }

                    // 更新D3D纹理
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

                    // 如果纹理不存在或尺寸不匹配，创建新的
                    if (_gpuTexCurr == null || _gpuTexCurr.Description.Width != texDesc.Width || _gpuTexCurr.Description.Height != texDesc.Height)
                    {
                        // 线程安全地释放旧纹理
                        var oldTex = _gpuTexCurr;
                        _gpuTexCurr = null;

                        if (oldTex != null)
                        {
                            _debugLogger?.Invoke("释放旧纹理，创建新纹理");
                            // 延迟释放以避免立即内存压力 - 修复async警告
                            var _ = Task.Delay(50).ContinueWith(t => oldTex.Dispose());
                        }

                        try
                        {
                            _gpuTexCurr = _device.CreateTexture2D(texDesc);
                            _debugLogger?.Invoke($"D3D纹理创建成功: {_screenW}x{_screenH}");
                        }
                        catch (Exception texEx)
                        {
                            _debugLogger?.Invoke($"D3D纹理创建失败: {texEx.Message}");
                            return false;
                        }
                    }

                    // RELEASE模式修复：安全的纹理数据更新
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
                            _debugLogger?.Invoke($"D3D纹理更新成功，RowPitch: {box.RowPitch}");
                        }
                        else
                        {
                            _debugLogger?.Invoke("纹理数据指针为空或纹理对象为空");
                            return false;
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _debugLogger?.Invoke($"D3D纹理更新失败: {updateEx.Message}");
                        return false;
                    }

                    return true;
                }
                finally
                {
                    // RELEASE模式修复：安全的位图解锁
                    if (bitmapData != null)
                    {
                        try
                        {
                            _gdiBitmap.UnlockBits(bitmapData);
                        }
                        catch (Exception unlockEx)
                        {
                            _debugLogger?.Invoke($"位图解锁失败: {unlockEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"GDI+屏幕捕获失败: {ex.Message}");
                _debugLogger?.Invoke($"异常详情: {ex}");
                return false;
            }
        }

        private bool TryAlternativeCaptureMethods(IDXGIOutput? output)
        {
            _debugLogger?.Invoke("尝试替代捕获方法...");

            // 检查输出是否为空
            if (output == null)
            {
                _debugLogger?.Invoke("输出对象为空，无法尝试替代捕获方法");
                return false;
            }

            // 方法1: 尝试使用不同的显示模式
            try
            {
                _debugLogger?.Invoke("尝试使用不同的显示模式...");
                var displayModeList = output.GetDisplayModeList(Format.B8G8R8A8_UNorm, DisplayModeEnumerationFlags.Interlaced | DisplayModeEnumerationFlags.Scaling);

                if (displayModeList.Any())
                {
                    // 尝试使用第一个支持的显示模式
                    var mode = displayModeList[0];
                    double refreshRate = (double)mode.RefreshRate.Numerator / mode.RefreshRate.Denominator;
                    _debugLogger?.Invoke($"尝试显示模式: {mode.Width}x{mode.Height}@{refreshRate:F2}Hz");

                    // 这里可以添加更多显示模式尝试逻辑
                    return false; // 暂时返回false，继续尝试其他方法
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"显示模式尝试失败: {ex.Message}");
            }

            // 方法2: 尝试GDI+捕获
            if (InitializeGdiCapture(output))
            {
                _useGdiCapture = true;
                _debugLogger?.Invoke("切换到GDI+捕获模式");
                return true;
            }

            return false;
        }

        // 获取当前鼠标位置
        private Point GetMousePosition()
        {
            Point point = new Point(-1, -1);
            try
            {
                bool result = NativeMethods.GetCursorPos(out point);
                _debugLogger?.Invoke($"获取鼠标位置: ({point.X}, {point.Y}), 调用结果: {result}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"获取鼠标位置失败: {ex.Message}");
            }
            return point;
        }

        // 获取文本光标位置
        private Point GetCaretPosition()
        {
            try
            {
                // 每CaretCheckInterval毫秒检查一次文本光标位置，避免频繁调用API
                if (DateTime.Now - _lastCaretCheck > TimeSpan.FromMilliseconds(CaretCheckInterval))
                {

                    _lastCaretCheck = DateTime.Now;

                    // 获取当前焦点窗口
                    IntPtr focusWindow = NativeMethods.GetFocus();
                    _debugLogger?.Invoke($"[Caret] 获取焦点窗口句柄: {focusWindow}");

                    // 尝试使用GetGUIThreadInfo获取更全面的信息
                    uint guiThread = 0;
                    Point guiCaretPos = new Point(-1, -1);
                    bool guiInfoAvailable = false;
                    IntPtr foregroundWindow = IntPtr.Zero; // 移到try块外部

                    try
                    {
                        NativeMethods.GUITHREADINFO guiThreadInfo = new NativeMethods.GUITHREADINFO();
                        guiThreadInfo.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO));

                        foregroundWindow = NativeMethods.GetForegroundWindow();
                        _debugLogger?.Invoke($"[Caret] 前台窗口句柄: {foregroundWindow}");

                        uint processId; uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out processId);
                        _debugLogger?.Invoke($"[Caret] 前台窗口线程ID: {foregroundThread}");

                        if (NativeMethods.GetGUIThreadInfo(foregroundThread, ref guiThreadInfo))
                        {
                            guiThread = foregroundThread;
                            _debugLogger?.Invoke($"[Caret] GetGUIThreadInfo成功: hwndCaret={guiThreadInfo.hwndCaret}, rcCaret=({guiThreadInfo.rcCaret.Left},{guiThreadInfo.rcCaret.Top},{guiThreadInfo.rcCaret.Right},{guiThreadInfo.rcCaret.Bottom})");

                            if (guiThreadInfo.hwndCaret != IntPtr.Zero)
                            {
                                // 使用GUI线程信息中的光标位置
                                guiCaretPos = new Point(guiThreadInfo.rcCaret.Left, guiThreadInfo.rcCaret.Bottom);
                                bool convertResult = NativeMethods.ClientToScreen(guiThreadInfo.hwndCaret, ref guiCaretPos);
                                _debugLogger?.Invoke($"[Caret] GUI线程光标位置转换结果: ({guiCaretPos.X}, {guiCaretPos.Y}), 转换结果: {convertResult}");
                                guiInfoAvailable = true;
                                _lastGuiCaretCheck = DateTime.Now;
                            }
                            else
                            {
                                _debugLogger?.Invoke($"[Caret] GUI线程信息中无有效光标窗口句柄");
                            }
                        }
                        else
                        {
                            _debugLogger?.Invoke($"[Caret] GetGUIThreadInfo失败");
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"[Caret] 获取GUI线程信息失败: {ex.Message}");
                        _debugLogger?.Invoke($"[Caret] 异常详情: {ex}");
                    }

                    // 更新最后记录的光标位置
                    _lastFocusWindow = focusWindow;
                    _lastGuiThread = guiThread;
                    _lastGuiCaretPosition = guiCaretPos;
                    _lastCaretPosition = guiInfoAvailable ? guiCaretPos : new Point(-1, -1);
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[Caret] 获取文本光标位置失败: {ex.Message}");
                _debugLogger?.Invoke($"[Caret] 异常详情: {ex}");
                _lastCaretPosition = new Point(-1, -1);
            }
            return _lastCaretPosition;
        }

        // 日志记录光标、鼠标和输入法位置信息
        private void LogCursorPositionInfo()
        {
            try
            {
                // 获取鼠标位置
                Point mousePos = GetMousePosition();
                _lastMousePosition = mousePos;

                // 获取文本光标位置
                Point caretPos = GetCaretPosition();
                _lastCaretPosition = caretPos;

                // 每ImeCheckInterval毫秒检查一次输入法位置，避免频繁调用API
                if (DateTime.Now - _lastImeCheck > TimeSpan.FromMilliseconds(ImeCheckInterval))
                {
                    _lastImeCheck = DateTime.Now;
                    // 获取输入法窗口位置
                    Rectangle imeRect = GetImeWindowRect();
                    _lastImeRect = imeRect;
                }

                // 仅在调试模式下输出详细信息
                _debugLogger?.Invoke($"[CursorInfo] Mouse: ({mousePos.X}, {mousePos.Y}), Caret: ({caretPos.X}, {caretPos.Y}), ImeRect: {_lastImeRect}");
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[CursorInfo] 记录光标信息失败: {ex.Message}");
            }
        }

        // 获取输入法窗口位置
        private Rectangle GetImeWindowRect()
        {
            try
            {
                // 获取当前焦点窗口
                IntPtr focusWindow = NativeMethods.GetFocus();
                if (focusWindow == IntPtr.Zero)
                {
                    return Rectangle.Empty;
                }

                // 获取输入法窗口句柄
                IntPtr imeWindow = NativeMethods.ImmGetDefaultIMEWnd(focusWindow);
                if (imeWindow == IntPtr.Zero)
                {
                    return Rectangle.Empty;
                }

                // 获取候选词窗口位置
                NativeMethods.RECT imeRect;
                int result = NativeMethods.SendMessage(imeWindow, NativeMethods.WM_IME_CONTROL, NativeMethods.IMC_GETCANDIDATEPOS, out imeRect);
                if (result != 0)
                {
                    return imeRect.ToRectangle();
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"[IME] 获取输入法窗口位置失败: {ex.Message}");
            }
            return Rectangle.Empty;
        }

        // 清理旧的D3D日志文件，保留最新的一个
        private void CleanupOldD3DLogFiles(string logDirectory)
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "d3d_debug_*.log");
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
                // 忽略清理失败的情况
            }
        }

        /// <summary>
        /// 获取当前主显示器的刷新率。
        /// </summary>
        /// <returns>主显示器的刷新率，如果获取失败则返回0.0。</returns>
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
                    // 检查factory是否为null以避免CS8602警告
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
                                // 添加空值检查以消除CS8602警告
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

        // RELEASE模式修复：添加专门的GDI+对象清理方法
        private void CleanupGdiObjects()
        {
            try
            {
                // 安全地清理GDI+图形对象
                if (_gdiGraphics != null)
                {
                    try
                    {
                        _gdiGraphics.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"GDI+图形对象清理失败: {ex.Message}");
                    }
                    _gdiGraphics = null;
                }

                // 安全地清理GDI+位图对象
                if (_gdiBitmap != null)
                {
                    try
                    {
                        _gdiBitmap.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _debugLogger?.Invoke($"GDI+位图对象清理失败: {ex.Message}");
                    }
                    _gdiBitmap = null;
                }
            }
            catch (Exception ex)
            {
                _debugLogger?.Invoke($"GDI+对象清理时发生异常: {ex.Message}");
            }
        }

        // DXGI格式验证方法 - 检查桌面格式是否受支持
        private bool IsValidDesktopFormat(Format format)
        {
            // 定义支持的桌面格式列表
            var supportedFormats = new[]
            {
                Format.B8G8R8A8_UNorm,     // 最常见的桌面格式
                Format.B8G8R8A8_UNorm_SRgb, // sRGB版本
                Format.R8G8B8A8_UNorm,     // RGBA格式
                Format.R8G8B8A8_UNorm_SRgb, // sRGB版本
                Format.B8G8R8X8_UNorm,     // 无alpha通道格式
                Format.B8G8R8X8_UNorm_SRgb, // sRGB版本
                Format.R10G10B10A2_UNorm,  // 10位色深格式
                Format.R16G16B16A16_UNorm  // 16位色深格式（较少见）
            };

            return supportedFormats.Contains(format);
        }

        // 动态超时优化 - 根据捕获历史调整超时时间
        private int GetOptimizedTimeout()
        {
            _captureAttemptCount++;

            // 计算成功率
            double successRate = _captureAttemptCount > 0 ? (double)_captureSuccessCount / _captureAttemptCount : 1.0;

            // 如果最近有成功捕获，使用基础超时
            if ((DateTime.Now - _lastSuccessfulCapture).TotalSeconds < 5.0 && successRate > 0.8)
            {
                _consecutiveTimeouts = 0;
                _consecutiveFailures = 0;
                return _baseTimeoutMs;
            }

            // 如果连续失败，逐步增加超时时间
            if (_consecutiveFailures > 3)
            {
                int increasedTimeout = Math.Min(_baseTimeoutMs + (_consecutiveFailures - 3) * 50, _maxTimeoutMs);
                _debugLogger?.Invoke($"WARNING: Increasing timeout to {increasedTimeout}ms due to {_consecutiveFailures} consecutive failures");
                return increasedTimeout;
            }

            // 如果连续超时，增加超时时间
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

            // 释放状态缓冲区
            _tileStateInUAV?.Dispose();
            _tileStateIn?.Dispose();
            _tileStateOutUAV?.Dispose();
            _tileStateOut?.Dispose();

            // 释放刷新列表缓冲区
            _refreshListUAV?.Dispose();
            _refreshList?.Dispose();
            _refreshCounterUAV?.Dispose();
            _refreshCounter?.Dispose();
            _refreshListReadback?.Dispose();
            _refreshCounterReadback?.Dispose();
            _tileStateInReadback?.Dispose();

            // 释放亮度缓冲区
            _tileBrightnessUAV?.Dispose();
            _tileBrightness?.Dispose();
            _tileBrightnessReadback?.Dispose();

            // 释放GPU端状态管理缓冲区
            _tileStableCountersUAV?.Dispose();
            _tileStableCountersBuffer?.Dispose();
            _tileProtectionExpiryUAV?.Dispose();
            _tileProtectionExpiryBuffer?.Dispose();

            _gpuTexCurr?.Dispose();
            _gpuTexPrev?.Dispose();
            _deskDup?.Dispose();
            _context?.Dispose();
            _device?.Dispose();

            // 释放GDI+资源
            CleanupGdiObjects();
        }
    }
}
