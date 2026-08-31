using System.Windows;
using System.Windows.Threading;
using FF14HouseReminder.ViewModels;

namespace FF14HouseReminder;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 400;
    private const double SettingsPanelWidth = 390;

    private readonly DispatcherTimer _ticker;
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        // 默认高度跟随屏幕工作区（约 90%），不低于 780
        Height = Math.Max(780, SystemParameters.WorkArea.Height * 0.9);
        MaxHeight = SystemParameters.WorkArea.Height;

        _vm = new MainViewModel(App.Config, App.Store, App.Polling, App.Reminders, App.Updates);
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowSettings))
                UpdateSettingsPanel(_vm.ShowSettings);
        };
        SettingsPanelControl.RequestClose += () => _vm.ShowSettings = false;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => _vm.Tick();
        _ticker.Start();
    }

    private double _restoreLeft;

    private void UpdateSettingsPanel(bool open)
    {
        if (open)
        {
            _restoreLeft = Left;
            SettingsPanelControl.Visibility = Visibility.Visible;
            Width = CollapsedWidth + SettingsPanelWidth;
            // 锚定右边缘展开，避免超出屏幕
            Left = Math.Max(SystemParameters.WorkArea.Left, _restoreLeft - SettingsPanelWidth);
        }
        else
        {
            SettingsPanelControl.Visibility = Visibility.Collapsed;
            Width = CollapsedWidth;
            Left = _restoreLeft;
        }
    }

    // 关闭时最小化到托盘（托盘菜单"退出"才真正退出）
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}

/// <summary>取反布尔转换器</summary>
public class InverseBoolConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool b && !b;
}
