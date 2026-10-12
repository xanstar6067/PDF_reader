using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDF_Reader.Models;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;

namespace PDF_Reader.Views;

/// <summary>PaperDesk's read-only 3D workspace. PDF scripts are never executed.</summary>
public sealed class ThreeDWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Border _viewport;
    private readonly TextBlock _status = new() { Text = "Чтение модели…", Margin = new Thickness(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap };
    private readonly ThreeDViewState _state = new();
    private readonly CancellationTokenSource _closed = new();
    private ThreeDRenderer? _renderer;
    private bool _running, _requested, _closing;
    private Point? _anchor;
    private bool _pan;
    private readonly PageItem _page;
    private readonly SettingsService _settings;
    public ThreeDWindow(PageItem page, SettingsService settings)
    {
        _page = page; _settings = settings;
        Title = "3D · " + (page.Document.FilePath is { } p ? System.IO.Path.GetFileName(p) : "Документ");
        Width = 1120; Height = 820; MinWidth = 800; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        var root = new DockPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(12) }; DockPanel.SetDock(toolbar, Dock.Top);
        void Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4) }; b.Click += (_, _) => action(); toolbar.Children.Add(b); }
        Button("Вписать модель", () => { _state.Reset(); RequestRender(); });
        Button("Спереди", () => { _state.Pitch = _state.Yaw = 0; RequestRender(); });
        Button("Сверху", () => { _state.Pitch = -MathF.PI / 2; _state.Yaw = 0; RequestRender(); });
        Button("Изометрия", () => { _state.Pitch = -.7f; _state.Yaw = -.7f; RequestRender(); });
        var mode = new ComboBox { Width = 155, Margin = new Thickness(4, 0, 8, 4), ItemsSource = new[] { "Матовый", "Глянцевый", "Металл", "Иллюстрация", "Каркас" }, SelectedIndex = 1 };
        mode.SelectionChanged += (_, _) => { _state.Style = (ThreeDStyle)mode.SelectedIndex; RequestRender(); }; toolbar.Children.Add(mode);
        Button("Настройки ГП…", () => { new GraphicsSettingsWindow(_settings) { Owner = this }.ShowDialog(); });
        root.Children.Add(toolbar);
        var side = new StackPanel { Width = 220, Margin = new Thickness(16) }; DockPanel.SetDock(side, Dock.Right);
        side.Children.Add(new TextBlock { Text = "Освещение", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        void Slider(string title, double min, double max, double value, Action<float> update)
        {
            side.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 10, 0, 4) });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value }; slider.ValueChanged += (_, e) => { update((float)e.NewValue); RequestRender(); }; side.Children.Add(slider);
        }
        Slider("Рассеянный свет", .05, .9, _state.Ambient, v => _state.Ambient = v);
        Slider("Направление света", -Math.PI, Math.PI, _state.LightAngle, v => _state.LightAngle = v);
        Slider("Блеск поверхности", 2, 128, _state.Shininess, v => _state.Shininess = v);
        var background = new CheckBox { Content = "Тёмный фон", Margin = new Thickness(0, 20, 0, 12) }; background.Checked += (_, _) => { _state.DarkBackground = true; RequestRender(); }; background.Unchecked += (_, _) => { _state.DarkBackground = false; RequestRender(); }; side.Children.Add(background);
        var section = new CheckBox { Content = "Сечение модели" }; section.Checked += (_, _) => { _state.Section = true; RequestRender(); }; section.Unchecked += (_, _) => { _state.Section = false; RequestRender(); }; side.Children.Add(section);
        Slider("Положение сечения", -.5, .5, _state.SectionPosition, v => _state.SectionPosition = v);
        side.Children.Add(new TextBlock { Text = "ЛКМ — вращение\nПКМ или Shift+ЛКМ — сдвиг\nКолесо — приближение\nHome — вписать модель", Margin = new Thickness(0, 24, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(side); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var viewport = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = _image };
        _viewport = viewport;
        viewport.MouseDown += (_, e) => { _anchor = e.GetPosition(viewport); _pan = e.ChangedButton == MouseButton.Right || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift); viewport.CaptureMouse(); e.Handled = true; };
        viewport.MouseMove += (_, e) => { if (_anchor is not { } old) return; var p = e.GetPosition(viewport); var d = p - old; _anchor = p; if (_pan) { _state.PanX -= (float)d.X / (float)Math.Max(1, viewport.ActualHeight) * _state.Distance * .7f; _state.PanY += (float)d.Y / (float)Math.Max(1, viewport.ActualHeight) * _state.Distance * .7f; } else { _state.Yaw += (float)d.X * .008f; _state.Pitch += (float)d.Y * .008f; } RequestRender(); };
        viewport.MouseUp += (_, _) => { _anchor = null; viewport.ReleaseMouseCapture(); };
        viewport.LostMouseCapture += (_, _) => _anchor = null;
        viewport.MouseWheel += (_, e) => { _state.Distance = Math.Clamp(_state.Distance * MathF.Exp(-e.Delta * .001f), .15f, 20); RequestRender(); e.Handled = true; };
        viewport.SizeChanged += (_, _) => RequestRender(); root.Children.Add(viewport); Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Home) { _state.Reset(); RequestRender(); e.Handled = true; } };
        Loaded += async (_, _) => await LoadModelAsync();
        Closed += (_, _) => { _closing = true; _closed.Cancel(); var renderer = _renderer; _renderer = null; if (renderer is not null) _ = Task.Run(renderer.Dispose); };
    }
    private async Task LoadModelAsync()
    {
        try
        {
            var scene = await ThreeDService.LoadAsync(_page.Document, _page.Info.Index, _closed.Token);
            var renderer = await Task.Run(() => new ThreeDRenderer(scene, _settings.Value.Preferred3DGpu, _settings.Value.Preferred3DGpuName));
            if (_closing) { renderer.Dispose(); return; } _renderer = renderer;
            var adapters = await Task.Run(ThreeDRenderer.GetAdapters);
            var gpu = renderer.AdapterId == -1 ? "Программный D3D11 (WARP)" : adapters.FirstOrDefault(a => a.Id == renderer.AdapterId)?.Name ?? "D3D11";
            _status.Text = $"{scene.Format} · {scene.Parts.Count:N0} частей · {scene.TriangleCount:N0} треугольников · {gpu}" + (scene.Notice is { } notice ? "\n" + notice : "");
            RequestRender();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) _status.Text = ex.Message; }
    }
    private async void RequestRender()
    {
        _requested = true; if (_running || _renderer is null || _closing) return; _running = true;
        try
        {
            while (_requested && !_closing && _renderer is { } renderer)
            {
                _requested = false; var state = _state.Copy(); var dpi = VisualTreeHelper.GetDpi(this);
                var width = (int)Math.Max(16, _viewport.ActualWidth * dpi.DpiScaleX); var height = (int)Math.Max(16, _viewport.ActualHeight * dpi.DpiScaleY);
                var bitmap = await Task.Run(() => renderer.Render(width, height, state)); if (!_closing) _image.Source = bitmap;
                if (_requested) await Task.Delay(16);
            }
        }
        catch (Exception ex) { if (!_closing) _status.Text = ex.Message; }
        finally { _running = false; }
    }
}
