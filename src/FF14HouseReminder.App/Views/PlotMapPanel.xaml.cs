using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FF14HouseReminder.Models;

namespace FF14HouseReminder.Views;

/// <summary>
/// 房区图面板：底图 + 30 块地的方框，滑过房屋列表时高亮对应那块。
/// 坐标换算见 <see cref="HousingMap"/>；画布直接用世界坐标，外层 Viewbox 负责缩放。
/// </summary>
public partial class PlotMapPanel : UserControl
{
    private static readonly Brush CellStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x6C)));
    private static readonly Brush HitStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x54, 0x5E)));
    private static readonly Brush HitFill = Freeze(new SolidColorBrush(Color.FromArgb(0x6B, 0x2D, 0x8C, 0x9D)));
    private static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    // 三档：可抽（橙实）／空置未开放（白，公示期或准备期）／已住人（灰）
    private static readonly Brush FreeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x61, 0xA6, 0x6A, 0x00)));
    private static readonly Brush FreeStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xA6, 0x6A, 0x00)));
    private static readonly Brush EmptyFill = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TakenFill = Freeze(new SolidColorBrush(Color.FromArgb(0x61, 0x37, 0x37, 0x30)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private readonly Rectangle[] _cells = new Rectangle[30];
    private int _drawnArea = -1, _drawnHalf = -1, _drawnSlot = -1, _hit;
    // 高亮属于哪个小区：只记房号的话，翻到别的小区时那边的同号地也会被点亮
    private int _hitArea = -1, _hitSlot = -1;

    public event Action? RequestClose;

    /// <summary>某小区的空置情况：能去抽的房号 / 空着但还不能抽的房号（公示期、准备期）</summary>
    public record WardPlots(HashSet<int> Buyable, HashSet<int> Empty);

    /// <summary>
    /// 取某小区的空置情况。售楼中心只给没人住的房，没出现的就是已经有人住了；
    /// 返回 null 表示没有数据（还没加载/该服无上报），这时不做判断。
    /// </summary>
    public Func<int, int, WardPlots?>? OnSaleLookup;

    public PlotMapPanel()
    {
        InitializeComponent();
        AreaBox.ItemsSource = GameData.AreaNames;
        SlotBox.ItemsSource = Enumerable.Range(1, 30).Select(i => $"{i}区").ToArray();
        HalfBox.ItemsSource = new[] { "主城区 1-30 号", "扩建区 31-60 号" };
        AreaBox.SelectedIndex = 0;
        SlotBox.SelectedIndex = 0;
        HalfBox.SelectedIndex = 0;
    }

    /// <summary>切到这套房所在的图，并只高亮它</summary>
    public void Highlight(int area, int slot, int plotId)
    {
        if (area < 0 || area >= GameData.AreaNames.Length || plotId < 1 || plotId > 60) return;
        _hit = plotId;
        _hitArea = area;
        _hitSlot = slot;
        AreaBox.SelectedIndex = area;
        if (slot >= 0 && slot < 30) SlotBox.SelectedIndex = slot;
        HalfBox.SelectedIndex = plotId > 30 ? 1 : 0;
        Draw();
    }

    /// <summary>在售数据变了，重画一次（空置标记跟着更新）</summary>
    public void Redraw()
    {
        _drawnArea = -1;
        Draw();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => Draw();

    /// <summary>手动切到某个小区（等同于用户操作那三个下拉）</summary>
    public void SetWard(int area, int slot, int half)
    {
        AreaBox.SelectedIndex = area;
        SlotBox.SelectedIndex = slot;
        HalfBox.SelectedIndex = half;
        Draw();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void Draw()
    {
        int area = AreaBox.SelectedIndex, half = HalfBox.SelectedIndex, slot = SlotBox.SelectedIndex;
        if (area < 0 || half < 0) return;

        if (area != _drawnArea || half != _drawnHalf || slot != _drawnSlot)
        {
            _drawnArea = area;
            _drawnHalf = half;
            _drawnSlot = slot;
            Build(area, half, Math.Max(slot, 0));
        }

        var sameWard = area == _hitArea && slot == _hitSlot;
        for (var i = 0; i < 30; i++)
        {
            var isHit = sameWard && half * 30 + i + 1 == _hit;
            if (isHit)
            {
                _cells[i].Fill = HitFill;
                _cells[i].Stroke = HitStroke;
                _cells[i].StrokeThickness = 4;
                _cells[i].StrokeDashArray = null;
            }
            else
            {
                // 恢复成空置底色（Build 时算好的，存在 Tag 里）
                var back = _cells[i].Tag as Brush;
                var isLand = ReferenceEquals(back, FreeFill) || ReferenceEquals(back, EmptyFill);
                _cells[i].Fill = back ?? Brushes.Transparent;
                _cells[i].Stroke = isLand ? FreeStroke : CellStroke;
                _cells[i].StrokeThickness = 1;
                _cells[i].StrokeDashArray = ReferenceEquals(back, FreeFill) ? null : new DoubleCollection { 4, 4 };
            }
        }
    }

    private void Build(int area, int half, int slot)
    {
        var ward = HousingMap.Ward(area, half);
        var occ = OnSaleLookup?.Invoke(area, slot);
        // 十张图共用同一个正方形取景框：各房区跨度 201~367、宽高比 0.58~1.73，
        // 各裁各的会让切换房区时缩放忽大忽小（网页端还会顶得下面的列表乱跳）
        var cx = (ward.Min(p => p.X) + ward.Max(p => p.X)) / 2.0;
        var cz = (ward.Min(p => p.Z) + ward.Max(p => p.Z)) / 2.0;
        double x0 = cx - HousingMap.ViewSpan / 2, z0 = cz - HousingMap.ViewSpan / 2;

        MapCanvas.Children.Clear();
        MapCanvas.Width = HousingMap.ViewSpan;
        MapCanvas.Height = HousingMap.ViewSpan;

        // 底图：主城区直接平移；扩建区顺时针转 90° 再平移（见 HousingMap 注释）
        const double halfSpan = HousingMap.MapSpan / 2, shift = HousingMap.SubdivisionShift;
        MapCanvas.Children.Add(new Image
        {
            // 带程序集名的 pack URI：不带的话解析的是入口程序集，被别的宿主引用时会找不到图
            Source = new BitmapImage(new Uri(
                $"pack://application:,,,/FF14HouseReminder;component/Resources/maps/{area}-0.jpg")),
            Width = HousingMap.MapSpan,
            Height = HousingMap.MapSpan,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            RenderTransform = half == 0
                ? new MatrixTransform(1, 0, 0, 1, -halfSpan - x0, -halfSpan - z0)
                : new MatrixTransform(0, 1, -1, 0, halfSpan + shift - x0, -halfSpan + shift - z0)
        });

        for (var i = 0; i < ward.Length; i++)
        {
            var (x, z, w) = ward[i];
            var no = half * 30 + i + 1;

            var isFree = occ?.Buyable.Contains(no) ?? false;
            var isEmpty = occ?.Empty.Contains(no) ?? false;
            var cell = new Rectangle
            {
                Width = w * 2,
                Height = w * 2,
                Fill = occ == null ? Brushes.Transparent
                     : isFree ? FreeFill : isEmpty ? EmptyFill : TakenFill,
                ToolTip = $"{no} 号 [{HousingMap.SizeOf(w)}]"
                          + (occ == null ? "" : isFree ? " · 可抽" : isEmpty ? " · 空置（未开放）" : " · 已住人")
            };
            cell.MouseEnter += (_, _) => { _hit = no; Draw(); };
            Canvas.SetLeft(cell, x - w - x0);
            Canvas.SetTop(cell, z - w - z0);
            cell.Tag = cell.Fill;   // 记住空置底色，取消高亮时恢复
            MapCanvas.Children.Add(cell);
            _cells[i] = cell;

            var label = new TextBlock
            {
                Text = no.ToString(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Background = LabelBg,   // WPF 没有描边文字，垫一层半透明白底保证压在地图上也看得清
                Padding = new Thickness(1, 0, 1, 0),
                IsHitTestVisible = false
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - x0 - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, z - z0 - label.DesiredSize.Height / 2);
            MapCanvas.Children.Add(label);
        }

    }
}
