using System.Runtime.InteropServices;
namespace BetterComputerUse;

internal static class ViewerGeometry
{
    // ActiveX smart sizing downsizes the desktop, but does not upscale beyond its
    // native resolution. Fit the window to the pixels instead of exposing grey gutters.
    internal static Rectangle Fit(Size desktop, Rectangle workingArea, int headerHeight, double fraction)
    {
        double scale = Math.Min(1, Math.Min((workingArea.Width * fraction - 2) / desktop.Width,
            (workingArea.Height * fraction - headerHeight - 2) / desktop.Height));
        var size = new Size((int)Math.Round(desktop.Width * scale) + 2,
            (int)Math.Round(desktop.Height * scale) + headerHeight + 2);
        return new Rectangle(workingArea.X + (workingArea.Width - size.Width) / 2,
            workingArea.Y + (workingArea.Height - size.Height) / 2, size.Width, size.Height);
    }
    internal static Rectangle Resize(Rectangle proposed, Size desktop, int headerHeight, int edge, Size available, int minimumWidth = 358, int minimumHeight = 84)
    {
        double scale = edge is 3 or 6 ? (proposed.Height - headerHeight - 2d) / desktop.Height : (proposed.Width - 2d) / desktop.Width;
        double maximum = Math.Min(1, Math.Min((available.Width - 2d) / desktop.Width, (available.Height - headerHeight - 2d) / desktop.Height));
        double minimum = Math.Max(minimumWidth / (double)desktop.Width, minimumHeight / (double)desktop.Height);
        scale = Math.Clamp(scale, Math.Min(minimum, maximum), maximum);
        int width = (int)Math.Round(desktop.Width * scale) + 2, height = (int)Math.Round(desktop.Height * scale) + headerHeight + 2;
        return new Rectangle(edge is 1 or 4 or 7 ? proposed.Right - width : proposed.Left,
            edge is 3 or 4 or 5 ? proposed.Bottom - height : proposed.Top, width, height);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal NativeRect(Rectangle rect) { Left = rect.Left; Top = rect.Top; Right = rect.Right; Bottom = rect.Bottom; }
        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}
