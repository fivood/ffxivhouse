using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FF14HouseReminder.ViewModels;

namespace FF14HouseReminder;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 400;
    private const double SidePanelWidth = 390;

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
            // 设置和房区图共用右侧那块地方，开一个就关掉另一个
            if (e.PropertyName == nameof(MainViewModel.ShowSettings))
            {
                if (_vm.ShowSettings) _vm.ShowMap = false;
                UpdateSidePanel();
            }
            else if (e.PropertyName == nameof(MainViewModel.ShowMap))
            {
                if (_vm.ShowMap) _vm.ShowSettings = false;
                UpdateSidePanel();
            }
        };
        SettingsPanelControl.RequestClose += () => _vm.ShowSettings = false;
        PlotMapPanelControl.RequestClose += () => _vm.ShowMap = false;

        // 房区图要知道哪些地在售：没出现在在售列表里的，就是已经有人住了
        PlotMapPanelControl.OnSaleLookup = (area, slot) =>
        {
            var server = _vm.SelectedServer?.Id ?? 0;
            if (server == 0) return null;
            var sales = App.Store.GetServerSales(server);
            if (sales.Count == 0) return null;   // 该服还没数据，不做判断
            var now = DateTimeOffset.Now;
            var ward = sales.Where(s => s.Data.Area == area && s.Data.Slot == slot).ToList();
            // 在列表里就说明没人住；只有申请期且可购买的才真能去抽，
            // 公示期（等开奖）和准备期（刚炸完等下轮）是空房但抽不了
            bool Buyable(Models.HouseSnapshot s) =>
                Services.LotteryCycle.GetPhase(s.Data, now).State == Models.LotteryState.Available
                && s.Data.PurchaseType is (int)Models.PurchaseType.Lottery or (int)Models.PurchaseType.FCFS;
            return new Views.PlotMapPanel.WardPlots(
                ward.Where(Buyable).Select(s => s.Data.ID).ToHashSet(),
                ward.Where(s => !Buyable(s)).Select(s => s.Data.ID).ToHashSet());
        };
        // 拉到新数据后重画（空置标记跟着变）
        App.Store.DataUpdated += () => Dispatcher.Invoke(() =>
        {
            if (_vm.ShowMap) PlotMapPanelControl.Redraw();
        });

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => _vm.Tick();
        _ticker.Start();
    }

    private double _restoreLeft;

    private void UpdateSidePanel()
    {
        var open = _vm.ShowSettings || _vm.ShowMap;
        SettingsPanelControl.Visibility = _vm.ShowSettings ? Visibility.Visible : Visibility.Collapsed;
        PlotMapPanelControl.Visibility = _vm.ShowMap ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            if (Width <= CollapsedWidth) _restoreLeft = Left;   // 两个面板互切时别把还原位置覆盖掉
            Width = CollapsedWidth + SidePanelWidth;
            // 以左边缘为原点向右展开；超出工作区右缘才左移
            var overflow = Left + Width - SystemParameters.WorkArea.Right;
            if (overflow > 0)
                Left = Math.Max(SystemParameters.WorkArea.Left, Left - overflow);
        }
        else
        {
            Width = CollapsedWidth;
            Left = _restoreLeft;
        }
    }

    /// <summary>滑过关注 / 在售 / 我的房产任一条目，就在房区图上高亮那块地</summary>
    private void Item_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_vm.ShowMap || sender is not FrameworkElement { DataContext: { } data }) return;
        var plot = data switch
        {
            HouseItemViewModel h => (h.Snapshot.Data.Area, h.Snapshot.Data.Slot, h.Snapshot.Data.ID),
            WatchViewModel w => (w.Item.Area, w.Item.Slot, w.Item.Id),
            HomeViewModel m => (m.Item.Area, m.Item.Slot, m.Item.Id),
            _ => (-1, -1, -1)
        };
        if (plot.Item1 >= 0) PlotMapPanelControl.Highlight(plot.Item1, plot.Item2, plot.Item3);
    }

    private void HomesStrip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _vm.ToggleHomesCommand.Execute(null);

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
