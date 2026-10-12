using System.Windows;
using System.Windows.Controls;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.Views;

public sealed class GraphicsSettingsWindow : Window
{
    public GraphicsSettingsWindow(SettingsService settings)
    {
        Title = "Настройки 3D-графики"; Width = 610; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => ThemeService.UpdateTitleBar(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Предпочтительный графический процессор", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var choices = new ComboBox { DisplayMemberPath = nameof(GpuAdapter.Description), MinWidth = 450 }; panel.Children.Add(choices);
        var status = new TextBlock { Text = "Чтение списка адаптеров…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 20) }; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Сохранить", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => { if (choices.SelectedItem is GpuAdapter gpu) { settings.Value.Preferred3DGpu = gpu.Id; settings.Value.Preferred3DGpuName = gpu.Id is 0 or -1 ? null : gpu.Name; ThreeDService.PreferredGpu = gpu.Id; ThreeDService.PreferredGpuName = settings.Value.Preferred3DGpuName; settings.TrySave(); DialogResult = true; } }; buttons.Children.Add(apply);
        var cancel = new Button { Content = "Отмена", IsCancel = true }; buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
        Loaded += async (_, _) =>
        {
            try
            {
                var hardware = await Task.Run(ThreeDRenderer.GetAdapters);
                var adapters = new List<GpuAdapter> { new(0, "Авто: дискретный ГП в приоритете", 0, false) }; adapters.AddRange(hardware); adapters.Add(new(-1, "Программный D3D11 (WARP)", 0, false));
                var selected = adapters.FirstOrDefault(a => a.Id == settings.Value.Preferred3DGpu) ?? adapters.FirstOrDefault(a => a.Name == settings.Value.Preferred3DGpuName);
                choices.ItemsSource = adapters; choices.SelectedItem = selected ?? adapters[0]; apply.IsEnabled = true;
                status.Text = "Авто выбирает дискретный адаптер, если он доступен. Встроенный используется при отсутствии дискретного. Настройка применяется при следующем открытии 3D-просмотрщика; обычные страницы PDF не меняются.";
                if (settings.Value.Preferred3DGpu != 0 && selected is null) status.Text += "\nРанее выбранный адаптер больше не доступен.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
    }
}
