using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskBoxes
{
    /// <summary>用 SHGetFileInfo 取系统真实图标（与资源管理器一致），带内存缓存。</summary>
    public static class IconLoader
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysIconIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

        private const uint SHGSI_ICON = 0x000000100;
        private const uint SHGSI_LARGEICON = 0x000000000;
        private const uint SIID_DESKTOPPC = 17;      // 此电脑
        private const uint SIID_RECYCLER = 31;       // 回收站

        /// <summary>此电脑 / 回收站用系统库存图标，比按 CLSID 路径取更可靠。</summary>
        private static Bitmap LoadStock(uint siid, int size)
        {
            try
            {
                SHSTOCKICONINFO info = new SHSTOCKICONINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO));
                int hr = SHGetStockIconInfo(siid, SHGSI_ICON | SHGSI_LARGEICON, ref info);
                if (hr != 0 || info.hIcon == IntPtr.Zero) return null;

                Bitmap result;
                using (Icon ic = Icon.FromHandle(info.hIcon))
                {
                    Bitmap src = ic.ToBitmap();
                    if (size > 0 && (src.Width != size || src.Height != size))
                    {
                        result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(result))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.DrawImage(src, new Rectangle(0, 0, size, size));
                        }
                        src.Dispose();
                    }
                    else result = src;
                }
                DestroyIcon(info.hIcon);
                return result;
            }
            catch { return null; }
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;   // 32x32
        private const uint SHGFI_SMALLICON = 0x000000001;   // 16x16
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        private static readonly object _lock = new object();

        /// <summary>取某个路径的图标；路径不存在时按扩展名给通用图标。</summary>
        public static Bitmap Get(string path, int size)
        {
            string key = (path == null ? "" : path.ToLowerInvariant()) + "|" + size;
            lock (_lock)
            {
                Bitmap bmp;
                if (_cache.TryGetValue(key, out bmp) && bmp != null) return bmp;

                bmp = Load(path, size);
                if (bmp != null) _cache[key] = bmp;
                return bmp;
            }
        }

        /// <summary>shell: 开头的虚拟项转成 CLSID 路径，才能取到图标（此电脑/回收站等）。</summary>
        private static string ToClsidPath(string p)
        {
            if (p == null) return p;
            string low = p.ToLowerInvariant();
            if (low == "shell:mycomputerfolder" || low == "shell:mycomputer")
                return "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
            if (low == "shell:recyclebinfolder" || low == "shell:recyclebin")
                return "::{645FF040-5081-101B-9F08-00AA002F954E}";
            return p;
        }

        private static Bitmap Load(string path, int size)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    string low = path.ToLowerInvariant();
                    if (low.StartsWith("shell:mycomputer")) return LoadStock(SIID_DESKTOPPC, size);
                    if (low.StartsWith("shell:recyclebin")) return LoadStock(SIID_RECYCLER, size);
                    path = ToClsidPath(path);
                }

                bool exists = !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                uint attr = 0;

                bool isClsid = path != null && path.StartsWith("::{");
                if (!exists && !isClsid)
                {
                    // 文件不在（比如移动了）：按扩展名/目录属性取通用图标
                    flags |= SHGFI_USEFILEATTRIBUTES;
                    bool isDir = !string.IsNullOrEmpty(path) && path.EndsWith("\\");
                    attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                }

                IntPtr r = SHGetFileInfo(path == null ? "" : path, attr, ref info,
                                         (uint)Marshal.SizeOf(info), flags);
                if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                Bitmap result;
                using (Icon ic = Icon.FromHandle(info.hIcon))
                {
                    // SHGetFileInfo 返回的多半是 32x32，按需要缩放并保留透明通道
                    Bitmap src = ic.ToBitmap();
                    if (size > 0 && (src.Width != size || src.Height != size))
                    {
                        result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(result))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(src, new Rectangle(0, 0, size, size));
                        }
                        src.Dispose();
                    }
                    else
                    {
                        result = src;
                    }
                }

                DestroyIcon(info.hIcon);
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>配置改动后清缓存（换了自定义图标要用）。</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                foreach (Bitmap b in _cache.Values) { try { if (b != null) b.Dispose(); } catch { } }
                _cache.Clear();
            }
        }
    }
}
