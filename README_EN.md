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

### 🚫 Smart Area Filtering
- **Cursor Area Exclusion**: Automatically excludes areas around mouse and text cursor (3-tile radius)
  - ⚠️ **Limitation**: Only supports standard Windows text cursor APIs. Third-party applications using non-standard cursor APIs may not be detectable
- **IME Candidate Window Detection**: Intelligently identifies and excludes input method candidate window areas
- **System UI Recognition**: Automatically recognizes system UI areas like taskbars

### ⚡ Performance Optimization
- **Protection Period Mechanism**: Refreshed blocks enter protection period to avoid repeated refreshing
- **Adaptive Threshold**: Triggers reset when 95% area changes to avoid conflicts with system refresh
- **Minimal Interference**: Only refreshes necessary areas to reduce screen flickering

### 🎨 Visual Feedback
- **Real-time Overlay**: Refreshed areas show brief color overlay (100ms)
- **System Tray Icon**: Supports minimizing to system tray
- **Detailed Logging**: Complete operation logs and performance statistics

## Technical Features

### Advanced Algorithms
- **Multi-frame Stability Detection**: Based on 4-frame average window and 3-frame stability requirement
- **Dynamic Protection Period**: Dynamically calculates protection period based on overlay display time
- **Noise Filtering**: Intelligent identification and filtering of screen noise

### DirectX Integration
- **Vortice.DirectX**: Uses modern .NET DirectX wrappers
- **Compute Shaders**: GPU parallel processing of image data
- **High-performance Capture**: Low-latency screen capture and processing

### System Compatibility
- **High DPI Support**: Automatically adapts to different DPI scaling settings
- **Multi-architecture Support**: Supports x64 architecture
- **Windows 10/11**: Optimized for modern Windows systems

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

### Common Issues
1. **Program Won't Start**: Check if .NET 8.0 runtime is installed
2. **DirectX Initialization Failed**: Update graphics card drivers
3. **Inaccurate Detection**: Adjust DPI settings or detection parameters
4. **Cursor Detection Failed**: Some third-party applications use non-standard cursor APIs that cannot be detected

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