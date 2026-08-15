using System;
using System.Runtime.InteropServices;

namespace Lib.Ui.Screens.Services
{
    /// <summary>
    /// マルチモニター検出ユーティリティ
    /// 概要：PInvoke で接続モニター数やポートレート（縦長）ディスプレイの
    ///       作業領域を取得する。IntegratedWindow の配置判定に使用。
    /// </summary>
    public static class MonitorDetector
    {
        #region PInvoke

        private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        #endregion PInvoke

        #region 構造体

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const uint MONITORINFOF_PRIMARY = 1;

        #endregion 構造体

        /// <summary>
        /// 接続されているモニターの数を返す
        /// </summary>
        public static int GetMonitorCount()
        {
            int count = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeRect rc, IntPtr data) =>
            {
                count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }

        /// <summary>
        /// ポートレート（縦長）モニターの作業領域を返す。見つからなければ null。
        /// </summary>
        public static NativeRect? FindPortraitMonitorWorkArea()
        {
            NativeRect? result = null;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeRect rc, IntPtr data) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(hMon, ref info) && info.rcMonitor.Height > info.rcMonitor.Width)
                {
                    result = info.rcWork;
                    return false; // 最初の縦長モニターで停止
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// プライマリモニターの作業領域を返す。見つからなければ null。
        /// </summary>
        public static NativeRect? FindPrimaryMonitorWorkArea()
        {
            NativeRect? result = null;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeRect rc, IntPtr data) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(hMon, ref info) && (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    result = info.rcWork;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// プライマリでないモニターの作業領域を返す。見つからなければ null。
        /// </summary>
        public static NativeRect? FindSecondMonitorWorkArea()
        {
            NativeRect? result = null;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeRect rc, IntPtr data) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(hMon, ref info) && (info.dwFlags & MONITORINFOF_PRIMARY) == 0)
                {
                    result = info.rcWork;
                    return false; // 最初の非プライマリモニターで停止
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
