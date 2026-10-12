using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OurMusic.Desktop;

public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        Set(handle, 20, dark ? 1 : 0);
        Set(handle, 35, ToColorRef(((SolidColorBrush)window.FindResource("CanvasBrush")).Color));
        Set(handle, 36, ToColorRef(((SolidColorBrush)window.FindResource("TextBrush")).Color));
        Set(handle, 34, ToColorRef(((SolidColorBrush)window.FindResource("SurfaceBrush")).Color));
    }
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    private static void Set(IntPtr handle, int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
}
