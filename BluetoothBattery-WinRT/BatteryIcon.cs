using System.Drawing;
using System.Drawing.Drawing2D;

namespace BluetoothBattery;

internal static class BatteryIcon
{
    private const int MaxLevel = 9;
    private const int ReferenceSize = 16;

    private static int Scale(int value, int size) =>
        (int)Math.Round(value * (size / (double)ReferenceSize));

    private static (Rectangle Body, Rectangle Cap, int BorderThickness) Geometry(int size)
    {
        var body = new Rectangle(
            Scale(1, size), Scale(5, size), Scale(12, size), Scale(7, size));

        var capWidth = Math.Max(1, Scale(2, size));
        var capHeight = Math.Max(1, Scale(3, size));
        var cap = new Rectangle(
            body.Left + body.Width,
            body.Top + (body.Height - capHeight) / 2,
            capWidth, capHeight);

        var borderThickness = Math.Max(1, Scale(1, size));

        return (body, cap, borderThickness);
    }

    private static int CurrentIconSize() =>
        Math.Max(ReferenceSize, Win32.GetSystemMetrics(Win32.SM_CXSMICON));

    internal static IntPtr Create(Windows.Devices.Radios.RadioState radioState, bool anyConnected, int? percent)
    {
        using var _timing = Timing.Measure("BatteryIcon.Create");

        var size = CurrentIconSize();
        var (body, cap, borderThickness) = Geometry(size);

        using var bitmap = new Bitmap(size, size);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);

            var radioOn = radioState == Windows.Devices.Radios.RadioState.On;

            DrawOutline(g, body, cap, borderThickness);

            if (radioOn && anyConnected && percent.HasValue)
            {
                var clamped = Math.Clamp(percent.Value, 0, 100);
                DrawFill(g, body, ColorFor(clamped), LevelFor(clamped));
            }
            else if (radioOn && anyConnected)
            {
                DrawUnknownDash(g, body);
            }

            if (!radioOn)
            {
                DrawSlash(g, body);
            }
        }

        // GetHicon: caller owns the returned handle; not freed automatically.
        return bitmap.GetHicon();
    }

    private static void DrawOutline(Graphics g, Rectangle body, Rectangle cap, int borderThickness)
    {
        using var brush = new SolidBrush(Color.White);

        var outerLeft = body.Left - borderThickness;
        var outerTop = body.Top - borderThickness;
        var outerWidth = body.Width + borderThickness * 2;
        var outerHeight = body.Height + borderThickness * 2;

        g.FillRectangle(brush, outerLeft, outerTop, outerWidth, borderThickness); // top
        g.FillRectangle(brush, outerLeft, outerTop + outerHeight - borderThickness, outerWidth, borderThickness); // bottom
        g.FillRectangle(brush, outerLeft, outerTop, borderThickness, outerHeight); // left
        g.FillRectangle(brush, outerLeft + outerWidth - borderThickness, outerTop, borderThickness, outerHeight); // right

        g.FillRectangle(brush, cap.Left, cap.Top, cap.Width, cap.Height);
    }

    private static void DrawFill(Graphics g, Rectangle body, Color fillColor, int level)
    {
        // Ceiling, not round, so any non-zero level fills at least one column.
        var fillWidth = level == 0 ? 0 : (int)Math.Ceiling(body.Width * (level / (double)MaxLevel));
        fillWidth = Math.Min(fillWidth, body.Width);

        if (fillWidth > 0)
        {
            using var fill = new SolidBrush(fillColor);
            g.FillRectangle(fill, body.Left, body.Top, fillWidth, body.Height);
        }
    }

    private static void DrawSlash(Graphics g, Rectangle body)
    {
        using var brush = new SolidBrush(Color.FromArgb(235, 235, 235));

        var left = body.Left;
        var right = body.Left + body.Width - 1;
        var top = body.Top;
        var bottom = body.Top + body.Height - 1;
        var spanX = Math.Max(1, right - left);
        var spanY = bottom - top;

        var thickness = Math.Max(2, body.Height * 2 / 7);

        for (var x = left; x <= right; x++)
        {
            var t = (x - left) / (double)spanX;
            var y = bottom - t * spanY;
            var yInt = (int)Math.Round(y);
            g.FillRectangle(brush, x, yInt - thickness / 2, 1, thickness);
        }
    }

    private static void DrawUnknownDash(Graphics g, Rectangle body)
    {
        using var dash = new SolidBrush(Color.Gray);
        var thickness = Math.Max(1, body.Height / 4);
        var midY = body.Top + body.Height / 2 - thickness / 2;
        var inset = Math.Max(1, body.Width / 12);
        g.FillRectangle(dash, body.Left + inset, midY, Math.Max(1, body.Width - inset * 2), thickness);
    }

    private static int LevelFor(int percent) => percent == 0
        ? 0
        : Math.Clamp((int)Math.Ceiling(MaxLevel * (percent / 100.0)), 1, MaxLevel);

    internal static Color ColorFor(int percent) => percent <= LowBatteryPercent
        ? Color.FromArgb(214, 92, 92)
        : Color.FromArgb(72, 176, 96);

    internal const int LowBatteryPercent = 20;
}
