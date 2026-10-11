using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PDF_Reader.Views;

public sealed class TextInputWindow : Window
{
    private readonly TextBox _text;
    private readonly PasswordBox _password;
    private readonly TextBox? _size;
    private readonly bool _secret;

    public string Value => _secret ? _password.Password : _text.Text;
    public float TextFontSize => _size is null ? 12 : float.Parse(_size.Text.Replace(',', '.'), CultureInfo.InvariantCulture);

    public TextInputWindow(string title, string description, string value = "", bool multiline = false, bool password = false, float? fontSize = null)
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => PDF_Reader.Services.ThemeService.UpdateTitleBar(this);
        _secret = password;

        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var descriptionBlock = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        descriptionBlock.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(descriptionBlock);
        _text = new TextBox
        {
            Text = value,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 120 : 38,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 20000
        };
        _password = new PasswordBox { MinHeight = 38, Padding = new Thickness(8), MaxLength = 1024 };
        panel.Children.Add(password ? _password : _text);
        if (fontSize is not null)
        {
            panel.Children.Add(new TextBlock { Text = "Размер шрифта, пт (4–200)", Margin = new Thickness(0, 16, 0, 8) });
            _size = new TextBox
            {
                Text = Math.Clamp(fontSize.Value, 4, 200).ToString("0.##", CultureInfo.InvariantCulture),
                Width = 90,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            panel.Children.Add(_size);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "Применить", IsDefault = true, MinWidth = 110, Style = (Style)FindResource("AccentButton") };
        ok.Click += (_, _) =>
        {
            if (_size is not null && !IsValidSize(_size.Text))
            {
                MessageWindow.Show(this, "Введите размер шрифта от 4 до 200 пт.", "Размер шрифта");
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            if (password)
            {
                _password.Focus();
                return;
            }
            _text.Focus();
            _text.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
    }

    private static bool IsValidSize(string text) =>
        float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && float.IsFinite(size) && size is >= 4 and <= 200;
}
