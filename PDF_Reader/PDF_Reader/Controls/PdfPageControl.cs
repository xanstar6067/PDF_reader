using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.ViewModels;

namespace PDF_Reader.Controls;

public sealed class PdfPageControl : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(PageItem), typeof(PdfPageControl), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(nameof(Match), typeof(SearchMatch), typeof(PdfPageControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(nameof(Workspace), typeof(WorkspaceViewModel), typeof(PdfPageControl));
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(bool), typeof(PdfPageControl), new FrameworkPropertyMetadata(false, Changed));
    public PageItem? Item { get => (PageItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public SearchMatch? Match { get => (SearchMatch?)GetValue(MatchProperty); set => SetValue(MatchProperty, value); }
    public WorkspaceViewModel? Workspace { get => (WorkspaceViewModel?)GetValue(WorkspaceProperty); set => SetValue(WorkspaceProperty, value); }
    public bool Thumbnail { get => (bool)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    private BitmapSource? _image;
    private string? _error;
    private CancellationTokenSource? _render;
    private Point? _anchor;
    private Point _dragPoint;
    private TextSelection? _selection;
    private static readonly Brush SelectionBrush = FrozenBrush(Color.FromArgb(85, 46, 117, 224));
    private static readonly Brush SearchBrush = FrozenBrush(Color.FromArgb(135, 255, 198, 38));
    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    public PdfPageControl()
    {
        Focusable = true; ClipToBounds = true;
        Loaded += (_, _) => RenderPage();
        Unloaded += (_, _) => { _render?.Cancel(); _render?.Dispose(); _render = null; _image = null; _selection = null; };
        SizeChanged += (_, _) => RenderPage();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        { if (!string.IsNullOrEmpty(_selection?.Text)) Clipboard.SetText(_selection.Text); e.Handled = true; }, (_, e) => { e.CanExecute = !string.IsNullOrEmpty(_selection?.Text); e.Handled = true; }));
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfPageControl)d; view._image = null; view._selection = null; view._error = null;
        view.InvalidateVisual(); view.RenderPage();
    }
    private async void RenderPage()
    {
        _render?.Cancel(); _render?.Dispose(); _render = null;
        if (!IsLoaded || Item is not { } item || ActualWidth < 1) return;
        var cancellation = new CancellationTokenSource(); _render = cancellation;
        var token = cancellation.Token;
        var width = (int)Math.Ceiling(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        try
        {
            await Task.Delay(90, token);
            var image = await Task.Run(() => item.Document.Render(item.Info.Index, width, cancellation: token), token);
            if (!token.IsCancellationRequested && IsLoaded && Item == item) { _image = image; _error = null; InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { _error = "Не удалось отобразить страницу"; InvalidateVisual(); if (Workspace is { } vm) vm.Status = ex.Message; } }
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (_image is not null) dc.DrawImage(_image, new Rect(RenderSize));
        else if (!Thumbnail)
        {
            var text = new FormattedText(_error ?? "Загрузка страницы…", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Max(16, (ActualWidth - text.Width) / 2), Math.Min(ActualHeight / 2, 160)));
        }
        if (Thumbnail) return;
        if (Match is { } match && match.PageIndex == Item?.Info.Index)
            foreach (var rect in match.Rectangles) dc.DrawRectangle(SearchBrush, null, DisplayRect(rect));
        if (_selection is { } selection)
            foreach (var rect in selection.Rectangles) dc.DrawRectangle(SelectionBrush, null, DisplayRect(rect));
        if (_anchor is { } anchor) dc.DrawRectangle(SelectionBrush, new Pen(Brushes.SteelBlue, .75), new Rect(anchor, _dragPoint));
    }
    private Rect DisplayRect(Rect rect) => new(rect.X * ActualWidth, rect.Y * ActualHeight, rect.Width * ActualWidth, rect.Height * ActualHeight);
    private Point Normalize(Point point) => new(Math.Clamp(point.X / ActualWidth, 0, 1), Math.Clamp(point.Y / ActualHeight, 0, 1));
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Thumbnail || Workspace?.CanInteract != true) return;
        Focus(); _selection = null; _anchor = e.GetPosition(this); _dragPoint = _anchor.Value; CaptureMouse(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Workspace?.Tool is DocumentTool.EditText or DocumentTool.AddText ? Cursors.IBeam : Workspace?.Tool == DocumentTool.Note ? Cursors.Pen : Cursors.Arrow;
        if (_anchor is not null && e.LeftButton == MouseButtonState.Pressed) { _dragPoint = e.GetPosition(this); InvalidateVisual(); }
    }
    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_anchor is not { } anchor || Item is not { } item || Workspace is not { } vm) return;
        var end = e.GetPosition(this); var startNormalized = Normalize(anchor); var endNormalized = Normalize(end);
        var dragged = (anchor - end).Length > 4; _anchor = null; ReleaseMouseCapture(); InvalidateVisual(); e.Handled = true;
        if (!vm.CanInteract || vm.Document != item.Document) return;
        vm.SetVisiblePage(item.Info.Index);
        try
        {
            if (vm.Tool is DocumentTool.EditText or DocumentTool.AddText or DocumentTool.Note) await vm.EditAtAsync(item.Info.Index, startNormalized);
            else if (dragged)
            {
                var selection = await Task.Run(() => item.Document.SelectText(item.Info.Index, startNormalized, endNormalized));
                if (vm.Document != item.Document || Item != item) return;
                if (vm.Tool == DocumentTool.Highlight && selection.Rectangles.Count > 0)
                    await vm.ChangeAsync(d => d.Highlight(item.Info.Index, selection.Rectangles));
                else { _selection = selection; vm.Status = string.IsNullOrEmpty(selection.Text) ? "Здесь нет текстового слоя. Для скана требуется OCR." : "Текст выделен • Ctrl+C — копировать"; InvalidateVisual(); }
            }
            else if (vm.Tool == DocumentTool.Select)
            {
                if (await vm.TryFillFormAtAsync(item.Info.Index, startNormalized)) return;
                var link = await Task.Run(() => item.Document.HitLink(item.Info.Index, startNormalized));
                if (link?.PageIndex >= 0) vm.CurrentPage = link.PageIndex + 1;
                else if (link?.Uri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" or "mailto")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { vm.ShowError(ex); }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _anchor = null; InvalidateVisual(); base.OnLostMouseCapture(e); }
}
