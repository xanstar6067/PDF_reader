using System.Windows;
using System.Windows.Controls;
using PDF_Reader.Services;

namespace PDF_Reader.Views;

/// <summary>Application messages share the theme and keep standard dialog results.</summary>
public sealed class MessageWindow : Window
{
    public MessageBoxResult Result { get; private set; }

    public MessageWindow(string title, string message, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        Result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : buttons == MessageBoxButton.YesNoCancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new ScrollViewer { MaxHeight = 450, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        void Add(string label, MessageBoxResult result, bool primary = false, bool cancel = false)
        {
            var button = new Button { Content = label, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = cancel };
            if (primary) button.Style = (Style)FindResource("AccentButton");
            button.Click += (_, _) => { Result = result; DialogResult = true; };
            actions.Children.Add(button);
        }
        if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {
            Add("Да", MessageBoxResult.Yes, primary: true);
            Add("Нет", MessageBoxResult.No, cancel: buttons == MessageBoxButton.YesNo);
            if (buttons == MessageBoxButton.YesNoCancel) Add("Отмена", MessageBoxResult.Cancel, cancel: true);
        }
        else Add("ОК", MessageBoxResult.OK, primary: true, cancel: true);
        panel.Children.Add(actions);
        Content = panel;
    }

    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        var dialog = new MessageWindow(title, message, buttons) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }
}
