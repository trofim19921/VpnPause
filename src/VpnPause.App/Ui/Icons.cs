using System.Drawing.Drawing2D;
using System.Reflection;

namespace VpnPause.App;

static class Icons
{
    public static readonly Icon App = LoadApp();
    public static readonly Icon Green = Dot(Color.FromArgb(46, 160, 67));
    public static readonly Icon Red = Dot(Color.FromArgb(210, 70, 60));
    public static readonly Icon Orange = Dot(Color.FromArgb(230, 150, 30));
    public static readonly Icon Gray = Dot(Color.FromArgb(140, 140, 140));

    static Icon LoadApp()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VpnPause.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    static Icon Dot(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 2, 2, 28, 28);
            using var w = new SolidBrush(Color.White);
            g.FillRectangle(w, 11, 9, 4, 14);
            g.FillRectangle(w, 18, 9, 4, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
