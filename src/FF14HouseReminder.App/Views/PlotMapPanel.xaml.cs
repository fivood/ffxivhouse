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

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private readonly Rectangle[] _cells = new Rectangle[30];
    private int _drawnArea = -1, _drawnHalf = -1, _hit;

    public event Action? RequestClose;

    public PlotMapPanel()
    {
        InitializeComponent();
        AreaBox.ItemsSource = GameData.AreaNames;
        HalfBox.ItemsSource = new[] { "主城区 1-30 号", "扩建区 31-60 号" };
        AreaBox.SelectedIndex = 0;
        HalfBox.SelectedIndex = 0;
    }

    /// <summary>切到这套房所在的图，并只高亮它</summary>
    public void Highlight(int area, int plotId)
    {
        if (area < 0 || area >= GameData.AreaNames.Length || plotId < 1 || plotId > 60) return;
        _hit = plotId;
        AreaBox.SelectedIndex = area;
        HalfBox.SelectedIndex = plotId > 30 ? 1 : 0;
        Draw();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => Draw();

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void Draw()
    {
        int area = AreaBox.SelectedIndex, half = HalfBox.SelectedIndex;
        if (area < 0 || half < 0) return;

        if (area != _drawnArea || half != _drawnHalf)
        {
            _drawnArea = area;
            _drawnHalf = half;
            Build(area, half);
        }

        for (var i = 0; i < 30; i++)
        {
            var isHit = half * 30 + i + 1 == _hit;
            _cells[i].Fill = isHit ? HitFill : Brushes.Transparent;
            _cells[i].Stroke = isHit ? HitStroke : CellStroke;
            _cells[i].StrokeThickness = isHit ? 4 : 1;
            _cells[i].StrokeDashArray = isHit ? null : new DoubleCollection { 4, 4 };
        }
    }

    private void Build(int area, int half)
    {
        var ward = HousingMap.Ward(area, half);
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

            var cell = new Rectangle
            {
                Width = w * 2,
                Height = w * 2,
                Fill = Brushes.Transparent,
                ToolTip = $"{no} 号 [{HousingMap.SizeOf(w)}]"
            };
            cell.MouseEnter += (_, _) => { _hit = no; Draw(); };
            Canvas.SetLeft(cell, x - w - x0);
            Canvas.SetTop(cell, z - w - z0);
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
