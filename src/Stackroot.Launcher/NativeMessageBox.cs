using System.Runtime.InteropServices;

namespace Stackroot.Launcher;

internal static class NativeMessageBox
{
    private const uint Ok = 0x00000000;
    private const uint IconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowError(string message, string caption = "Stackroot") =>
        MessageBoxW(IntPtr.Zero, message, caption, Ok | IconError);
}
