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