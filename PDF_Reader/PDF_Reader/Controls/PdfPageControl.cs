using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;

namespace PDF_Reader.Controls;

/// <summary>
/// Displays one PDF page. Small pages are rendered whole at device resolution. Large ones (high zoom) get a
/// low-resolution base image plus a sharp image of the visible part, re-rendered as the view scrolls,
/// so memory stays bounded and text stays crisp at any zoom.
/// </summary>
public sealed class PdfPageControl : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(PageItem), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(null, ItemChanged));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(0, VersionChanged));
    public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(nameof(Match), typeof(SearchMatch), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MatchesVersionProperty = DependencyProperty.Register(nameof(MatchesVersion), typeof(int), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedAnnotationProperty = DependencyProperty.Register(nameof(SelectedAnnotation), typeof(PdfComment), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WorkspaceProperty =DependencyProperty.Register(nameof(Workspace), typeof(WorkspaceViewModel), typeof(PdfPageControl));
    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(bool), typeof(PdfPageControl),
        new FrameworkPropertyMetadata(false, ItemChanged));

    public PageItem? Item { get => (PageItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public SearchMatch? Match { get => (SearchMatch?)GetValue(MatchProperty); set => SetValue(MatchProperty, value); }
    public int MatchesVersion { get => (int)GetValue(MatchesVersionProperty); set => SetValue(MatchesVersionProperty, value); }
    public PdfComment? SelectedAnnotation { get => (PdfComment?)GetValue(SelectedAnnotationProperty); set => SetValue(SelectedAnnotationProperty, value); }
    public WorkspaceViewModel? Workspace { get => (WorkspaceViewModel?)GetValue(WorkspaceProperty); set => SetValue(WorkspaceProperty, value); }
    public bool Thumbnail { get => (bool)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }

    private const double FullRenderPixels = 4_000_000;
    private const double BaseRenderPixels = 1_500_000;
    private const double DetailMargin = 192;

    private static readonly Brush SelectionBrush = FrozenBrush(Color.FromArgb(85, 46, 117, 224));
    private static readonly Brush MatchBrush = FrozenBrush(Color.FromArgb(80, 255, 214, 64));
    private static readonly Brush CurrentMatchBrush = FrozenBrush(Color.FromArgb(150, 255, 150, 20));
    private static readonly Pen SelectionPen = FrozenPen(new Pen(FrozenBrush(Color.FromRgb(46, 117, 224)), 1.25) { DashStyle = DashStyles.Dash });
    private static readonly Pen HandlePen = FrozenPen(new Pen(FrozenBrush(Color.FromRgb(46, 117, 224)), 1.25));
    private const double HandleSize = 8;

    private enum Drag { None, Text, Shape, Ink, Move, Resize }

    private BitmapSource? _image;
    private (int Width, int Version) _imageKey;
    private BitmapSource? _detail;
    private Rect _detailRect;
    private (int Width, int Height, int Version) _detailKey;
    private string? _error;
    private CancellationTokenSource? _render;
    private CancellationTokenSource? _detailRender;
    private ScrollViewer? _viewer;
    private Point? _anchor;
    private Point _dragPoint;
    private TextSelection? _selection;
    private Drag _drag;
    private PdfComment? _dragAnnotation;
    private Point _resizeFixed;
    private readonly List<Point> _ink = [];

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    public PdfPageControl()
    {
        Focusable = true;
        ClipToBounds = true;
        UseLayoutRounding = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => RenderPage();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) =>
            {
                if (!string.IsNullOrEmpty(_selection?.Text)) Clipboard.SetText(_selection.Text);
                e.Handled = true;
            },
            (_, e) =>
            {
                e.CanExecute = !string.IsNullOrEmpty(_selection?.Text);
                e.Handled = true;
            }));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Thumbnail)
        {
            _viewer = FindAncestor<ScrollViewer>(this);
            if (_viewer is not null) _viewer.ScrollChanged += OnScrollChanged;
        }
        RenderPage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewer is not null) _viewer.ScrollChanged -= OnScrollChanged;
        _viewer = null;
        Cancel(ref _render);
        Cancel(ref _detailRender);
        _image = null;
        _detail = null;
        _selection = null;
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T found) return found;
        return null;
    }

    private static void ItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfPageControl)d;
        view._image = null;
        view._detail = null;
        view._selection = null;
        view._error = null;
        view._anchor = null;
        view._drag = Drag.None;
        view.InvalidateVisual();
        view.RenderPage();
    }

    // The previous images stay on screen until the new content is ready, so edits do not flash.
    private static void VersionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PdfPageControl)d).RenderPage(15);

    private (int Width, int Height) DeviceSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    private bool UsesDetail(int width, int height) =>
        !Thumbnail && Item?.Info.Has3D != true && (double)width * height > FullRenderPixels && width <= PdfDocument.MaxRenderSide && height <= PdfDocument.MaxRenderSide;

    private async void RenderPage(int delay = 90)
    {
        Cancel(ref _render);
        if (!IsLoaded || Item is not { } item || ActualWidth < 1 || ActualHeight < 1) return;
        var (deviceWidth, deviceHeight) = DeviceSize();
        var width = UsesDetail(deviceWidth, deviceHeight)
            ? Math.Max(16, (int)(deviceWidth * Math.Sqrt(BaseRenderPixels / ((double)deviceWidth * deviceHeight))))
            : deviceWidth;
        var version = item.Version;
        if (_image is not null && _imageKey == (width, version))
        {
            // Zooming within the detail range keeps the base image; only the visible part is redrawn.
            InvalidateVisual();
            UpdateDetail();
            return;
        }
        var cancellation = new CancellationTokenSource();
        _render = cancellation;
        var token = cancellation.Token;
        try
        {
            await Task.Delay(delay, token);
            var image = item.Info.Has3D
                ? await ThreeDService.PreviewAsync(item.Document, item.Info.Index, width, token)
                : await Task.Run(() => item.Document.Render(item.Info.Index, width, cancellation: token), token);
            if (token.IsCancellationRequested || !IsLoaded || Item != item) return;
            _image = image;
            _imageKey = (width, version);
            _error = null;
            InvalidateVisual();
            UpdateDetail();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _error = "Не удалось отобразить страницу";
            InvalidateVisual();
            if (Workspace is { } vm) vm.Status = ex.Message;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateDetail();

    private Rect VisibleRect()
    {
        if (_viewer is null) return Rect.Empty;
        try
        {
            if (_viewer.TransformToDescendant(this) is not { } transform) return Rect.Empty;
            var width = _viewer.ViewportWidth > 0 ? _viewer.ViewportWidth : _viewer.ActualWidth;
            var height = _viewer.ViewportHeight > 0 ? _viewer.ViewportHeight : _viewer.ActualHeight;
            var visible = transform.TransformBounds(new Rect(0, 0, width, height));
            visible.Intersect(new Rect(RenderSize));
            return visible;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty; // Detached during container recycling.
        }
    }

    private void UpdateDetail()
    {
        if (!IsLoaded || Item is not { } item || _viewer is null || ActualWidth < 1) return;
        var (width, height) = DeviceSize();
        if (!UsesDetail(width, height))
        {
            if (_detail is null) return;
            _detail = null;
            InvalidateVisual();
            return;
        }
        var visible = VisibleRect();
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) return;
        if (_detail is not null && _detailKey == (width, height, item.Version) && _detailRect.Contains(visible)) return;
        RenderDetail(item, width, height, visible);
    }

    private async void RenderDetail(PageItem item, int width, int height, Rect visible)
    {
        Cancel(ref _detailRender);
        var cancellation = new CancellationTokenSource();
        _detailRender = cancellation;
        var token = cancellation.Token;
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = visible;
        area.Inflate(DetailMargin, DetailMargin);
        area.Intersect(new Rect(RenderSize));
        if ((area.Width * dpi.DpiScaleX) * (area.Height * dpi.DpiScaleY) > PdfDocument.MaxRenderPixels) area = visible;
        var region = new Int32Rect((int)Math.Floor(area.X * dpi.DpiScaleX), (int)Math.Floor(area.Y * dpi.DpiScaleY),
            (int)Math.Ceiling(area.Width * dpi.DpiScaleX), (int)Math.Ceiling(area.Height * dpi.DpiScaleY));
        var version = item.Version;
        try
        {
            await Task.Delay(60, token);
            var image = await Task.Run(() => item.Document.RenderRegion(item.Info.Index, width, height, region, token), token);
            if (token.IsCancellationRequested || !IsLoaded || Item != item) return;
            _detail = image;
            _detailRect = new Rect(region.X / dpi.DpiScaleX, region.Y / dpi.DpiScaleY, image.PixelWidth / dpi.DpiScaleX, image.PixelHeight / dpi.DpiScaleY);
            _detailKey = (width, height, version);
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // The base image is still shown; a failed sharp region is not worth interrupting the user.
            ErrorLog.Write("Не удалось отрисовать фрагмент страницы", ex);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.White, null, bounds);
        if (_image is not null) dc.DrawImage(_image, bounds);
        else if (!Thumbnail)
        {
            var text = new FormattedText(_error ?? "Загрузка страницы…", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Max(16, (ActualWidth - text.Width) / 2), Math.Min(ActualHeight / 2, 160)));
        }
        if (Thumbnail) return;
        var (width, height) = DeviceSize();
        if (_detail is not null && _detailKey.Width == width && _detailKey.Height == height) dc.DrawImage(_detail, _detailRect);
        if (Item is not { } item || Workspace is not { } vm) return;
        foreach (var match in vm.GetPageMatches(item.Info.Index))
        {
            var brush = match == Match ? CurrentMatchBrush : MatchBrush;
            foreach (var rect in match.Rectangles) dc.DrawRectangle(brush, null, DisplayRect(rect));
        }
        if (_selection is { } selection)
            foreach (var rect in selection.Rectangles) dc.DrawRectangle(SelectionBrush, null, DisplayRect(rect));
        if (SelectedAnnotation is { } selected && selected.PageIndex == item.Info.Index && _drag is not (Drag.Move or Drag.Resize))
            DrawSelection(dc, DisplayRect(selected.Bounds), selected.CanResize);
        if (_anchor is not { } anchor) return;
        switch (_drag)
        {
            case Drag.Move when _dragAnnotation is { } moving:
                var moved = DisplayRect(moving.Bounds);
                var offset = ClampOffset(moving.Bounds, _dragPoint - anchor);
                moved.Offset(offset.X * ActualWidth, offset.Y * ActualHeight);
                DrawSelection(dc, moved, false);
                break;
            case Drag.Resize:
                DrawSelection(dc, new Rect(_resizeFixed, Clamp(_dragPoint)), true);
                break;
            case Drag.Shape:
                var shape = new Rect(anchor, Clamp(_dragPoint));
                var pen = ShapePen(vm);
                if (vm.Tool == DocumentTool.Ellipse) dc.DrawEllipse(null, pen, new Point(shape.X + shape.Width / 2, shape.Y + shape.Height / 2), shape.Width / 2, shape.Height / 2);
                else dc.DrawRectangle(null, pen, shape);
                break;
            case Drag.Ink when _ink.Count > 1:
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(_ink[0], false, false);
                    context.PolyLineTo(_ink.Skip(1).ToList(), true, true);
                }
                dc.DrawGeometry(null, ShapePen(vm), geometry);
                break;
            case Drag.Text:
                dc.DrawRectangle(SelectionBrush, new Pen(Brushes.SteelBlue, .75), new Rect(anchor, _dragPoint));
                break;
        }
    }

    private Pen ShapePen(WorkspaceViewModel vm)
    {
        // Line width is in PDF points; the page is drawn at ActualWidth for Info.Width points.
        var scale = Item is { } item ? ActualWidth / item.Info.Width : 1;
        return new Pen(new SolidColorBrush(vm.CurrentColor), Math.Max(1, vm.StrokeWidth * scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    }

    private static void DrawSelection(DrawingContext dc, Rect rect, bool handles)
    {
        rect.Inflate(3, 3);
        dc.DrawRectangle(null, SelectionPen, rect);
        if (!handles) return;
        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
            dc.DrawRectangle(Brushes.White, HandlePen, new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private Rect DisplayRect(Rect rect) => new(rect.X * ActualWidth, rect.Y * ActualHeight, rect.Width * ActualWidth, rect.Height * ActualHeight);

    private Point Normalize(Point point) => new(Math.Clamp(point.X / ActualWidth, 0, 1), Math.Clamp(point.Y / ActualHeight, 0, 1));

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight));

    // Hit tolerance of a few pixels around thin annotations, in normalized coordinates.
    private Size Tolerance => new(4 / Math.Max(1, ActualWidth), 4 / Math.Max(1, ActualHeight));

    /// <summary>Normalized offset for a drag, limited so the annotation stays on the page.</summary>
    private Vector ClampOffset(Rect bounds, Vector drag)
    {
        var dx = drag.X / ActualWidth;
        var dy = drag.Y / ActualHeight;
        return new(Math.Clamp(dx, Math.Min(0, -bounds.Left), Math.Max(0, 1 - bounds.Right)), Math.Clamp(dy, Math.Min(0, -bounds.Top), Math.Max(0, 1 - bounds.Bottom)));
    }

    /// <summary>Opposite corner of the resize handle under <paramref name="point"/>, if any.</summary>
    private Point? ResizeAnchorAt(PdfComment annotation, Point point)
    {
        // Handles are drawn around the annotation; the fixed corner is the annotation's own corner, so resizing does not drift.
        var rect = DisplayRect(annotation.Bounds);
        var corners = new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft };
        rect.Inflate(3, 3);
        var handles = new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft };
        for (var i = 0; i < 4; i++)
            if ((handles[i] - point).Length <= HandleSize) return corners[(i + 2) % 4];
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Thumbnail || Workspace is not { CanInteract: true } vm || Item is not { } item) return;
        Focus();
        e.Handled = true;
        var position = e.GetPosition(this);
        var point = Normalize(position);
        var index = item.Info.Index;
        // Make this the current page first, so selecting an annotation does not scroll the view.
        vm.SetVisiblePage(index);
        _selection = null;
        _drag = Drag.Text;
        if (vm.Tool == DocumentTool.Select)
        {
            if (e.ClickCount == 2 && vm.HitAnnotation(index, point, Tolerance) is { } clicked)
            {
                vm.SelectedAnnotation = clicked;
                vm.EditAnnotationTextCommand.Execute(null);
                return;
            }
            if (vm.SelectedAnnotation is { CanResize: true } selected && selected.PageIndex == index && ResizeAnchorAt(selected, position) is { } fixedCorner)
            {
                _drag = Drag.Resize;
                _dragAnnotation = selected;
                _resizeFixed = fixedCorner;
            }
            else if (vm.HitAnnotation(index, point, Tolerance, movableOnly: true) is { } movable)
            {
                vm.SelectedAnnotation = movable;
                _drag = Drag.Move;
                _dragAnnotation = movable;
            }
            else vm.SelectedAnnotation = null;
        }
        else if (vm.Tool is DocumentTool.Rectangle or DocumentTool.Ellipse) _drag = Drag.Shape;
        else if (vm.Tool == DocumentTool.Ink)
        {
            _drag = Drag.Ink;
            _ink.Clear();
            _ink.Add(position);
        }
        _anchor = position;
        _dragPoint = position;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_anchor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = HoverCursor(position);
            return;
        }
        _dragPoint = position;
        if (_drag == Drag.Ink && (_ink[^1] - position).Length >= 1.5 && _ink.Count < 20000) _ink.Add(Clamp(position));
        InvalidateVisual();
    }

    private Cursor HoverCursor(Point position)
    {
        if (Thumbnail || Workspace is not { } vm || Item is not { } item) return Cursors.Arrow;
        switch (vm.Tool)
        {
            case DocumentTool.EditText or DocumentTool.AddText:
                return Cursors.IBeam;
            case DocumentTool.Note or DocumentTool.Ink:
                return Cursors.Pen;
            case DocumentTool.Rectangle or DocumentTool.Ellipse:
                return Cursors.Cross;
            case DocumentTool.Select:
                if (vm.SelectedAnnotation is { CanResize: true } selected && selected.PageIndex == item.Info.Index && ResizeAnchorAt(selected, position) is { } opposite)
                    return (opposite.X < position.X) == (opposite.Y < position.Y) ? Cursors.SizeNWSE : Cursors.SizeNESW;
                return vm.HitAnnotation(item.Info.Index, Normalize(position), Tolerance, movableOnly: true) is not null ? Cursors.SizeAll : Cursors.Arrow;
            default:
                return Cursors.Arrow;
        }
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_anchor is not { } anchor || Item is not { } item || Workspace is not { } vm) return;
        var end = e.GetPosition(this);
        var startNormalized = Normalize(anchor);
        var endNormalized = Normalize(end);
        var dragged = (anchor - end).Length > 4;
        var drag = _drag;
        var annotation = _dragAnnotation;
        var ink = _ink.Select(Normalize).ToList();
        var resizeFixed = _resizeFixed;
        _anchor = null;
        _drag = Drag.None;
        _dragAnnotation = null;
        _ink.Clear();
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
        if (!vm.CanInteract || vm.Document != item.Document) return;
        var index = item.Info.Index;
        vm.SetVisiblePage(index);
        try
        {
            switch (drag)
            {
                case Drag.Move when dragged && annotation is not null:
                    await vm.MoveAnnotationAsync(annotation, ClampOffset(annotation.Bounds, end - anchor));
                    return;
                case Drag.Resize when dragged && annotation is not null:
                    var resized = new Rect(Normalize(resizeFixed), endNormalized);
                    if (resized.Width * ActualWidth >= 6 && resized.Height * ActualHeight >= 6) await vm.ResizeAnnotationAsync(annotation, resized);
                    return;
                case Drag.Move or Drag.Resize:
                    return;
                case Drag.Shape:
                    var shape = new Rect(startNormalized, endNormalized);
                    if (shape.Width * ActualWidth >= 4 && shape.Height * ActualHeight >= 4) await vm.AddShapeAsync(index, shape);
                    return;
                case Drag.Ink:
                    if (dragged && ink.Count > 1) await vm.AddInkAsync(index, ink);
                    return;
            }
            if (vm.Tool is DocumentTool.EditText or DocumentTool.AddText or DocumentTool.Note)
            {
                await vm.EditAtAsync(index, startNormalized);
            }
            else if (dragged)
            {
                var selection = await Task.Run(() => item.Document.SelectText(index, startNormalized, endNormalized));
                if (vm.Document != item.Document || Item != item) return;
                if (vm.Tool is DocumentTool.Highlight or DocumentTool.Underline or DocumentTool.StrikeOut && selection.Rectangles.Count > 0)
                {
                    await vm.AddTextMarkupAsync(index, selection.Rectangles);
                }
                else
                {
                    _selection = selection;
                    vm.Status = string.IsNullOrEmpty(selection.Text) ? "Здесь нет текстового слоя. Для скана требуется OCR." : "Текст выделен • Ctrl+C — копировать";
                    InvalidateVisual();
                }
            }
            else if (vm.Tool == DocumentTool.Select)
            {
                if (vm.HitAnnotation(index, startNormalized, Tolerance) is { } hit)
                {
                    vm.SelectedAnnotation = hit;
                    vm.Status = "Аннотация выделена • Delete — удалить • двойной щелчок — комментарий";
                    return;
                }
                if (await vm.TryFillFormAtAsync(index, startNormalized)) return;
                var link = await Task.Run(() => item.Document.HitLink(index, startNormalized));
                if (link is not null && vm.Document == item.Document) vm.OpenLink(link);
            }
        }
        catch (Exception ex)
        {
            vm.ShowError(ex);
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // A lost capture (another window, Alt+Tab) cancels the gesture; a normal release has already consumed it.
        _anchor = null;
        _drag = Drag.None;
        _dragAnnotation = null;
        _ink.Clear();
        InvalidateVisual();
        base.OnLostMouseCapture(e);
    }
}
