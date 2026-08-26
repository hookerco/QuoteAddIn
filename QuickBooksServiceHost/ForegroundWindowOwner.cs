using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickBooksServiceHost
{
    internal static class ForegroundWindowOwner
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint processId);

        internal static IntPtr CaptureHandle()
        {
            return GetForegroundWindow();
        }

        internal static IWin32Window TryCreate(IntPtr handle)
        {
            return TryCreate(handle, IsWindow, ProcessNameForWindow);
        }

        internal static IWin32Window TryCreate(
            IntPtr handle,
            Func<IntPtr, bool> isWindow,
            Func<IntPtr, string> processNameForWindow)
        {
            if (handle == IntPtr.Zero || !isWindow(handle))
            {
                return null;
            }

            string processName = processNameForWindow(handle);
            if (!IsBrowserProcess(processName))
            {
                return null;
            }

            return new WindowHandle(handle);
        }

        private static string ProcessNameForWindow(IntPtr handle)
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return null;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsBrowserProcess(string processName)
        {
            return string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "brave", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "opera", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "vivaldi", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class WindowHandle : IWin32Window
        {
            internal WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
