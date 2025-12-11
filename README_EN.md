# TbEinkSuperFlushTurbo - E-Ink Screen Ghosting Reduction Tool

<div align="right">
  <strong>Language:</strong> <a href="README.md">中文</a> | English
</div>

## Project Overview

TbEinkSuperFlushTurbo is an intelligent ghosting reduction tool specifically designed for e-ink display devices. Through DirectX GPU acceleration and smart area detection algorithms, it effectively reduces screen ghosting and flickering, providing a better visual experience.

### 🖥️ Primary Target Device
**This tool is specifically developed and optimized for the Kaleido color e-ink display on ThinkBook Plus Gen4 (Twist)**, while maintaining compatibility with other e-ink display devices.

## Core Features

### 🎯 Intelligent Ghosting Reduction
- **GPU Acceleration**: Utilizes DirectX 11 and compute shaders for high-performance image processing
- **Tiled Detection**: Divides screen into 8x8 pixel tiles for precise change detection
- **Smart Refresh Algorithm**: Multi-frame stability detection to avoid over-refreshing

### 💡 Working Principle
- **Non-Waveform Driving Technology**: Uses the inverse semi-transparent brightness color (black/white) of the current stable area as the refresh color, briefly displayed for 100ms to drive the e-ink screen to quickly disturb ink particles, achieving ghosting reduction effect
- **Local Refresh Advantage**: Employs local refresh instead of full-screen refresh, reducing screen flickering
- **Single Inverse Color Refresh**: Inverse color refresh flashes only once, significantly reducing interference compared to the system driver's 4 flashes (black→white→black→white)

### 🚀 Turbo Features
- **GPU Parallel Computing**: Pixel difference comparison of each block and traversal operations on blocks are all performed in GPU, greatly reducing the performance impact of calculating millions of pixels across the full screen
- **Scrolling Area Suppression Refresh**: Based on small blocks, it adds fixed partitioned adjacent blocks to form an enclosing area. For areas where n frames out of m frames need to be refreshed within the enclosing area (considered as scrolling), the blocks within the area are not refreshed. After scrolling stops, refreshing will occur, greatly reducing the interference caused by temporarily displaying inverse colors on text during scrolling

### 🚫 Smart Area Filtering
- **Cursor Area Exclusion**: Automatically excludes areas around mouse and text cursor (3-tile radius)
  - ⚠️ **Limitation**: Only supports standard Windows text cursor APIs. Third-party applications using non-standard cursor APIs may not be detectable

### ⚡ Performance Optimization
- **First Refresh Delay Mechanism**: To avoid full-screen refresh caused by massive area changes at startup, the first refresh trigger has an appropriate delay to ensure that only areas that truly need refresh are processed
- **Cooldown Period Mechanism**: Refreshed blocks enter a short-term cooldown period to prevent repeated refreshing, further reducing screen flickering and improving user experience
- **User Full Refresh Smart Recognition**: Considers 95% area change as user manually/automatically using the computer's original E-ink driver for full refresh, resets change statistics at this time to avoid repeated full-screen refreshes
- **Minimal Interference**: Only refreshes necessary areas to reduce screen flickering

### 🎨 Visual Feedback
- **Real-time Overlay**: Refreshed areas show brief color overlay (100ms)
- **System Tray Icon**: Supports minimizing to system tray
- **Detailed Logging**: Complete operation logs and performance statistics

## Technical Features

### Brightness Difference Detection Algorithm
This project employs a GPU-based brightness difference detection algorithm that efficiently analyzes screen content through DirectX 11 compute shaders:

1. **Tiled Processing**: The entire screen is divided into 8x8 pixel tiles for parallel processing and localized refresh
2. **Pixel-level Comparison**: Compare pixels between consecutive frames for each tile, calculating RGB channel differences
3. **Luminance Calculation**: Use the standard luminance formula Y = 0.299×R + 0.587×G + 0.114×B to calculate the brightness of each pixel
4. **Average Difference Threshold**: Set pixel difference threshold, changes exceeding the threshold are considered significant
5. **Sliding Window Average**: Employ a 4-frame sliding window average algorithm to smooth instantaneous changes and improve detection stability
6. **Multi-frame Stability Detection**: Only areas that remain stable for 3 or more consecutive frames will trigger a refresh operation
7. **Dynamic Cooldown Period**: Dynamically calculates cooldown period based on overlay display time, ensuring refreshed blocks won't be refreshed again during the cooldown period
8. **Noise Filtering**: Intelligent identification and filtering of screen noise

This algorithm fully leverages GPU parallel computing power to achieve high processing efficiency while ensuring detection accuracy.

### DirectX Integration
- **Vortice.DirectX**: Uses modern .NET DirectX wrappers
- **Compute Shaders**: GPU parallel processing of image data
- **High-performance Capture**: Low-latency screen capture and processing

### System Compatibility
- **High DPI Support**: Automatically adapts to different DPI scaling settings
- **Multi-architecture Support**: Supports x64 architecture
- **Windows 10/11**: Optimized for modern Windows systems
- **Smart Screen Detection**: Automatically detects screen refresh rates, DPI scaling factors, and supports
  multi-monitor environments
- **E-ink Screen Auto-Recognition**: Intelligently identifies e-ink display devices through refresh rate
  characteristics (less than 55Hz)
- **Smart Screen Detection**: Automatically detects screen refresh rates, DPI scaling factors, and supports
  multi-monitor environments
