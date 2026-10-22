using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace OpenAIUsageWidget.Desktop;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _drawing;
    public TrayIcon(Action settings, Action resetPosition, Action refresh, Action exit)
    {
        _drawing = CreateIcon();
        var menu = new Forms.ContextMenuStrip();
        Add(menu, "Configuración y cuentas", settings);
        Add(menu, "Refrescar cuotas", refresh);
        Add(menu, "Restaurar posición en el escritorio", resetPosition);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, "Salir", exit);
        _icon = new Forms.NotifyIcon { Icon = _drawing, Text = "OpenAI Usage · solo lectura", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => Application.Current.Dispatcher.Invoke(settings);
    }

    private static void Add(Forms.ContextMenuStrip menu, string label, Action action) =>
        menu.Items.Add(label, null, (_, _) => Application.Current.Dispatcher.Invoke(action));

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(23, 28, 34));
        graphics.FillEllipse(background, 0, 0, 31, 31);
        using var track = new Pen(Color.FromArgb(66, 74, 85), 4);
        using var accent = new Pen(Color.FromArgb(0, 230, 160), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(track, 6, 6, 19, 19);
        graphics.DrawArc(accent, 6, 6, 19, 19, -90, 275);
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _drawing.Dispose();
    }
}
