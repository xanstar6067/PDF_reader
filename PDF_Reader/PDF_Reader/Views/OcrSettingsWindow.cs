using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.Views;

public sealed class OcrSettingsWindow : Window
{
    private readonly StackPanel _languages = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly List<CheckBox> _checks = [];
    private bool _importing;

    public OcrSettingsWindow(SettingsService settings)
    {
        Title = "Настройки распознавания текста"; Width = 570; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Языки OCR", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Выберите языки документа. Русский и английский включены в поставку.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(new ScrollViewer { Content = _languages, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        LoadLanguages(settings.Value.Ocr.Languages);
        var import = new Button { Content = "Добавить язык из файла…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(import);
        panel.Children.Add(new TextBlock { Text = "Файлы *.traineddata из tessdata_fast или tessdata_best для Tesseract 4/5. Сохраняйте исходное имя файла, например deu.traineddata. Распознавание работает без интернета.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var sourceLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Официальные языковые пакеты (tessdata_fast)")) { NavigateUri = new Uri("https://github.com/tesseract-ocr/tessdata_fast") };
        sourceLink.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Accent");
        sourceLink.RequestNavigate += (_, e) => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        var sourceText = new TextBlock(); sourceText.Inlines.Add(sourceLink); panel.Children.Add(sourceText);
        Label(panel, "Разрешение изображения");
        var dpi = new ComboBox { ItemsSource = new[] { 200, 300, 400, 600 }, SelectedItem = settings.Value.Ocr.Dpi }; panel.Children.Add(dpi);
        panel.Children.Add(new TextBlock { Text = "300 DPI — по умолчанию. Более высокое разрешение увеличивает время и расход памяти. Большие листы ограничиваются 24 млн пикселей.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Label(panel, "Разметка страницы");
        var modes = new[] { new Mode(3, "Автоматически (по умолчанию)"), new Mode(6, "Один блок текста"), new Mode(11, "Разрозненные надписи"), new Mode(7, "Одна строка") };
        var segmentation = new ComboBox { ItemsSource = modes, DisplayMemberPath = nameof(Mode.Name), SelectedValuePath = nameof(Mode.Id), SelectedValue = settings.Value.Ocr.PageSegmentation }; panel.Children.Add(segmentation);
        var dictionaries = new CheckBox { Content = "Использовать языковые словари", IsChecked = settings.Value.Ocr.UseDictionaries, Margin = new Thickness(0, 14, 0, 0) }; panel.Children.Add(dictionaries);
        var spaces = new CheckBox { Content = "Сохранять увеличенные пробелы при распознавании", IsChecked = settings.Value.Ocr.PreserveSpaces, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(spaces);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            try
            {
                var value = new OcrOptions { Languages = SelectedLanguages(), Dpi = (int)dpi.SelectedItem, PageSegmentation = (int)segmentation.SelectedValue,
                    UseDictionaries = dictionaries.IsChecked == true, PreserveSpaces = spaces.IsChecked == true };
                value.Validate(); settings.Value.Ocr = value; settings.TrySave(); DialogResult = true;
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        Closing += (_, e) => { if (_importing) e.Cancel = true; };
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Языковой пакет Tesseract|*.traineddata", Title = "Добавить язык OCR" };
            if (dialog.ShowDialog(this) != true) return;
            var code = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            if (OcrService.Languages().Contains(code, StringComparer.OrdinalIgnoreCase) &&
                MessageWindow.Show(this, $"Заменить языковой пакет «{OcrService.LanguageName(code)}»?", "Языки OCR", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _importing = true; save.IsEnabled = cancel.IsEnabled = import.IsEnabled = false;
            _status.Text = "Проверка и установка языкового пакета…";
            try
            {
                var selected = SelectedLanguages().Append(code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                await Task.Run(() => OcrService.ImportLanguageAsync(dialog.FileName));
                LoadLanguages(selected); _status.Text = "Языковой пакет установлен. Он останется доступным после закрытия настроек.";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _importing = false; save.IsEnabled = cancel.IsEnabled = import.IsEnabled = true; }
        };
    }

    private string[] SelectedLanguages() => _checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
    private void LoadLanguages(string[] selected)
    {
        _checks.Clear(); _languages.Children.Clear();
        foreach (var code in OcrService.Languages())
        {
            var check = new CheckBox { Content = $"{OcrService.LanguageName(code)} ({code})", Tag = code, IsChecked = selected.Contains(code, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 4, 0, 4) };
            _checks.Add(check); _languages.Children.Add(check);
        }
    }
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
    private sealed record Mode(int Id, string Name);
}
