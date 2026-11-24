using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDF_Reader.Models;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;

namespace PDF_Reader;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(60);
    private bool _closeApproved, _closing, _navigating, _fullscreen;
    private bool _syncingThumbnails, _deferThumbnailClick;
    private PageItem? _thumbnailDragPage;
    private Point _thumbnailDragStart;
    private ThumbnailDropIndicator? _thumbnailDropIndicator;
    private AdornerLayer? _thumbnailDropLayer;
    private WindowState _previousState;
    private readonly DispatcherTimer _scrollTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DialogService _dialogs;
    private readonly SettingsService _settings;
    public ThemeService Themes { get; }

    public WorkspaceViewModel Workspace { get; }

    public MainWindow() : this(new SettingsService()) { }

    private void Open3D_Click(object sender, RoutedEventArgs e)
    {
        var page = sender is FrameworkElement { Tag: PageItem item } ? item : Workspace.Pages.ElementAtOrDefault(Workspace.CurrentPage - 1);
        if (page is null || !page.Info.Has3D)
        {
            Views.MessageWindow.Show(this, "На текущей странице нет встроенной 3D-модели.", "3D-просмотр");
            return;
        }
        new Views.ThreeDWindow(page, _settings) { Owner = this }.Show();
    }

    private void GraphicsSettings_Click(object sender, RoutedEventArgs e) => new Views.GraphicsSettingsWindow(_settings) { Owner = this }.ShowDialog();

    private void OcrSettings_Click(object sender, RoutedEventArgs e)
    {
        new Views.OcrSettingsWindow(_settings) { Owner = this }.ShowDialog();
        Workspace.ResetOcr();
    }

    public MainWindow(SettingsService settings, RecoveryService? recovery = null)
    {
        _settings = settings;
        ThreeDService.PreferredGpu = settings.Value.Preferred3DGpu;
        ThreeDService.PreferredGpuName = settings.Value.Preferred3DGpuName;
        Themes = new ThemeService(settings);
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        UpdateThemeMenu();
        _dialogs = new DialogService(() => this);
        Workspace = new WorkspaceViewModel(_dialogs, settings, recovery);
        DataContext = Workspace;
        Workspace.NavigateRequested += NavigateTo;
        Workspace.ThumbnailSelectionRequested += SelectThumbnails;
        Workspace.FindRequested += () => Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Loaded);
        Workspace.PrintRequested += PrintAsync;
        Workspace.DefaultAppRequested += () =>
        {
            try
            {
                WindowsIntegration.RegisterAndOpenDefaultApps();
                Workspace.Status = "Выберите PaperDesk PDF для расширения .pdf в настройках Windows";
            }
            catch (Exception ex)
            {
                Workspace.ShowError(ex);
            }
        };
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _scrollTimer.Tick += (_, _) =>
        {
            _scrollTimer.Stop();
            UpdateVisiblePage();
        };
        _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += async (_, _) => await Workspace.AutosaveAsync();
        _autosaveTimer.Start();
        Closed += (_, _) =>
        {
            _scrollTimer.Stop();
            _autosaveTimer.Stop();
            Themes.Dispose();
            Workspace.Dispose();
        };
    }

    /// <summary>Startup: offer documents left by a crash, then open the command-line files.</summary>
    public async Task StartAsync(IEnumerable<string> arguments)
    {
        try
        {
            await Workspace.RestoreRecoveredAsync();
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
        await OpenArgumentsAsync(arguments);
    }

    /// <summary>Arguments from a second launch of the application.</summary>
    public async Task ReceiveArgumentsAsync(string[] arguments)
    {
        if (WindowState == WindowState.Minimized) WindowState = _fullscreen ? WindowState.Maximized : WindowState.Normal;
        Activate();
        await OpenArgumentsAsync(arguments);
    }

    public async Task OpenArgumentsAsync(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        foreach (var path in args.Where(a => !a.StartsWith('/') && !a.StartsWith("--")))
        {
            // Files arriving while a dialog or an operation is running wait instead of being dropped.
            while (Workspace.IsBusy) await Task.Delay(100);
            try
            {
                await Workspace.OpenPathAsync(path);
            }
            catch (Exception ex)
            {
                Workspace.ShowError(ex);
            }
        }
        var print = args.Any(a => string.Equals(a, "/p", StringComparison.OrdinalIgnoreCase) || a == "--print");
        if (print && Workspace.PrintCommand.CanExecute(null)) Workspace.PrintCommand.Execute(null);
    }

    private async void PrintAsync()
    {
        if (Workspace.Document is not { } document || Workspace.IsPrinting) return;
        Workspace.IsPrinting = true;
        try
        {
            var sent = await PrintService.PrintAsync(this, document, Workspace.CurrentPage, Workspace.PrintScaling, status => Workspace.Status = status);
            Workspace.Status = sent ? "Задание отправлено на принтер" : "Печать отменена";
        }
        catch (OperationCanceledException)
        {
            Workspace.Status = "Печать отменена";
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
        finally
        {
            Workspace.IsPrinting = false;
        }
    }

    private void NavigateTo(int index)
    {
        if (index < 0 || index >= Workspace.Pages.Count) return;
        _navigating = true;
        var page = Workspace.Pages[index];
        Dispatcher.BeginInvoke(() =>
        {
            if (!Workspace.Pages.Contains(page))
            {
                _navigating = false;
                return;
            }
            PageList.ScrollIntoView(page);
            if (ThumbnailList.SelectedItems.Count <= 1)
            {
                _syncingThumbnails = true;
                ThumbnailList.SelectedItem = page;
                _syncingThumbnails = false;
            }
            ThumbnailList.ScrollIntoView(page);
            Dispatcher.BeginInvoke(() =>
            {
                if (PageList.ItemContainerGenerator.ContainerFromItem(page) is ListBoxItem container && FindVisual<ScrollViewer>(PageList) is { } viewer)
                {
                    var position = container.TransformToAncestor(PageList).Transform(new Point());
                    var target = Workspace.SelectedMatch is { } match && match.PageIndex == index && match.Rectangles.Count > 0
                        ? match.Rectangles[0].Y * page.Height - viewer.ViewportHeight * .25
                        : 0;
                    viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + position.Y + target));
                }
                Dispatcher.BeginInvoke(() => _navigating = false, DispatcherPriority.ContextIdle);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void UpdateVisiblePage()
    {
        if (_navigating || Workspace.IsBusy || !Workspace.HasDocument || FindVisual<VirtualizingStackPanel>(PageList) is not { } panel) return;
        double largest = 0;
        PageItem? visible = null;
        foreach (var child in panel.Children.OfType<ListBoxItem>())
        {
            var y = child.TransformToAncestor(PageList).Transform(new Point()).Y;
            var intersection = Math.Max(0, Math.Min(PageList.ActualHeight, y + child.ActualHeight) - Math.Max(0, y));
            if (intersection <= largest || child.DataContext is not PageItem item) continue;
            largest = intersection;
            visible = item;
        }
        if (visible is not null) Workspace.SetVisiblePage(visible.Info.Index);
    }

    private void PageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _scrollTimer is null) return;
        _scrollTimer.Stop();
        _scrollTimer.Start();
    }

    private void PageList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel vm) vm.SetViewportWidth(e.NewSize.Width);
    }

    private void PageList_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || !Workspace.CanInteract) return;
        (e.Delta > 0 ? Workspace.ZoomInCommand : Workspace.ZoomOutCommand).Execute(null);
        e.Handled = true;
    }

    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbnails || Workspace?.CanInteract != true) return;
        Workspace.SetSelectedThumbnails(ThumbnailList.SelectedItems.OfType<PageItem>().Select(page => page.Info.Index).Order().ToArray());
        if (e.AddedItems.OfType<PageItem>().LastOrDefault() is { } page)
            Workspace.CurrentPage = page.Number;
    }

    private void SelectThumbnails(int[] indices)
    {
        _syncingThumbnails = true;
        try
        {
            ThumbnailList.UnselectAll();
            foreach (var index in indices) ThumbnailList.SelectedItems.Add(Workspace.Pages[index]);
        }
        finally { _syncingThumbnails = false; }
    }

    private ListBoxItem? ThumbnailAt(object source) =>
        source is DependencyObject hit ? ItemsControl.ContainerFromElement(ThumbnailList, hit) as ListBoxItem : null;

    private void Thumbnails_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _thumbnailDragPage = null;
        _deferThumbnailClick = false;
        if (Keyboard.Modifiers != ModifierKeys.None || !Workspace.CanInteract ||
            ThumbnailAt(e.OriginalSource) is not { Content: PageItem page } row) return;
        if (Workspace.Info?.CanAssemble != true) return;
        _thumbnailDragPage = page;
        _thumbnailDragStart = e.GetPosition(ThumbnailList);
        _deferThumbnailClick = row.IsSelected && ThumbnailList.SelectedItems.Count > 1;
        if (_deferThumbnailClick) e.Handled = true;
    }

    private void Thumbnails_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferThumbnailClick && _thumbnailDragPage is { } page &&
            ThumbnailAt(e.OriginalSource) is { Content: PageItem released } row && ReferenceEquals(page, released))
        {
            ThumbnailList.UnselectAll();
            ThumbnailList.SelectedItem = page;
            row.Focus();
            Workspace.CurrentPage = page.Number;
            e.Handled = true;
        }
        _thumbnailDragPage = null;
        _deferThumbnailClick = false;
    }

    private void Thumbnails_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _thumbnailDragPage = null;
            _deferThumbnailClick = false;
            return;
        }
        if (_thumbnailDragPage is not { } page || !Workspace.CanInteract || Workspace.Document != page.Document) return;
        var movement = e.GetPosition(ThumbnailList) - _thumbnailDragStart;
        if (Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _thumbnailDragPage = null;
        _deferThumbnailClick = false;
        if (!ThumbnailList.SelectedItems.Contains(page)) ThumbnailList.SelectedItem = page;
        if (ThumbnailList.SelectedItems.Count == 1) Workspace.SetVisiblePage(page.Info.Index);
        e.Handled = true;
        try { DragDrop.DoDragDrop(ThumbnailList, new DataObject(typeof(PageItem), page), DragDropEffects.Move); }
        finally { ClearThumbnailDropIndicator(); }
    }

    private (int Boundary, ListBoxItem Row, bool After)? ThumbnailDropTarget(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PageItem)) is not PageItem dragging || Workspace.Document != dragging.Document ||
            !Workspace.CanInteract || Workspace.Info?.CanAssemble != true) return null;
        if (ThumbnailAt(e.OriginalSource) is { Content: PageItem target } row)
        {
            var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
            return (target.Info.Index + (after ? 1 : 0), row, after);
        }
        if (ThumbnailList.ItemContainerGenerator.ContainerFromIndex(Workspace.PageCount - 1) is ListBoxItem last)
        {
            var point = e.GetPosition(last);
            if (point.Y >= last.ActualHeight && point.X >= 0 && point.X <= last.ActualWidth)
                return (Workspace.PageCount, last, true);
        }
        return null;
    }

    private void Thumbnails_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PageItem))) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (ThumbnailDropTarget(e) is not { } target)
        {
            ClearThumbnailDropIndicator();
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (FindVisual<ScrollViewer>(ThumbnailList) is { } viewer)
        {
            var y = e.GetPosition(viewer).Y;
            if (y < 28) viewer.ScrollToVerticalOffset(viewer.VerticalOffset - 18);
            else if (y > viewer.ActualHeight - 28) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 18);
        }
        if (_thumbnailDropIndicator is { } prior && prior.AdornedElement == target.Row && prior.After == target.After) return;
        ClearThumbnailDropIndicator();
        if (AdornerLayer.GetAdornerLayer(target.Row) is not { } layer) return;
        _thumbnailDropLayer = layer;
        _thumbnailDropIndicator = new ThumbnailDropIndicator(target.Row, target.After, (Brush)FindResource("Accent")) { IsHitTestVisible = false };
        layer.Add(_thumbnailDropIndicator);
    }

    private void Thumbnails_DragLeave(object sender, DragEventArgs e) => ClearThumbnailDropIndicator();

    private async void Thumbnails_Drop(object sender, DragEventArgs e)
    {
        ClearThumbnailDropIndicator();
        if (!e.Data.GetDataPresent(typeof(PageItem))) return;
        e.Handled = true;
        if (ThumbnailDropTarget(e) is not { } target) return;
        try { await Workspace.MovePagesAsync(target.Boundary); }
        catch (Exception ex) { Workspace.ShowError(ex); }
    }

    private void ClearThumbnailDropIndicator()
    {
        if (_thumbnailDropIndicator is not { } indicator) return;
        _thumbnailDropLayer?.Remove(indicator);
        _thumbnailDropLayer = null;
        _thumbnailDropIndicator = null;
    }

    private sealed class ThumbnailDropIndicator(UIElement element, bool after, Brush brush) : Adorner(element)
    {
        public bool After { get; } = after;
        protected override void OnRender(DrawingContext context)
        {
            var y = After ? AdornedElement.RenderSize.Height - 2 : 2;
            context.DrawLine(new Pen(brush, 3), new Point(6, y), new Point(Math.Max(6, AdornedElement.RenderSize.Width - 6), y));
        }
    }

    private void Outline_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { PageIndex: >= 0 } node) Workspace.CurrentPage = node.PageIndex + 1;
    }

    private void Comments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is PdfComment comment) Workspace.CurrentPage = comment.PageNumber;
    }

    private async void DeleteComment_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is PdfComment comment) await Workspace.DeleteCommentAsync(comment);
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is DocumentTab tab) await Workspace.CloseTabAsync(tab);
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
    }

    private async void Recent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((Button)sender).Tag is RecentFile file) await Workspace.OpenPathAsync(file.Path);
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
    }

    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Workspace.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsPdf)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        await OpenArgumentsAsync(files.Where(IsPdf));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (await Workspace.PrepareCloseAsync())
            {
                _closeApproved = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        catch (Exception ex)
        {
            Workspace.ShowError(ex);
        }
        finally
        {
            _closing = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_fullscreen) ToggleFullscreen();
            else if (Workspace.SelectedAnnotation is not null) Workspace.SelectedAnnotation = null;
            else Workspace.Tool = DocumentTool.Select;
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox box)
        {
            if (e.Key != Key.Enter) return;
            if (box == SearchBox) Workspace.NextMatchCommand.Execute(null);
            else box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }
        ICommand? command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Delete, ModifierKeys.None) => ThumbnailList.IsKeyboardFocusWithin ? Workspace.DeletePageCommand : Workspace.DeleteAnnotationCommand,
            (Key.Z, ModifierKeys.Control) => Workspace.UndoCommand,
            (Key.Y, ModifierKeys.Control) => Workspace.RedoCommand,
            (Key.PageDown, ModifierKeys.None) => Workspace.NextPageCommand,
            (Key.PageUp, ModifierKeys.None) => Workspace.PreviousPageCommand,
            (Key.Home, ModifierKeys.Control) => Workspace.FirstPageCommand,
            (Key.End, ModifierKeys.Control) => Workspace.LastPageCommand,
            (Key.OemPlus, ModifierKeys.Control) or (Key.Add, ModifierKeys.Control) => Workspace.ZoomInCommand,
            (Key.OemMinus, ModifierKeys.Control) or (Key.Subtract, ModifierKeys.Control) => Workspace.ZoomOutCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _previousState;
        }
        else
        {
            _previousState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        _fullscreen = !_fullscreen;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && Enum.TryParse<AppTheme>(value, out var theme))
        {
            Themes.SetTheme(theme);
            UpdateThemeMenu();
        }
    }

    private void UpdateThemeMenu()
    {
        SystemThemeMenu.IsChecked = Themes.Theme == AppTheme.System;
        LightThemeMenu.IsChecked = Themes.Theme == AppTheme.Light;
        DarkThemeMenu.IsChecked = Themes.Theme == AppTheme.Dark;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Help_Click(object sender, RoutedEventArgs e) => _dialogs.Info("Горячие клавиши",
        "Ctrl+O — открыть PDF\nCtrl+N — новый PDF\nCtrl+S — сохранить\nCtrl+Shift+S — сохранить как\nCtrl+P — печать\nCtrl+F — поиск\n" +
        "F3 / Shift+F3 — следующее / предыдущее совпадение\nCtrl+C — копировать выделение\nCtrl+Z / Ctrl+Y — отменить / повторить правку PDF\n" +
        "PageUp / PageDown — страницы\nCtrl+Home / Ctrl+End — начало / конец\nCtrl+колесо — масштаб\nCtrl+0 — по ширине, Ctrl+1 — 100%\n" +
        "F11 — полный экран\nCtrl+W — закрыть вкладку\n" +
        "Delete — удалить выбранную аннотацию\nДвойной щелчок по аннотации — комментарий\n" +
        "Esc — снять выделение аннотации / инструмент выделения / выход из полного экрана");

    private void About_Click(object sender, RoutedEventArgs e) => _dialogs.Info("О PaperDesk PDF",
        "PaperDesk PDF 1.0\nПросмотр, печать и редактирование документов.\n\n" +
        "Движок PDFium (Chromium), BSD-3-Clause.\nБинарная сборка bblanchon.PDFium.Win32, Apache-2.0.\n\n" +
        "Текст изменяется на уровне объектов PDF: исходный шрифт сохраняется, если в нём есть нужные символы, иначе встраивается подмножество Segoe UI. " +
        "OCR, редактирование вложенных Form XObjects, XFA-форм и создание цифровых подписей пока не поддерживаются.");
}
