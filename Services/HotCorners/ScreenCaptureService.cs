// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using TopToolbar.Logging;

namespace TopToolbar.Services.HotCorners
{
    internal readonly struct CapturedBitmap
    {
        public CapturedBitmap(byte[] bgra, int width, int height)
        {
            Bgra = bgra;
            Width = width;
            Height = height;
        }

        public byte[] Bgra { get; }

        public int Width { get; }

        public int Height { get; }

        public bool IsValid => Bgra != null && Width > 0 && Height > 0;
    }

    /// <summary>
    /// Captures a rectangle of the screen (physical pixels) into a top-down BGRA buffer via GDI BitBlt.
    /// </summary>
    internal static class ScreenCaptureService
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int BI_RGB = 0;
        private const int DIB_RGB_COLORS = 0;

        public static CapturedBitmap Capture(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return default;
            }

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return default;
            }

            var memDc = IntPtr.Zero;
            var bmp = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    return default;
                }

                bmp = CreateCompatibleBitmap(screenDc, width, height);
                if (bmp == IntPtr.Zero)
                {
                    return default;
                }

                var prev = SelectObject(memDc, bmp);
                var ok = BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT);
                SelectObject(memDc, prev);

                if (!ok)
                {
                    return default;
                }

                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative => top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                };

                var stride = width * 4;
                var buffer = new byte[stride * height];
                var info = new BITMAPINFO { bmiHeader = header };

                var copied = GetDIBits(screenDc, bmp, 0, (uint)height, buffer, ref info, DIB_RGB_COLORS);
                if (copied == 0)
                {
                    return default;
                }

                return new CapturedBitmap(buffer, width, height);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"ScreenCapture: failed - {ex.Message}");
                return default;
            }
            finally
            {
                if (bmp != IntPtr.Zero)
                {
                    DeleteObject(bmp);
                }

                if (memDc != IntPtr.Zero)
                {
                    DeleteDC(memDc);
                }

                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public int biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);
    }
}
