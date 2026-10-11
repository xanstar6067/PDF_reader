using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDF_Reader.Models;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;

namespace PDF_Reader;

public partial class MainWindow : Window
{
    public WorkspaceViewModel Workspace { get; }
    private bool _closeApproved, _closing, _navigating, _fullscreen;
    private WindowState _previousState;
    private readonly DispatcherTimer _scrollTimer;
    private readonly DialogService _dialogs;
    public MainWindow() : this(new SettingsService()) { }
    public MainWindow(SettingsService settings)
    {
        InitializeComponent();
        _dialogs = new DialogService(() => this);
        Workspace = new WorkspaceViewModel(_dialogs, settings); DataContext = Workspace;
        Workspace.NavigateRequested += NavigateTo;
        Workspace.FindRequested += () => Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Loaded);
        Workspace.PrintRequested += () => { try { if (Workspace.Document is { } doc) PrintService.Print(doc, Workspace.CurrentPage, this); } catch (Exception ex) { Workspace.ShowError(ex); } };
        Workspace.DefaultAppRequested += () => { try { WindowsIntegration.RegisterAndOpenDefaultApps(); Workspace.Status = "Выберите PaperDesk PDF для расширения .pdf в настройках Windows"; } catch (Exception ex) { Workspace.ShowError(ex); } };
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _scrollTimer.Tick += (_, _) => { _scrollTimer.Stop(); UpdateVisiblePage(); };
        Closed += (_, _) => { _scrollTimer.Stop(); Workspace.Dispose(); };
    }
    public async Task OpenArgumentsAsync(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        foreach (var path in args.Where(a => !a.StartsWith('/') && !a.StartsWith("--")))
        {
            try { await Workspace.OpenPathAsync(path); } catch (Exception ex) { Workspace.ShowError(ex); }
        }
        if (args.Any(a => string.Equals(a, "/p", StringComparison.OrdinalIgnoreCase) || a == "--print") && Workspace.HasDocument)
            Workspace.PrintCommand.Execute(null);
    }
    private void NavigateTo(int index)
    {
        if (index < 0 || index >= Workspace.Pages.Count) return;
        _navigating = true;
        var page = Workspace.Pages[index];
        Dispatcher.BeginInvoke(() =>
        {
            if (!Workspace.Pages.Contains(page)) { _navigating = false; return; }
            PageList.ScrollIntoView(page);
            ThumbnailList.SelectedItem = page; ThumbnailList.ScrollIntoView(page);
            Dispatcher.BeginInvoke(() =>
            {
                if (PageList.ItemContainerGenerator.ContainerFromItem(page) is ListBoxItem container && FindVisual<ScrollViewer>(PageList) is { } viewer)
                {
                    var position = container.TransformToAncestor(PageList).Transform(new Point());
                    var target = Workspace.SelectedMatch is { } match && match.PageIndex == index && match.Rectangles.Count > 0 ? match.Rectangles[0].Y * page.Height - viewer.ViewportHeight * .25 : 0;
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
        double largest = 0; PageItem? visible = null;
        foreach (var child in panel.Children.OfType<ListBoxItem>())
        {
            var y = child.TransformToAncestor(PageList).Transform(new Point()).Y;
            var intersection = Math.Max(0, Math.Min(PageList.ActualHeight, y + child.ActualHeight) - Math.Max(0, y));
            if (intersection > largest && child.DataContext is PageItem item) { largest = intersection; visible = item; }
        }
        if (visible is not null) Workspace.SetVisiblePage(visible.Info.Index);
    }
    private void PageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    { if (e.VerticalChange != 0 && _scrollTimer is not null) { _scrollTimer.Stop(); _scrollTimer.Start(); } }
    private void PageList_SizeChanged(object sender, SizeChangedEventArgs e) { if (DataContext is WorkspaceViewModel vm) vm.SetViewportWidth(e.NewSize.Width); }
    private void PageList_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || !Workspace.CanInteract) return;
        (e.Delta > 0 ? Workspace.ZoomInCommand : Workspace.ZoomOutCommand).Execute(null); e.Handled = true;
    }
    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_navigating && ((ListBox)sender).SelectedItem is PageItem page && Workspace?.CanInteract == true) Workspace.CurrentPage = page.Number; }
    private void Outline_Changed(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is OutlineNode { PageIndex: >= 0 } node) Workspace.CurrentPage = node.PageIndex + 1; }
    private void Comments_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (((ListBox)sender).SelectedItem is PdfComment comment) Workspace.CurrentPage = comment.PageNumber; }
    private async void DeleteComment_Click(object sender, RoutedEventArgs e) { try { if (((Button)sender).Tag is PdfComment comment) await Workspace.DeleteCommentAsync(comment); } catch (Exception ex) { Workspace.ShowError(ex); } }
    private async void CloseTab_Click(object sender, RoutedEventArgs e) { try { if (((Button)sender).Tag is DocumentTab tab) await Workspace.CloseTabAsync(tab); } catch (Exception ex) { Workspace.ShowError(ex); } }
    private async void Recent_Click(object sender, RoutedEventArgs e) { try { if (((Button)sender).Tag is RecentFile file) await Workspace.OpenPathAsync(file.Path); } catch (Exception ex) { Workspace.ShowError(ex); } }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Workspace.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var file in files.Where(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
        { try { await Workspace.OpenPathAsync(file); } catch (Exception ex) { Workspace.ShowError(ex); } }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        try { if (await Workspace.PrepareCloseAsync()) { _closeApproved = true; _ = Dispatcher.BeginInvoke(Close); } }
        catch (Exception ex) { Workspace.ShowError(ex); }
        finally { _closing = false; }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { if (_fullscreen) ToggleFullscreen(); else Workspace.Tool = DocumentTool.Select; e.Handled = true; return; }
        if (e.OriginalSource is TextBox box)
        {
            if (e.Key == Key.Enter) { if (box == SearchBox) Workspace.NextMatchCommand.Execute(null); else box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
            return;
        }
        ICommand? command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Z, ModifierKeys.Control) => Workspace.UndoCommand, (Key.Y, ModifierKeys.Control) => Workspace.RedoCommand,
            (Key.PageDown, ModifierKeys.None) => Workspace.NextPageCommand, (Key.PageUp, ModifierKeys.None) => Workspace.PreviousPageCommand,
            (Key.Home, ModifierKeys.Control) => Workspace.FirstPageCommand, (Key.End, ModifierKeys.Control) => Workspace.LastPageCommand,
            (Key.OemPlus, ModifierKeys.Control) or (Key.Add, ModifierKeys.Control) => Workspace.ZoomInCommand,
            (Key.OemMinus, ModifierKeys.Control) or (Key.Subtract, ModifierKeys.Control) => Workspace.ZoomOutCommand, _ => null
        };
        if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
    }
    private void ToggleFullscreen()
    {
        if (_fullscreen) { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _previousState; }
        else { _previousState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        _fullscreen = !_fullscreen;
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Help_Click(object sender, RoutedEventArgs e) => _dialogs.Info("Горячие клавиши", "Ctrl+O — открыть PDF\nCtrl+N — новый PDF\nCtrl+S — сохранить\nCtrl+Shift+S — сохранить как\nCtrl+P — печать\nCtrl+F — поиск\nF3 / Shift+F3 — следующее / предыдущее совпадение\nCtrl+C — копировать выделение\nCtrl+Z / Ctrl+Y — отменить / повторить правку PDF\nPageUp / PageDown — страницы\nCtrl+Home / Ctrl+End — начало / конец\nCtrl+колесо — масштаб\nCtrl+0 — по ширине, Ctrl+1 — 100%\nF11 — полный экран\nCtrl+W — закрыть вкладку\nEsc — инструмент выделения / выход из полного экрана");
    private void About_Click(object sender, RoutedEventArgs e) => _dialogs.Info("О PaperDesk PDF", "PaperDesk PDF 1.0\nПросмотр, печать и редактирование документов.\n\nДвижок PDFium (Chromium), BSD-3-Clause.\nБинарная сборка bblanchon.PDFium.Win32, Apache-2.0.\n\nТекст изменяется на уровне объектов PDF, с заменой шрифта на Segoe UI. OCR, редактирование вложенных Form XObjects, XFA-форм и создание цифровых подписей пока не поддерживаются.");
}
