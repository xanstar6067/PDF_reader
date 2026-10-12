using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.Views;

public partial class PrintWindow : Window
{
    private sealed record DuplexChoice(string Label, Duplexing Value) { public override string ToString() => Label; }
    private readonly PdfDocument _document;
    private readonly IReadOnlyList<PdfPageInfo> _pages;
    private readonly int _currentPage;
    private readonly bool _discoverPrinters;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<Task> _pending = [];
    private CancellationTokenSource? _previewCancellation;
    private PrinterProfile? _profile;
    private PrintTicket? _baseTicket;
    private bool _ready, _loadingProfile, _closed;
    private int _version, _printerVersion, _sheetIndex;
    private string? _printerError;

    internal PrintPlan? Plan { get; private set; }
    internal PrintTicket? Ticket { get; private set; }
    internal string? PrinterName => PrinterBox.SelectedItem as string;
    internal string DocumentName { get; }
    internal bool PreviewReady => Plan is not null && PreviewImage.Source is not null && PreviewBusy.Visibility == Visibility.Collapsed;

    internal PrintWindow(PdfDocument document, IReadOnlyList<PdfPageInfo> pages, int currentPage, PrintScaling scaling, string name, bool discoverPrinters = true)
    {
        _document = document;
        _pages = pages;
        _currentPage = Math.Clamp(currentPage, 1, pages.Count);
        _discoverPrinters = discoverPrinters;
        DocumentName = name;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 30);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 30);
        DocumentTitle.Text = $"{name} · {pages.Count} стр.";
        RangeBox.Text = $"1-{pages.Count}";
        ScalingBox.SelectedIndex = (int)scaling;
        SetOfflinePaper();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Track(UpdateAsync()); };
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        Loaded += (_, _) => Track(InitializeAsync());
        Closed += (_, _) => { _closed = true; _debounce.Stop(); _previewCancellation?.Cancel(); };
    }

    private void Track(Task task)
    {
        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(task);
    }

    internal async Task DrainAsync()
    {
        _previewCancellation?.Cancel();
        try { await Task.WhenAll(_pending); } catch (OperationCanceledException) { }
        _previewCancellation?.Dispose();
    }

    private void SetOfflinePaper()
    {
        PaperBox.ItemsSource = new[] { new PaperChoice("A4 · 210 × 297 мм", new PageMediaSize(PageMediaSizeName.ISOA4, 210 * PrintLayout.Mm, 297 * PrintLayout.Mm)),
            new PaperChoice("A3 · 297 × 420 мм", new PageMediaSize(PageMediaSizeName.ISOA3, 297 * PrintLayout.Mm, 420 * PrintLayout.Mm)) };
        PaperBox.SelectedIndex = 0;
        DuplexBox.ItemsSource = new[] { new DuplexChoice("Односторонняя", Duplexing.OneSided) };
        DuplexBox.SelectedIndex = 0;
    }

    private async Task InitializeAsync()
    {
        if (!_discoverPrinters) { _ready = true; Schedule(); return; }
        try
        {
            var list = await Task.Run(PrinterSettingsService.List);
            if (_closed) return;
            _ready = true;
            PrinterBox.ItemsSource = list.Names;
            if (list.Names.Length > 0) PrinterBox.SelectedItem = list.Names.Contains(list.Default) ? list.Default : list.Names[0];
            else { _printerError = "Принтеры не найдены. Предпросмотр доступен для A4/A3."; Schedule(); }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _ready = true;
            _printerError = "Не удалось загрузить принтеры: " + ex.Message;
            Schedule();
        }
    }

    private void PrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PrinterName is not { } name) return;
        Track(LoadPrinterAsync(name));
    }

    private async Task LoadPrinterAsync(string name)
    {
        var version = ++_printerVersion;
        _loadingProfile = true;
        Invalidate();
        PropertiesButton.IsEnabled = false;
        StatusText.Text = "Чтение настроек принтера…";
        try
        {
            var profile = await Task.Run(() => PrinterSettingsService.Load(name));
            if (_closed || version != _printerVersion) return;
            _profile = profile;
            _baseTicket = profile.Ticket;
            _printerError = null;
            PaperBox.ItemsSource = profile.Papers;
            PaperBox.SelectedItem = profile.Papers.FirstOrDefault(p => p.Media.PageMediaSizeName == profile.Ticket.PageMediaSize?.PageMediaSizeName) ?? profile.Papers[0];
            var duplex = new List<DuplexChoice> { new("Односторонняя", Duplexing.OneSided) };
            if (profile.Duplex.Contains(Duplexing.TwoSidedLongEdge)) duplex.Add(new("По длинному краю", Duplexing.TwoSidedLongEdge));
            if (profile.Duplex.Contains(Duplexing.TwoSidedShortEdge)) duplex.Add(new("По короткому краю", Duplexing.TwoSidedShortEdge));
            DuplexBox.ItemsSource = duplex;
            DuplexBox.SelectedIndex = 0;
            PropertiesButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (_closed || version != _printerVersion) return;
            _profile = null;
            _baseTicket = null;
            SetOfflinePaper();
            _printerError = "Не удалось прочитать принтер: " + ex.Message + " Предпросмотр использует поля 5 мм.";
        }
        finally
        {
            if (!_closed && version == _printerVersion) { _loadingProfile = false; Schedule(); }
        }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loadingProfile) return;
        RangeBox.IsEnabled = PageSelectionBox.SelectedIndex == 2;
        ScaleBox.IsEnabled = ScalingBox.SelectedIndex == 3;
        AutoRotateBox.IsEnabled = ModeTabs.SelectedIndex != 1;
        Schedule();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModeTabs || !_ready || _loadingProfile) return;
        AutoRotateBox.IsEnabled = ModeTabs.SelectedIndex != 1;
        if (ModeTabs.SelectedIndex == 1) DuplexBox.SelectedIndex = 0;
        if (ModeTabs.SelectedIndex == 3 && _profile?.Duplex.Contains(Duplexing.TwoSidedShortEdge) == true)
            DuplexBox.SelectedItem = DuplexBox.Items.Cast<DuplexChoice>().First(d => d.Value == Duplexing.TwoSidedShortEdge);
        Schedule();
    }

    private void Invalidate()
    {
        ++_version;
        _previewCancellation?.Cancel();
        Plan = null;
        Ticket = null;
        PrintButton.IsEnabled = false;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        PreviewImage.Source = null;
        TileMap.Children.Clear();
        PreviewBusy.Text = "Подготовка предпросмотра…";
        PreviewBusy.Visibility = Visibility.Visible;
    }

    private void Schedule()
    {
        if (!_ready || _loadingProfile || _closed) return;
        Invalidate();
        _debounce.Stop();
        _debounce.Start();
    }

    private static double Number(TextBox box, string label)
    {
        if (!double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new ArgumentException("Введите число: " + label + ".");
        return number;
    }

    private PrintOptions ReadOptions()
    {
        var mode = (PrintMode)ModeTabs.SelectedIndex;
        return new PrintOptions
        {
            Mode = mode, Scaling = (PrintScaling)ScalingBox.SelectedIndex,
            ScalePercent = mode == PrintMode.Poster ? Number(TileScaleBox, "масштаб постера") : ScalingBox.SelectedIndex == 3 ? Number(ScaleBox, "масштаб") : 100,
            OverlapMm = mode == PrintMode.Poster ? Number(OverlapBox, "нахлёст") : 5,
            GlueMarginsMm = mode == PrintMode.Poster ? new Thickness(Number(GlueLeftBox, "поле слева"), Number(GlueTopBox, "поле сверху"), Number(GlueRightBox, "поле справа"), Number(GlueBottomBox, "поле снизу")) : new(8),
            Columns = mode == PrintMode.Multiple ? Integer(ColumnsBox, "столбцы", 8) : 2,
            Rows = mode == PrintMode.Multiple ? Integer(RowsBox, "строки", 8) : 2,
            PageOrder = (PrintPageOrder)OrderBox.SelectedIndex,
            AutoRotate = AutoRotateBox.IsChecked == true, Grayscale = GrayBox.IsChecked == true,
            CutMarks = CutMarksBox.IsChecked == true, Labels = LabelsBox.IsChecked == true,
            TileOnlyLargePages = LargeOnlyBox.IsChecked == true, PageBorder = mode == PrintMode.Multiple && BorderBox.IsChecked == true,
            BookletSide = (BookletSide)BookletSideBox.SelectedIndex, RightBinding = RightBindingBox.IsChecked == true
        };
    }

    private static int Integer(TextBox box, string label, int max)
    {
        if (!int.TryParse(box.Text.Trim(), out var value) || value < 1 || value > max) throw new ArgumentException($"{label}: введите целое число от 1 до {max}.");
        return value;
    }

    private async Task UpdateAsync()
    {
        var version = _version;
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellation = _previewCancellation.Token;
        try
        {
            var options = ReadOptions();
            var range = PageSelectionBox.SelectedIndex switch { 1 => _currentPage.ToString(), 2 => RangeBox.Text, _ => $"1-{_pages.Count}" };
            var pages = PrintLayout.SelectPages(range, _pages.Count, (PrintPageFilter)FilterBox.SelectedIndex, ReverseBox.IsChecked == true);
            var copies = Integer(CopiesBox, "Копии", Math.Max(1, Math.Min(999, _profile?.MaxCopies ?? 999)));
            var paper = ((PaperChoice)PaperBox.SelectedItem).Media;
            var landscape = OrientationBox.SelectedIndex == 2 || OrientationBox.SelectedIndex == 0 &&
                (options.Mode == PrintMode.Booklet || (options.Mode == PrintMode.Multiple ? options.Columns > options.Rows : _pages[pages[0]].Width > _pages[pages[0]].Height));
            var requested = (_baseTicket ?? new PrintTicket()).Clone();
            requested.PageMediaSize = paper;
            requested.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            requested.CopyCount = copies;
            requested.Collation = CollateBox.IsChecked == true ? Collation.Collated : Collation.Uncollated;
            requested.Duplexing = options.Mode == PrintMode.Booklet && options.BookletSide != BookletSide.Both ? Duplexing.OneSided : ((DuplexChoice)DuplexBox.SelectedItem).Value;
            requested.PagesPerSheet = 1; // PaperDesk has already imposed the PDF pages.
            requested.PageScalingFactor = 100; // Do not let driver scaling apply a second time.
            var name = PrinterName;
            PrinterGeometry geometry;
            if (name is not null && _profile is not null)
                geometry = await Task.Run(() => PrinterSettingsService.Resolve(name, requested), cancellation);
            else
            {
                var size = landscape ? new Size(paper.Height!.Value, paper.Width!.Value) : new Size(paper.Width!.Value, paper.Height!.Value);
                geometry = new(requested, size, new Rect(5 * PrintLayout.Mm, 5 * PrintLayout.Mm, size.Width - 10 * PrintLayout.Mm, size.Height - 10 * PrintLayout.Mm), _printerError);
            }
            if (_closed || version != _version) return;
            var plan = await Task.Run(() => PrintLayout.Create(_pages, pages, geometry.Paper, geometry.Area, options), cancellation);
            if (_closed || version != _version) return;
            _sheetIndex = Math.Clamp(_sheetIndex, 0, plan.Sheets.Count - 1);
            var bitmap = await PrintService.RenderPreviewAsync(_document, plan, _sheetIndex, DocumentName, cancellation);
            if (_closed || version != _version) return;
            Plan = plan;
            Ticket = geometry.Ticket;
            PreviewImage.Source = bitmap;
            PreviewBusy.Visibility = Visibility.Collapsed;
            PaperSummary.Text = $"{plan.Paper.Width / PrintLayout.Mm:0.#} × {plan.Paper.Height / PrintLayout.Mm:0.#} мм · {(plan.Paper.Width > plan.Paper.Height ? "альбомная" : "книжная")}";
            if (options.Mode == PrintMode.Poster)
            {
                var source = _pages[plan.Sheets[_sheetIndex].PageIndex];
                PaperSummary.Text += $"\nПолотно: {source.Width * 25.4 / 72 * options.ScalePercent / 100:0.#} × {source.Height * 25.4 / 72 * options.ScalePercent / 100:0.#} мм · {options.ScalePercent:0.#} %";
            }
            var duplex = geometry.Ticket.Duplexing is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge;
            var bookletBlocked = options.Mode == PrintMode.Booklet && options.BookletSide == BookletSide.Both && !duplex;
            var warning = bookletBlocked ? "Для обеих сторон брошюры включите дуплекс или выберите только лицевые/оборотные стороны." : geometry.Warning;
            StatusText.Text = warning ?? $"{pages.Length} стр. PDF → {plan.Sheets.Count} сторон для печати · {copies} коп. · {(duplex ? (plan.Sheets.Count + 1) / 2 : plan.Sheets.Count) * copies} листов бумаги";
            if (options.Mode == PrintMode.Booklet && !bookletBlocked && duplex)
                StatusText.Text += " · Для брошюры проверьте край переворота в предпросмотре и свойствах принтера.";
            PrintButton.IsEnabled = name is not null && _profile is not null && !bookletBlocked;
            UpdateNavigation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed || version != _version) return;
            Plan = null;
            Ticket = null;
            PrintButton.IsEnabled = false;
            PreviewBusy.Text = "Проверьте настройки печати";
            StatusText.Text = ex.Message;
        }
    }

    private void UpdateNavigation()
    {
        if (Plan is not { } plan) return;
        SheetCounter.Text = $"Лист {_sheetIndex + 1} из {plan.Sheets.Count}";
        var sheet = plan.Sheets[_sheetIndex];
        SheetLabel.Text = sheet.Label;
        PreviousButton.IsEnabled = _sheetIndex > 0;
        NextButton.IsEnabled = _sheetIndex + 1 < plan.Sheets.Count;
        TileMap.Children.Clear();
        if (sheet.Rows < 1 || sheet.Rows * sheet.Columns > 100) return;
        TileMap.Width = sheet.Columns * 44;
        TileMap.MaxWidth = 440;
        // Large grids retain row-column labels on the sheet without an unreadable overview.
        if (sheet.Columns > 10 || sheet.Rows > 10) return;
        for (var r = 1; r <= sheet.Rows; r++) for (var c = 1; c <= sheet.Columns; c++)
        {
            var index = plan.Sheets.ToList().FindIndex(s => s.PageIndex == sheet.PageIndex && s.Row == r && s.Column == c);
            var button = new Button { Content = $"{r}-{c}", Width = 40, Padding = new Thickness(2), Margin = new Thickness(2), FontSize = 10, ToolTip = $"Строка {r}, столбец {c}" };
            if (r == sheet.Row && c == sheet.Column) button.SetResourceReference(BackgroundProperty, "Selected");
            button.Click += (_, _) => { _sheetIndex = index; Schedule(); };
            TileMap.Children.Add(button);
        }
    }

    private void PreviousClick(object sender, RoutedEventArgs e) { if (_sheetIndex > 0) { --_sheetIndex; Schedule(); } }
    private void NextClick(object sender, RoutedEventArgs e) { if (Plan is { } plan && _sheetIndex + 1 < plan.Sheets.Count) { ++_sheetIndex; Schedule(); } }

    private void PropertiesClick(object sender, RoutedEventArgs e)
    {
        if (PrinterName is not { } name || _baseTicket is null) return;
        try
        {
            var ticket = PrinterSettingsService.ShowProperties(this, name, Ticket ?? _baseTicket);
            if (ticket is null) return;
            _loadingProfile = true;
            _baseTicket = ticket;
            var media = ticket.PageMediaSize;
            if (media is not null) PaperBox.SelectedItem = PaperBox.Items.Cast<PaperChoice>().FirstOrDefault(p => p.Media.PageMediaSizeName == media.PageMediaSizeName) ?? PaperBox.SelectedItem;
            OrientationBox.SelectedIndex = ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape ? 2 : 1;
            CopiesBox.Text = (ticket.CopyCount ?? 1).ToString();
            DuplexBox.SelectedItem = DuplexBox.Items.Cast<DuplexChoice>().FirstOrDefault(d => d.Value == ticket.Duplexing) ?? DuplexBox.Items[0];
        }
        catch (Exception ex) { MessageWindow.Show(this, ex.Message, "Свойства принтера"); }
        finally { _loadingProfile = false; Schedule(); }
    }

    private void PrintClick(object sender, RoutedEventArgs e)
    {
        if (PrintButton.IsEnabled && Plan is not null && Ticket is not null) DialogResult = true;
    }
}
