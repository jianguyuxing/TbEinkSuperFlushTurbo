using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TbEinkSuperFlushTurbo
{
    public class OverlayForm : Form
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

        // Use Dictionary to store tiles for improved lookup efficiency, key is (bx,by) tuple, value is brightness data
        readonly Dictionary<(int bx, int by), float> _tiles = new Dictionary<(int bx, int by), float>();
        readonly List<(int bx, int by)> _expiredTiles = new List<(int bx, int by)>(); // Expired tiles for current drawing cycle
        readonly List<CancellationTokenSource> _batchCancellationTokenSources = new List<CancellationTokenSource>(); // Record cancellation tokens for each batch of tiles
        Bitmap? _overlayBitmap; // Accumulated bitmap
        private readonly object _bitmapLock = new object(); // Lock for synchronized access to _bitmap
        private bool _isDisplaying = false; // Flag indicating whether refresh color is being displayed
        public int TileCount => _tiles.Count;
        readonly int _tileSize, _screenW, _screenH, _noiseDensity, _noisePointInterval;
        readonly int _screenIndex; // Added screen index field
        readonly double _scaleX, _scaleY; // Scaling ratio from physical resolution to logical resolution
        // Added fields
        readonly Color _overlayBaseColor;
        readonly Action<string>? _logger;

        public bool IsDisplaying => _isDisplaying;
        public int TileSize => _tileSize;

        public void UpdateContent(List<(int bx, int by)> tiles, float[]? brightnessData = null)
        {
            bool addedNewTiles = false;

            // Add new tiles to the existing Dictionary, use ContainsKey for improved lookup efficiency (O(1))
            foreach (var tile in tiles)
            {
                // Calculate tile index
                int tilesX = (_screenW + _tileSize - 1) / _tileSize;
                int tileIdx = tile.by * tilesX + tile.bx;

                // Get the brightness value for this tile
                float brightness = 0.5f; // Default brightness
                if (brightnessData != null && tileIdx < brightnessData.Length)
                {
                    brightness = brightnessData[tileIdx];
                }

                // Check if the tile is already in the display list, using Dictionary's ContainsKey method, O(1) complexity
                if (!_tiles.ContainsKey(tile))
                {
                    _tiles[tile] = brightness;
                    addedNewTiles = true;
                }
            }

            // Create a unified timer for the current batch of tiles, regardless of whether new tiles are added
            // Ensure that each batch of tiles has an expiration time set for each call
            if (tiles.Count > 0)
            {
                // Create a unified timer for this new batch of tiles
                CancellationTokenSource cts = new CancellationTokenSource();
                _batchCancellationTokenSources.Add(cts);

                // Start a unified timer to clear this batch of tiles
                _ = Task.Run(async () => {
                    try
                    {
                        await Task.Delay(MainForm.OVERLAY_DISPLAY_TIME, cts.Token);
                        // Time's up, mark this batch of tiles as expired
                        MarkTilesAsExpired(tiles, cts);
                    }
                    catch (OperationCanceledException)
                    {
                        // Task was canceled, normal case
                    }
                });
            }

            // Update display (accumulative mode, do not clear previous display)
            UpdateVisuals();
            if (!_isDisplaying)
            {
                _isDisplaying = true;
            }
            if (addedNewTiles)
            {
                _logger?.Invoke($"DEBUG: Refresh color display started, will show for {MainForm.OVERLAY_DISPLAY_TIME}ms, current tile count: {_tiles.Count}");
            }
        }

        // Safely mark a batch of tiles as expired from the UI thread
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
            // Mark these tiles as expired
            int expiredCount = 0;
            foreach (var tile in tiles)
            {
                // Use Dictionary's Remove method, O(1) complexity
                if (_tiles.Remove(tile))
                {
                    _expiredTiles.Add(tile);
                    expiredCount++;
                }
            }

            // Erase expired tiles from the bitmap
            ClearExpiredTilesFromBitmap();

            // Remove this token from the cancellation token list
            _batchCancellationTokenSources.Remove(cts);
            cts.Dispose();

            // Update display to clear expired tiles
            UpdateVisuals();

            _logger?.Invoke($"DEBUG: Partial refresh color expired, expired tile count this time: {expiredCount}, remaining tile count: {_tiles.Count}, expired tile count: {_expiredTiles.Count}");
            // Clean up temporary expired tiles list for next refresh cycle
            _expiredTiles.Clear();
        }

        public void HideOverlay()
        {
            // Cancel all ongoing timers
            foreach (var cts in _batchCancellationTokenSources)
            {
                cts.Cancel();
            }

            // Clean up all data
            _tiles.Clear();
            _expiredTiles.Clear();
            foreach (var cts in _batchCancellationTokenSources)
            {
                cts.Dispose();
            }
            _batchCancellationTokenSources.Clear();

            // Clean up bitmap resources
            lock (_bitmapLock)
            {
                _overlayBitmap?.Dispose();
                _overlayBitmap = null;
            }
            _isDisplaying = false;
            UpdateVisuals();
            _logger?.Invoke("DEBUG: Refresh color forcibly hidden");
        }

        // Clear expired tiles from the bitmap
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
                        // Convert physical coordinates to logical coordinates
                        int sx = (int)(tile.bx * _tileSize / _scaleX);
                        int sy = (int)(tile.by * _tileSize / _scaleY);
                        int w = Math.Min((int)(_tileSize / _scaleX), _screenW - sx);
                        int h = Math.Min((int)(_tileSize / _scaleY), _screenH - sy);

                        // Use transparent color to clear expired tile areas
                        using (var brush = new SolidBrush(Color.Transparent))
                            g.FillRectangle(brush, sx, sy, w, h);
                    }
                }
            }
        }

        public OverlayForm(int tileSize, int screenWidth, int screenHeight, int noiseDensity, int noisePointInterval, 
            Color overlayBaseColor, Action<string> logger, int screenIndex, double scaleX, double scaleY)
        {
            _tileSize = tileSize;
            _screenW = screenWidth;
            _screenH = screenHeight;
            _noiseDensity = noiseDensity;
            _noisePointInterval = noisePointInterval;
            _overlayBaseColor = overlayBaseColor;
            _logger = logger;
            _screenIndex = screenIndex;
            _scaleX = scaleX;
            _scaleY = scaleY;
            
            // Initialize dictionaries
            _tiles = new Dictionary<(int bx, int by), float>();
            _expiredTiles = new List<(int bx, int by)>();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            
            // Set window position and size to match the specified screen
            Screen[] allScreens = Screen.AllScreens;
            Screen targetScreen = allScreens.Length > screenIndex ? allScreens[screenIndex] : Screen.PrimaryScreen!;
            Location = targetScreen.Bounds.Location;
            Size = targetScreen.Bounds.Size;

            // Set WS_EX_LAYERED extended style
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
            // Ensure window is visible and always on top
            if (!this.Visible)
                this.Show();

            // Get the position and size of the target display
            Screen[] allScreens = Screen.AllScreens;
            Screen targetScreen = allScreens.Length > _screenIndex ? allScreens[_screenIndex] : Screen.PrimaryScreen!;
            Rectangle bounds = targetScreen.Bounds;
            
            // Use SetWindowPos to ensure window is always on top and positioned correctly - use actual coordinates of the target display
            SetWindowPos(this.Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);

            // Draw content on the bitmap
            DrawOverlayBitmap();

            // Use UpdateLayeredWindow to update the window - use actual coordinates of the target display
            UpdateLayeredWindowFromBitmap(bounds.X, bounds.Y);
        }

        private void DrawOverlayBitmap()
        {
            lock (_bitmapLock)
            {
                // Only recreate bitmap if it doesn't exist or has mismatched dimensions, avoid frequent bitmap reconstruction
                if (_overlayBitmap == null || _overlayBitmap.Width != _screenW || _overlayBitmap.Height != _screenH)
                {
                    // Release old bitmap
                    _overlayBitmap?.Dispose();
                    // Create new bitmap
                    _overlayBitmap = new Bitmap(_screenW, _screenH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                }
                else
                {
                    // Don't clear bitmap to achieve cumulative display effect and avoid flickering (currently testing shows minimal impact)
                    // using (Graphics g = Graphics.FromImage(_overlayBitmap))
                    // {
                    //     // Clear bitmap with transparent color
                    //     g.Clear(Color.Transparent);
                    // }
                }

                using (Graphics g = Graphics.FromImage(_overlayBitmap))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

                    // Draw semi-transparent white/black overlay with inverse brightness only in refresh areas
                    foreach (var tile in _tiles.Keys)
                    {
                        // Get brightness data from Dictionary
                        float brightness = _tiles[tile];
                        int bx = tile.bx;
                        int by = tile.by;

                        // Convert physical coordinates to logical coordinates
                        int sx = (int)(bx * _tileSize / _scaleX);
                        int sy = (int)(by * _tileSize / _scaleY);
                        int w = Math.Min((int)(_tileSize / _scaleX), _screenW - sx);
                        int h = Math.Min((int)(_tileSize / _scaleY), _screenH - sy);

                        // Determine whether to display black or white based on brightness value (inverse display)
                        Color overlayColor;
                        // Brightness > 0.5 displays black, brightness <= 0.5 displays white (inverse)
                        if (brightness > 0.5f)
                        {
                            overlayColor = Color.FromArgb(85, 0, 0, 0); // Semi-transparent black
                        }
                        else
                        {
                            overlayColor = Color.FromArgb(85, 255, 255, 255); // Semi-transparent white
                        }

                        // Draw semi-transparent squares with inverse brightness color in refresh areas
                        using (var br = new SolidBrush(overlayColor))
                            g.FillRectangle(br, sx, sy, w, h);
                    }
                }

                // Note: Don't clear the _expiredTiles list here because it's still needed in the MarkTilesAsExpiredInternal method
                // _expiredTiles.Clear();
            }
        }

        private void UpdateLayeredWindowFromBitmap(int screenX, int screenY)
        {
            lock (_bitmapLock)
            {
                // Check if bitmap exists
                if (_overlayBitmap == null) return;

                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = _overlayBitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                IntPtr hOld = SelectObject(hdcMem, hBitmap);

                Win32Point ptSrc = new Win32Point(0, 0);
                Win32Size sz = new Win32Size(_screenW, _screenH);
                Win32Point ptDest = new Win32Point(screenX, screenY); // Use actual coordinates of the target display

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