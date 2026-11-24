using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using PDF_Reader.Services;

namespace PDF_Reader.Views;

internal sealed class PageSourcesWindow : Window
{
    private readonly ObservableCollection<string> _paths;
    private readonly ListBox _files;
    public string[] Paths => _paths.ToArray();

    internal PageSourcesWindow(string[] paths)
    {
        _paths = new(paths);
        Title = "Порядок исходных PDF";
        Width = 700;
        Height = 380;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);

        var panel = new DockPanel { Margin = new Thickness(24) };
        var label = new TextBlock { Text = "Страницы будут добавлены в указанном порядке.", Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);

        _files = new ListBox { ItemsSource = _paths, SelectedIndex = 0 };
        AutomationProperties.SetName(_files, "Исходные PDF");
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var up = new Button { Content = "Выше", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var down = new Button { Content = "Ниже", Padding = up.Padding };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        void UpdateButtons()
        {
            up.IsEnabled = _files.SelectedIndex > 0;
            down.IsEnabled = _files.SelectedIndex >= 0 && _files.SelectedIndex < _paths.Count - 1;
        }
        _files.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = up.Padding, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Продолжить", IsDefault = true, Padding = up.Padding };
        apply.SetResourceReference(StyleProperty, "AccentButton");
        apply.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        buttons.Children.Add(actions);
        panel.Children.Add(_files);
        Content = panel;
    }

    private void Move(int offset)
    {
        var index = _files.SelectedIndex;
        var next = index + offset;
        if (index < 0 || next < 0 || next >= _paths.Count) return;
        _paths.Move(index, next);
        _files.SelectedIndex = next;
        _files.ScrollIntoView(_files.SelectedItem);
    }
}
