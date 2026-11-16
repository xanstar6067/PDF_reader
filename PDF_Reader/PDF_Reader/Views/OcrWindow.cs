using System.Windows;
using System.Windows.Controls;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;
using PDF_Reader.Models;

namespace PDF_Reader.Views;

public sealed class OcrWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _running;

    public OcrWindow(WorkspaceViewModel workspace, SettingsService settings)
    {
        Title = "Распознать текст (OCR)"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Распознавание текста на сканах", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Добавляет невидимый текстовый слой для выделения, копирования и поиска. Изображение страницы сохраняется. Страницы с текстом пропускаются.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        var scope = new ComboBox { ItemsSource = new[] { "Текущая страница", "Весь документ", "Диапазон страниц" }, SelectedIndex = 0 }; panel.Children.Add(scope);
        var range = new TextBox { Text = workspace.CurrentPage.ToString(), IsEnabled = false, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Например: 1, 3-5" }; panel.Children.Add(range);
        scope.SelectionChanged += (_, _) => range.IsEnabled = scope.SelectedIndex == 2;
        var languages = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8) }; panel.Children.Add(languages);
        void UpdateLanguages() => languages.Text = "Языки: " + string.Join(", ", settings.Value.Ocr.Languages.Select(OcrService.LanguageName)) + $" · {settings.Value.Ocr.Dpi} DPI";
        UpdateLanguages();
        var configure = new Button { Content = "Настройки OCR и языки…", HorizontalAlignment = HorizontalAlignment.Left };
        configure.Click += (_, _) => { new OcrSettingsWindow(settings) { Owner = this }.ShowDialog(); UpdateLanguages(); }; panel.Children.Add(configure);
        var status = new TextBlock { Text = "После распознавания сохраните PDF. Каждую обработанную страницу можно отменить через Ctrl+Z.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 10) }; panel.Children.Add(status);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 0, 0, 18) }; panel.Children.Add(progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var start = new Button { Content = "Распознать", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Закрыть" }; buttons.Children.Add(start); buttons.Children.Add(close); panel.Children.Add(buttons);
        close.Click += (_, _) => { if (_running) { _cancellation?.Cancel(); status.Text = "Отмена распознавания…"; } else Close(); };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancellation?.Cancel(); status.Text = "Отмена распознавания…"; } };
        start.Click += async (_, _) =>
        {
            int[] pages;
            try { pages = PrintLayout.SelectPages(scope.SelectedIndex switch { 0 => workspace.CurrentPage.ToString(), 1 => "", _ => range.Text }, workspace.PageCount); }
            catch (Exception ex) { status.Text = ex.Message; return; }
            _running = true; start.IsEnabled = configure.IsEnabled = scope.IsEnabled = range.IsEnabled = false; close.Content = "Отменить";
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            try
            {
                var reporter = new Progress<OcrProgress>(p => { progress.Maximum = p.Total; progress.Value = p.Completed; status.Text = p.Message; });
                var result = await workspace.RecognizeAsync(pages, settings.Value.Ocr, reporter, cancellation.Token);
                status.Text = $"Распознано: {result.Recognized}. Уже содержат текст: {result.Skipped}. Текст не найден: {result.Empty}.\nСохраните PDF, чтобы оставить результат в файле.";
            }
            catch (OperationCanceledException) { status.Text = "Распознавание отменено. Уже обработанные страницы сохранены в документе; их можно отменить через Ctrl+Z."; }
            catch (Exception ex) { status.Text = ex.Message + "\nУже обработанные страницы остаются в документе."; }
            finally
            {
                _running = false; _cancellation = null; close.Content = "Закрыть"; start.IsEnabled = configure.IsEnabled = scope.IsEnabled = true; range.IsEnabled = scope.SelectedIndex == 2;
            }
        };
    }
}
