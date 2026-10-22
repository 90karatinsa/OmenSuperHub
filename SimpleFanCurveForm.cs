using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace OmenSuperHub {
  /// <summary>简单风扇曲线编辑器的参数：纵轴单位（% 或 RPM）、范围、实时数据和“应用”回调由调用方决定。</summary>
  internal sealed class SimpleFanCurveOptions {
    public string Title;
    public string Hint;
    public string ValueAxisTitle;
    public int ValueMin;          // 纵轴可设置的最小值（下方区域灰色显示）
    public int ValueMax;          // 纵轴最大值
    public int ValueInterval;     // 纵轴刻度
    public int ValueSnap = 1;     // 拖动时取整的步长（RPM 用 100）
    public Func<int, string> FormatValue;   // 60 → "%60"，4500 → "4500 RPM"
    public List<Point> Points;
    public List<Point> Defaults;
    public Func<SimpleFanCurveForm.LiveState> Live;
    public Action<List<Point>> Apply;
  }

  /// <summary>
  /// 简单风扇曲线编辑器（类似 MSI Afterburner）：拖动固定数量的点（横轴温度 °C，纵轴风扇 % 或 RPM），
  /// 图上实时显示当前温度和风扇。“应用”立即生效并保存，窗口保持打开以便继续微调。
  /// eGPU 风扇和笔记本风扇共用这个窗口。
  /// </summary>
  internal sealed class SimpleFanCurveForm : Form {
    internal struct LiveState {
      public bool Available;
      public int Temperature;   // 红点横坐标
      public int Value;         // 红点纵坐标（与曲线同单位）
      public string Text;       // 状态栏文字（不含“曲线目标”部分）
    }

    internal const int TemperatureMin = 20;
    internal const int TemperatureMax = 100;
    private const int PointHitRadius = 14;

    private static readonly Color PageBackColor = Color.FromArgb(245, 247, 250);
    private static readonly Color CardBackColor = Color.White;
    private static readonly Color BorderColor = Color.FromArgb(220, 225, 230);
    private static readonly Color AccentColor = Color.FromArgb(0, 122, 204);
    private static readonly Color LiveColor = Color.FromArgb(220, 53, 69);
    private static readonly Color GridColor = Color.FromArgb(232, 236, 240);
    private static readonly Color AxisLineColor = Color.FromArgb(200, 205, 210);
    private static readonly Color FloorColor = Color.FromArgb(240, 240, 240);
    private static readonly Color TextColor = Color.FromArgb(70, 70, 70);

    private readonly SimpleFanCurveOptions options;
    private readonly Chart chart;
    private readonly Series curveSeries;
    private readonly Series liveSeries;
    private readonly StripLine temperatureLine;
    private readonly Label statusLabel;
    private readonly Timer liveTimer;
    private readonly ToolTip pointToolTip;
    private DataPoint draggingPoint;
    private string appliedNote;

    internal SimpleFanCurveForm(SimpleFanCurveOptions options) {
      this.options = options;

      Text = options.Title;
      Icon = Properties.Resources.fan;
      StartPosition = FormStartPosition.CenterScreen;
      AutoScaleMode = AutoScaleMode.Dpi;
      BackColor = PageBackColor;
      Size = new Size(980, 620);
      MinimumSize = new Size(720, 460);

      pointToolTip = new ToolTip { AutomaticDelay = 0, InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 2000, ShowAlways = true };

      var root = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 3,
        Padding = new Padding(16),
        BackColor = PageBackColor
      };
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

      root.Controls.Add(new Label {
        AutoSize = true,
        MaximumSize = new Size(940, 0),
        Text = options.Hint,
        ForeColor = TextColor,
        Font = new Font(Font.FontFamily, 10.5F),
        Margin = new Padding(4, 0, 4, 10)
      }, 0, 0);

      chart = BuildChart(out curveSeries, out liveSeries, out temperatureLine);
      root.Controls.Add(chart, 0, 1);

      var bottom = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        AutoSize = true,
        ColumnCount = 2,
        RowCount = 1,
        BackColor = PageBackColor,
        Margin = new Padding(0, 10, 0, 0)
      };
      bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
      bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

      statusLabel = new Label {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = TextColor,
        Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
        Padding = new Padding(4, 0, 0, 0)
      };
      bottom.Controls.Add(statusLabel, 0, 0);

      var buttons = new FlowLayoutPanel {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        BackColor = PageBackColor,
        Margin = new Padding(0)
      };
      Button defaultButton = CreateButton(Strings.FanCurveDefaultButton, false);
      Button applyButton = CreateButton(Strings.FanCurveApplyButton, true);
      Button closeButton = CreateButton(Strings.FanCurveCloseButton, false);
      buttons.Controls.Add(defaultButton);
      buttons.Controls.Add(applyButton);
      buttons.Controls.Add(closeButton);
      bottom.Controls.Add(buttons, 1, 0);
      root.Controls.Add(bottom, 0, 2);

      Controls.Add(root);
      AcceptButton = applyButton;
      CancelButton = closeButton;

      defaultButton.Click += (s, e) => { SetPoints(options.Defaults); appliedNote = null; UpdateLive(); };
      applyButton.Click += (s, e) => {
        options.Apply(GetPoints());
        appliedNote = Strings.FanCurveAppliedNote;
        UpdateLive();
      };
      closeButton.Click += (s, e) => Close();

      SetPoints(options.Points);

      liveTimer = new Timer { Interval = 1000 };
      liveTimer.Tick += (s, e) => UpdateLive();
      Shown += (s, e) => { UpdateLive(); liveTimer.Start(); };
      FormClosed += (s, e) => { liveTimer.Stop(); liveTimer.Dispose(); pointToolTip.Dispose(); };
    }

    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED，拖动时不闪烁
        return cp;
      }
    }

    /// <summary>曲线线性插值：低于第一个点取第一个点的值，高于最后一个点取最后一个点的值，结果限制在 [min, max]。</summary>
    internal static int Evaluate(IList<Point> points, double temperature, int min, int max) {
      if (points == null || points.Count == 0) return max;
      double value;
      if (temperature <= points[0].X) {
        value = points[0].Y;
      } else if (temperature >= points[points.Count - 1].X) {
        value = points[points.Count - 1].Y;
      } else {
        value = points[points.Count - 1].Y;
        for (int i = 1; i < points.Count; i++) {
          if (temperature <= points[i].X) {
            Point a = points[i - 1], b = points[i];
            value = a.Y + (b.Y - a.Y) * (temperature - a.X) / (double)(b.X - a.X);
            break;
          }
        }
      }
      return Math.Max(min, Math.Min(max, (int)Math.Round(value)));
    }

    private Chart BuildChart(out Series curve, out Series live, out StripLine tempLine) {
      var c = new Chart {
        Dock = DockStyle.Fill,
        BackColor = PageBackColor,
        Cursor = Cursors.Default,
        AntiAliasing = AntiAliasingStyles.All,
        TextAntiAliasingQuality = TextAntiAliasingQuality.High
      };
      var area = new ChartArea("Curve") { BackColor = CardBackColor };
      Font axisFont = new Font(Font.FontFamily, 11F, FontStyle.Bold);

      area.AxisX.Minimum = TemperatureMin;
      area.AxisX.Maximum = TemperatureMax;
      area.AxisX.Interval = 10;
      area.AxisX.Title = Strings.FanCurveTempAxisSimple;
      area.AxisX.TitleFont = axisFont;
      area.AxisX.TitleForeColor = TextColor;
      area.AxisX.LineColor = AxisLineColor;
      area.AxisX.MajorGrid.LineColor = GridColor;
      area.AxisX.LabelStyle.ForeColor = TextColor;

      area.AxisY.Minimum = 0;
      area.AxisY.Maximum = options.ValueMax;
      area.AxisY.Interval = options.ValueInterval;
      area.AxisY.Title = options.ValueAxisTitle;
      area.AxisY.TitleFont = axisFont;
      area.AxisY.TitleForeColor = TextColor;
      area.AxisY.LineColor = AxisLineColor;
      area.AxisY.MajorGrid.LineColor = GridColor;
      area.AxisY.LabelStyle.ForeColor = TextColor;

      // 设备不允许的低速区域（如 eGPU 风扇最低 30%）用浅灰色标出
      if (options.ValueMin > 0)
        area.AxisY.StripLines.Add(new StripLine { IntervalOffset = 0, StripWidth = options.ValueMin, BackColor = FloorColor });

      // 当前温度的竖线（没有读数时宽度为 0，不显示）
      tempLine = new StripLine {
        IntervalOffset = TemperatureMin,
        StripWidth = 0,
        BorderColor = LiveColor,
        BorderDashStyle = ChartDashStyle.Dash,
        BorderWidth = 0
      };
      area.AxisX.StripLines.Add(tempLine);
      c.ChartAreas.Add(area);

      curve = new Series("Curve") {
        ChartType = SeriesChartType.Line,
        BorderWidth = 3,
        Color = AccentColor,
        MarkerStyle = MarkerStyle.Circle,
        MarkerSize = 13,
        MarkerColor = AccentColor,
        MarkerBorderColor = CardBackColor,
        MarkerBorderWidth = 2
      };
      live = new Series("Live") {
        ChartType = SeriesChartType.Point,
        MarkerStyle = MarkerStyle.Circle,
        MarkerSize = 14,
        Color = LiveColor,
        MarkerBorderColor = CardBackColor,
        MarkerBorderWidth = 2
      };
      c.Series.Add(curve);
      c.Series.Add(live);

      c.MouseDown += Chart_MouseDown;
      c.MouseMove += Chart_MouseMove;
      c.MouseUp += (s, e) => { draggingPoint = null; chart.Cursor = Cursors.Default; };
      c.MouseLeave += (s, e) => pointToolTip.Hide(chart);
      return c;
    }

    private Button CreateButton(string text, bool primary) {
      var b = new Button {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(130, 44),
        Margin = new Padding(10, 0, 0, 0),
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? AccentColor : CardBackColor,
        ForeColor = primary ? Color.White : Color.FromArgb(40, 40, 40),
        Font = new Font(Font.FontFamily, 11F, primary ? FontStyle.Bold : FontStyle.Regular),
        Cursor = Cursors.Hand
      };
      b.FlatAppearance.BorderColor = primary ? AccentColor : BorderColor;
      b.FlatAppearance.BorderSize = 1;
      return b;
    }

    private int ClampValue(double v) {
      int snap = Math.Max(1, options.ValueSnap);
      int snapped = (int)(Math.Round(v / snap) * snap);
      return Math.Max(options.ValueMin, Math.Min(options.ValueMax, snapped));
    }

    private void SetPoints(IEnumerable<Point> points) {
      curveSeries.Points.Clear();
      foreach (Point p in points.OrderBy(p => p.X))
        curveSeries.Points.AddXY(Math.Max(TemperatureMin, Math.Min(TemperatureMax, p.X)), ClampValue(p.Y));
      chart.Invalidate();
    }

    private List<Point> GetPoints() {
      return curveSeries.Points
        .Select(p => new Point((int)Math.Round(p.XValue), (int)Math.Round(p.YValues[0])))
        .OrderBy(p => p.X)
        .ToList();
    }

    private void UpdateLive() {
      LiveState s = options.Live();
      liveSeries.Points.Clear();
      if (!s.Available || s.Temperature <= 0) {
        temperatureLine.BorderWidth = 0;
        statusLabel.Text = appliedNote ?? Strings.FanCurveLiveNone;
        chart.Invalidate();
        return;
      }
      int t = Math.Max(TemperatureMin, Math.Min(TemperatureMax, s.Temperature));
      liveSeries.Points.AddXY(t, Math.Max(0, Math.Min(options.ValueMax, s.Value)));
      temperatureLine.IntervalOffset = t;
      temperatureLine.BorderWidth = 2;
      int target = Evaluate(GetPoints(), s.Temperature, options.ValueMin, options.ValueMax);
      string live = s.Text + Strings.FanCurveLiveTarget(options.FormatValue(target));
      statusLabel.Text = appliedNote != null ? $"{appliedNote}   {live}" : live;
      chart.Invalidate();
    }

    private DataPoint FindPoint(Point location) {
      ChartArea area = chart.ChartAreas[0];
      foreach (DataPoint p in curveSeries.Points) {
        double dx = area.AxisX.ValueToPixelPosition(p.XValue) - location.X;
        double dy = area.AxisY.ValueToPixelPosition(p.YValues[0]) - location.Y;
        if (dx * dx + dy * dy <= PointHitRadius * PointHitRadius) return p;
      }
      return null;
    }

    private bool TryGetValues(Point location, out double temperature, out double value) {
      temperature = value = 0;
      try {
        ChartArea area = chart.ChartAreas[0];
        temperature = area.AxisX.PixelPositionToValue(location.X);
        value = area.AxisY.PixelPositionToValue(location.Y);
        return true;
      } catch (Exception) {
        return false;   // 图表尚未完成布局时会抛出异常
      }
    }

    private string PointLabel(int temperature, int value) => $"{temperature}°C → {options.FormatValue(value)}";

    private void Chart_MouseDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      draggingPoint = FindPoint(e.Location);
      if (draggingPoint != null) chart.Cursor = Cursors.SizeAll;
    }

    private void Chart_MouseMove(object sender, MouseEventArgs e) {
      if (draggingPoint != null) {
        if (!TryGetValues(e.Location, out double temperature, out double value)) return;
        int index = curveSeries.Points.IndexOf(draggingPoint);
        // 横向只能在相邻两点之间移动，保证温度严格递增
        int minT = index == 0 ? TemperatureMin : (int)Math.Round(curveSeries.Points[index - 1].XValue) + 1;
        int maxT = index == curveSeries.Points.Count - 1 ? TemperatureMax : (int)Math.Round(curveSeries.Points[index + 1].XValue) - 1;
        int t = Math.Max(minT, Math.Min(maxT, (int)Math.Round(temperature)));
        int y = ClampValue(value);
        draggingPoint.XValue = t;
        draggingPoint.YValues[0] = y;
        appliedNote = null;
        pointToolTip.Show(PointLabel(t, y), chart, e.X + 16, e.Y - 30, 1500);
        chart.Invalidate();
        return;
      }
      DataPoint hover = FindPoint(e.Location);
      if (hover == null) {
        chart.Cursor = Cursors.Default;
        pointToolTip.Hide(chart);
        return;
      }
      chart.Cursor = Cursors.Hand;
      pointToolTip.Show(PointLabel((int)Math.Round(hover.XValue), (int)Math.Round(hover.YValues[0])), chart, e.X + 16, e.Y - 30, 1500);
    }
  }
}