- **E-ink Screen Auto-Recognition**: Intelligently identifies e-ink display devices through refresh rate
  characteristics (less than 55Hz)

## Usage

### Basic Operation
1. Run `TbEinkSuperFlushTurbo.exe`
2. Click **Start** button to begin monitoring
3. Click **Stop** button to stop monitoring
4. Use tray icon to control show/hide

### Advanced Settings
- Supports command-line parameter configuration
- Customizable detection parameters (tile size, thresholds, etc.)
- Adjustable log levels

### Configuration Files

The application uses a configuration file stored in the application directory:

1. `config.json` - Contains all application settings:
   ```json
   {
     "PixelDelta": 10,
     "PollInterval": 500,
     "TileSize": 8,
     "ScreenIndex": 0,
     "ToggleHotkey": 117
   }
   ```
   Where:
   - `PixelDelta`: Sensitivity threshold for pixel color differences (2-25)
   - `PollInterval`: Screen capture interval in milliseconds (200-5000)
   - `TileSize`: Size of detection blocks in pixels (8-64)
   - `ScreenIndex`: Target monitor index for multi-monitor setups (0 for primary)
   - `ToggleHotkey`: Virtual key code for the toggle hotkey (117 = F6)

If this file doesn't exist, the application will create it with default values on first run.

## System Requirements

- **Operating System**: Windows 10 or higher
- **.NET Version**: .NET 8.0 or higher
- **Graphics Card**: DirectX 11 compatible graphics card
- **Permissions**: Administrator privileges recommended for best results
- **Recommended Device**: ThinkBook Plus Gen4 (Twist) or other devices equipped with Kaleido e-ink displays

## Build Instructions

### Development Environment
- Visual Studio 2022 or higher
- .NET 8.0 SDK
- Windows 10 SDK

### Dependencies
- Vortice.Direct3D11 (3.6.2)
- Vortice.DXGI (3.6.2)
- Vortice.D3DCompiler (3.6.2)

### Build Steps
```bash
# Clone project
git clone [project address]

# Enter project directory
cd TbEinkSuperFlushTurbo

# Restore dependencies
dotnet restore

# Build project
dotnet build

# Run project
dotnet run
```

## Performance

### Detection Performance
- **Detection Cycle**: 515ms (configurable)
- **Processing Latency**: < 50ms
- **Memory Usage**: < 100MB

### Refresh Optimization
- **Reduced Refresh Count**: 60-80% reduction compared to traditional methods
- **Reduced Flickering**: Smart protection period mechanism
- **Extended Screen Life**: Minimizes unnecessary refresh operations

## Troubleshooting

### ⚙️ Automatic Detection Features

#### Screen Detection Mechanism

- **Refresh Rate Detection**: Automatically scans all connected displays to identify refresh rate characteristics
- **E-ink Screen Recognition**: Intelligently identifies e-ink display devices through low refresh rate
  characteristics (less than 55Hz)
- **Multi-monitor Support**: Automatically selects E-ink screens for processing in multi-monitor environments
- **DPI Adaptation**: Automatically detects and adapts to different DPI scaling settings

#### Detection Logic

1. **System Enumeration**: Enumerates all display information through Windows API
2. **Feature Recognition**: Identifies E-ink screens based on refresh rate, resolution, and other characteristics
3. **Priority Processing**: Prioritizes processing of identified E-ink screens, supports multiple E-ink screen
   environments
4. **Dynamic Adaptation**: Automatically re-detects when display connection status changes

### Common Issues
1. **Program Won't Start**: Check if .NET 8.0 runtime is installed
2. **DirectX Initialization Failed**: Update graphics card drivers
3. **Inaccurate Detection**: Adjust DPI settings or detection parameters
4. **Cursor Detection Failed**: Some third-party applications use non-standard cursor APIs that cannot be detected
5. **Poor Display Effect**: Works best with light themes + Intel Graphics Control Center adjusted contrast enhancement to make the interface pure white, or using a white background high contrast theme

### ⚠️ Special Tips
- **System Feature Interference**: Power saving mode, night mode, and the following two system brightness options (enabled by default, which also affect E-ink screens) may impact display quality:
  - "Adjust brightness according to content"
  - "Adjust brightness according to ambient light"
- **Display Effect Explanation**: These features create numerous gray wavy lines on light-colored interfaces. The pure white displayed after ghosting clearance may contrast with these gray interfaces, appearing like white ghosting.

### Debug Information
- Check detailed log files in the `Logs` directory
- Review `debug_output.txt` for debug information
- Use Visual Studio debugger for in-depth analysis

## Changelog

### Latest Version
- ✅ GPU-accelerated image processing
- ✅ Smart area filtering
- ✅ High DPI support
- ✅ System tray icon functionality
- ✅ Detailed logging system

## Contributing

Feel free to submit Issues and Pull Requests to improve the project. Before contributing:
1. Read project documentation and code comments
2. Test all changes locally
3. Follow existing code style

## License

This project uses an open-source license. See LICENSE file for details.

## Contact

For questions or suggestions, please contact via:
- Submit GitHub Issue
- Send detailed problem descriptions and log files

---

**Note**: This tool is specifically optimized for e-ink display devices. Effects may not be noticeable on regular LCD/LED screens. It is recommended to disable other screen refresh tools before use to avoid conflicts.