using System;
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

        internal static IntPtr CaptureHandle()
        {
            return GetForegroundWindow();
        }

        internal static IWin32Window TryCreate(IntPtr handle)
        {
            return TryCreate(handle, IsWindow);
        }

        internal static IWin32Window TryCreate(
            IntPtr handle,
            Func<IntPtr, bool> isWindow)
        {
            if (handle == IntPtr.Zero || !isWindow(handle))
            {
                return null;
            }

            return new WindowHandle(handle);
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
