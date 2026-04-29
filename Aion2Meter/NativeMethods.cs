using System.Runtime.InteropServices;

namespace Aion2Meter;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
