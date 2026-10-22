using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenAIUsageWidget.Desktop;
using OpenAIUsageWidget.Presentation;

namespace OpenAIUsageWidget.Views;

public partial class MainWindow : Window
{
    private readonly WidgetViewModel _model;
    public DesktopAnchor? Anchor { get; private set; }

    public MainWindow(WidgetViewModel model, bool attachToDesktop = true)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        if (model.Settings.Left is { } x && model.Settings.Top is { } y) { Left = x; Top = y; }
        SourceInitialized += (_, _) => { if (attachToDesktop) Anchor = new DesktopAnchor(this); };
        Loaded += (_, _) =>
        {
            var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (model.Settings.Left is null || !bounds.IntersectsWith(new Rect(Left, Top, ActualWidth, ActualHeight)))
                ResetPosition();
            Anchor?.Reposition();
        };
        Closed += (_, _) => Anchor?.Dispose();
    }

    public void ResetPosition()
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Right - ActualWidth - 24);
        Top = Math.Max(work.Top, work.Bottom - ActualHeight - 24);
        Anchor?.Reposition();
    }

    private async void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != WidgetSurface)
        {
            if (source is ButtonBase or TextBox or ScrollBar) return;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        AccountsPopup.IsOpen = false;
        Anchor?.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { Anchor?.EndDrag(); }
        await _model.SavePositionAsync(Left, Top);
    }

    private async void AccountItem_Click(object sender, RoutedEventArgs e)
    {
        AccountsPopup.IsOpen = false;
        if (sender is Button { DataContext: AccountViewModel account }) await _model.SelectAsync(account);
    }

    public void SavePreview(string path)
    {
        UpdateLayout();
        SaveVisual(WidgetSurface, path);
    }

    public async Task SaveDropdownPreviewAsync(string path)
    {
        var child = (FrameworkElement)AccountsPopup.Child;
        child.DataContext = _model;
        child.Measure(new Size(280, double.PositiveInfinity));
        child.Arrange(new Rect(child.DesiredSize));
        await Dispatcher.InvokeAsync(() => child.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        SaveVisual(child, path);
    }

    private static void SaveVisual(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
