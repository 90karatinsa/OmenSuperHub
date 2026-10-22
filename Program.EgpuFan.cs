// eGPU（外接显卡）风扇：通过 NVAPI 直接设置外接 NVIDIA 卡的风扇转速（与 MSI Afterburner 同一接口）。
// 笔记本自身独显的风扇由 HP EC 控制，NVAPI 对其返回 NVAPI_NOT_SUPPORTED，因此这里只作用于非内置 GPU。
// OSH 默认不碰 eGPU 风扇；只有用户在菜单里选了百分比或风扇曲线后才接管，退出时交还给驱动自动控制。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace OmenSuperHub {
  static partial class Program {
    // -1 = 不接管（驱动自动曲线）；-2 = 按用户的风扇曲线；0..100 = 手动百分比（实际下限取显卡上报的 DefaultMinimumLevel）
    const int EgpuFanAutoMode = -1;
    const int EgpuFanCurveMode = -2;
    static int egpuFanPercent = EgpuFanAutoMode;
    // 风扇曲线（X = 温度 °C，Y = 风扇 %）。降温时按“温度 + 滞后”取值，避免风扇在两档之间来回跳
    static readonly List<Point> EgpuFanDefaultCurve = new List<Point> {
      new Point(30, 30), new Point(50, 40), new Point(60, 50), new Point(70, 65), new Point(78, 80), new Point(85, 100)
    };
    static List<Point> egpuFanCurve = new List<Point>(EgpuFanDefaultCurve);
    const int EgpuFanCurveHysteresis = 3;
    static int egpuCurveApplied = -1;           // 曲线模式下最近一次写入的档位（-1 = 尚未写入）
    static volatile int egpuTempC = 0;          // eGPU 核心温度（NVAPI，每 2 秒更新）
    static ToolStripMenuItem egpuFanCurveItem;
    static SimpleFanCurveForm egpuFanCurveForm;
    static volatile bool egpuFanAvailable = false;
    static PhysicalGPU egpuFanGpu;
    static string egpuFanGpuName = "eGPU";
    static int egpuFanMinPercent = 30;
    static volatile int egpuFanRpm = 0, egpuFanLevel = 0;
    static volatile bool egpuFanManual = false;
    static bool egpuFanGuardActive = false;
    static bool egpuFanDetecting = false;
    static int egpuFanApplySeq = 0;
    static DateTime egpuFanLastError = DateTime.MinValue;
    static System.Threading.Timer egpuFanTimer;
    static readonly object egpuFanLock = new object();
    static ToolStripMenuItem egpuFanMenu, egpuFanStatusItem, egpuFanValueLabel;
    static ToolStripSeparator egpuFanSeparator;
    static ToolStripTrackBar egpuFanTrackBar;

    // 手动转速 < 100% 而 eGPU 过热时临时拉满，降到释放温度后恢复用户设置
    const int EgpuFanGuardTemp = 85;
    const int EgpuFanGuardReleaseTemp = 72;

    static int ReadEgpuFanPercent() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          object raw = key?.GetValue("EgpuFanPercent");
          if (raw != null) return Math.Max(EgpuFanCurveMode, Math.Min(100, Convert.ToInt32(raw)));
        }
      } catch (Exception ex) {
        Logger.Error($"EgpuFanPercent okunamadi: {ex.Message}");
      }
      return EgpuFanAutoMode;
    }

    /// <summary>注册表 EgpuFanCurve："温度:百分比,温度:百分比,…"，温度严格递增；无效时用默认曲线。</summary>
    static List<Point> ReadEgpuFanCurve() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          string raw = key?.GetValue("EgpuFanCurve") as string;
          if (!string.IsNullOrWhiteSpace(raw)) {
            var points = new List<Point>();
            foreach (string pair in raw.Split(',')) {
              string[] p = pair.Split(':');
              if (p.Length != 2 || !int.TryParse(p[0].Trim(), out int t) || !int.TryParse(p[1].Trim(), out int v)) return new List<Point>(EgpuFanDefaultCurve);
              points.Add(new Point(t, Math.Max(0, Math.Min(100, v))));
            }
            bool increasing = true;
            for (int i = 1; i < points.Count; i++) if (points[i].X <= points[i - 1].X) increasing = false;
            if (points.Count >= 2 && increasing) return points;
            Logger.Error($"EgpuFanCurve gecersiz, varsayilan kullaniliyor: {raw}");
          }
        }
      } catch (Exception ex) {
        Logger.Error($"EgpuFanCurve okunamadi: {ex.Message}");
      }
      return new List<Point>(EgpuFanDefaultCurve);
    }

    static string FormatEgpuFanCurve(IEnumerable<Point> points) => string.Join(",", points.Select(p => $"{p.X}:{p.Y}"));

    static void SaveEgpuFanCurve() {
      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub"))
          key.SetValue("EgpuFanCurve", FormatEgpuFanCurve(egpuFanCurve));
      } catch (Exception ex) {
        Logger.Error($"EgpuFanCurve kaydedilemedi: {ex.Message}");
      }
    }

    static int ReadEgpuTemp(PhysicalGPU gpu) {
      try {
        var sensor = gpu.ThermalInformation.ThermalSensors.FirstOrDefault();
        return sensor != null ? sensor.CurrentTemperature : 0;
      } catch {
        return 0;
      }
    }

    /// <summary>把所有风扇设为同一档位。调用方持有 egpuFanLock。</summary>
    static void SetEgpuCoolers(PhysicalGPU gpu, int level) {
      GPUCoolerInformation info = gpu.CoolerInformation;
      level = Math.Max(egpuFanMinPercent, Math.Min(100, level));
      foreach (GPUCooler c in info.Coolers.ToList())
        info.SetCoolerSettings(c.CoolerId, CoolerPolicy.Manual, level);
    }

    /// <summary>曲线模式：按当前温度计算目标档位（含滞后与 85°C 安全上限），与上次写入不同时才写入。调用方持有 egpuFanLock。</summary>
    static void ApplyEgpuFanCurveStep(PhysicalGPU gpu, string reason) {
      int t = egpuTempC;
      if (t <= 0) return;
      int up = SimpleFanCurveForm.Evaluate(egpuFanCurve, t, egpuFanMinPercent, 100);
      int down = SimpleFanCurveForm.Evaluate(egpuFanCurve, t + EgpuFanCurveHysteresis, egpuFanMinPercent, 100);
      if (t >= EgpuFanGuardTemp) up = down = 100;
      int target = egpuCurveApplied;
      if (egpuCurveApplied < 0 || up > egpuCurveApplied) target = up;
      else if (down < egpuCurveApplied) target = down;
      if (target == egpuCurveApplied) return;
      SetEgpuCoolers(gpu, target);
      if (egpuCurveApplied < 0 || reason != null)
        Logger.Info($"eGPU fan egrisi ({reason ?? "sicaklik"}): {t}C -> %{target}");
      egpuCurveApplied = target;
    }

    static void SaveEgpuFanPercent() {
      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub"))
          key.SetValue("EgpuFanPercent", egpuFanPercent);
      } catch (Exception ex) {
        Logger.Error($"EgpuFanPercent kaydedilemedi: {ex.Message}");
      }
    }

    static string GpuName(PhysicalGPU g) {
      try { return g.FullName; } catch { return "GPU"; }
    }

    static string DescribeEgpuFanSetting() =>
      egpuFanPercent == EgpuFanAutoMode ? "otomatik"
      : egpuFanPercent == EgpuFanCurveMode ? $"egri ({FormatEgpuFanCurve(egpuFanCurve)})"
      : $"%{egpuFanPercent}";

    /// <summary>在非内置 GPU 中找出第一块能通过 NVAPI 读到风扇的显卡。调用方持有 egpuFanLock。</summary>
    static PhysicalGPU FindEgpuWithFanControl(out int minPercent, out int fanCount) {
      minPercent = 30;
      fanCount = 0;
      NVIDIA.Initialize();
      PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
      if (gpus == null || gpus.Length < 2) return null;
      PhysicalGPU internalGpu = GpuAppManager.PickInternalGpu(gpus);
      foreach (PhysicalGPU g in gpus) {
        if (g.Equals(internalGpu)) continue;
        try {
          var coolers = g.CoolerInformation.Coolers.ToList();
          if (coolers.Count == 0) continue;
          minPercent = Math.Max(0, Math.Min(100, coolers.Min(c => c.DefaultMinimumLevel)));
          fanCount = coolers.Count;
          return g;
        } catch (Exception ex) {
          Logger.Info($"eGPU fan: {GpuName(g)} fan API'si yok ({ex.Message})");
        }
      }
      return null;
    }

    /// <summary>启动时调用：读取保存的设置并在后台检测 eGPU。</summary>
    static void InitEgpuFan() {
      egpuFanPercent = ReadEgpuFanPercent();
      egpuFanCurve = ReadEgpuFanCurve();
      DetectEgpuFan("baslangic");
    }

    /// <summary>后台检测可控风扇的 eGPU；找到后应用已保存的手动设置并启动状态轮询。</summary>
    static void DetectEgpuFan(string reason) {
      lock (egpuFanLock) {
        if (egpuFanDetecting) return;
        egpuFanDetecting = true;
      }
      Task.Run(() => {
        try {
          lock (egpuFanLock) {
            PhysicalGPU gpu = FindEgpuWithFanControl(out int min, out int count);
            if (gpu == null) {
              egpuFanAvailable = false;
              egpuFanGpu = null;
              Logger.Info($"eGPU fan ({reason}): NVAPI ile fani okunabilen harici GPU yok");
              return;
            }
            egpuFanGpu = gpu;
            egpuFanGpuName = GpuName(gpu);
            egpuFanMinPercent = min;
            egpuFanAvailable = true;
            Logger.Info($"eGPU fan ({reason}): {egpuFanGpuName}, {count} fan, alt sinir %{min}, kayitli ayar: {DescribeEgpuFanSetting()}");
            if (egpuFanPercent != EgpuFanAutoMode) ApplyEgpuFanCore(reason);
          }
          if (egpuFanTimer == null)
            egpuFanTimer = new System.Threading.Timer(_ => PollEgpuFan(), null, 0, 2000);
        } catch (Exception ex) {
          Logger.Error($"eGPU fan algilama ({reason}): {ex.Message}");
        } finally {
          lock (egpuFanLock) egpuFanDetecting = false;
        }
      });
    }

    /// <summary>把当前 egpuFanPercent 写入显卡。调用方持有 egpuFanLock；失败时抛出异常。</summary>
    static void ApplyEgpuFanCore(string reason) {
      PhysicalGPU gpu = egpuFanGpu;
      if (gpu == null) throw new InvalidOperationException("eGPU bulunamadi");
      NVIDIA.Initialize();
      egpuFanGuardActive = false;
      egpuCurveApplied = -1;
      if (egpuFanPercent == EgpuFanAutoMode) {
        gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
      } else if (egpuFanPercent == EgpuFanCurveMode) {
        int t = ReadEgpuTemp(gpu);
        if (t > 0) egpuTempC = t;
        ApplyEgpuFanCurveStep(gpu, reason);
      } else {
        SetEgpuCoolers(gpu, egpuFanPercent);
      }
      Logger.Info($"eGPU fan uygulandi ({reason}): {DescribeEgpuFanSetting()}");
    }

    /// <summary>菜单入口：保存设置并在后台写入显卡。拖动滑块产生的连续变化会被合并。</summary>
    static void SetEgpuFanPercent(int percent) {
      egpuFanPercent = percent;
      SaveEgpuFanPercent();
      int seq = Interlocked.Increment(ref egpuFanApplySeq);
      Task.Run(() => {
        Thread.Sleep(150);
        if (seq != Volatile.Read(ref egpuFanApplySeq)) return;
        lock (egpuFanLock) {
          try {
            if (egpuFanGpu == null) egpuFanGpu = FindEgpuWithFanControl(out egpuFanMinPercent, out _);
            ApplyEgpuFanCore("menu");
          } catch (Exception ex) {
            egpuFanGpu = null;
            Logger.Error($"eGPU fan ayari uygulanamadi: {ex.Message}");
            ShowBalloon(Strings.EgpuFanMenu, Strings.EgpuFanApplyFailed(ex.Message), ToolTipIcon.Warning);
          }
        }
      });
    }

    /// <summary>每 2 秒读取转速/档位，并执行过热保护。</summary>
    static void PollEgpuFan() {
      if (!egpuFanAvailable || !Monitor.TryEnter(egpuFanLock)) return;
      try {
        PhysicalGPU gpu = egpuFanGpu;
        if (gpu == null) return;
        var coolers = gpu.CoolerInformation.Coolers.ToList();
        if (coolers.Count == 0) return;
        egpuFanRpm = coolers.Max(c => c.CurrentFanSpeedInRPM);
        egpuFanLevel = coolers.Max(c => c.CurrentLevel);
        egpuFanManual = coolers.Any(c => c.CurrentPolicy == CoolerPolicy.Manual);
        int temp = ReadEgpuTemp(gpu);
        if (temp > 0) egpuTempC = temp;

        if (egpuFanPercent == EgpuFanCurveMode) {
          ApplyEgpuFanCurveStep(gpu, null);
        } else if (egpuFanPercent >= 0 && egpuFanPercent < 100 && (egpuTempC > 0 || hasEGPU)) {
          float t = egpuTempC > 0 ? egpuTempC : rawTempEGPU;
          if (!egpuFanGuardActive && t >= EgpuFanGuardTemp) {
            GPUCoolerInformation info = gpu.CoolerInformation;
            foreach (GPUCooler c in coolers)
              info.SetCoolerSettings(c.CoolerId, CoolerPolicy.Manual, 100);
            egpuFanGuardActive = true;
            Logger.Info($"eGPU fan korumasi: {t:F0}C, fan gecici olarak %100");
            ShowBalloon(Strings.EgpuFanGuardTitle, Strings.EgpuFanGuardText(t), ToolTipIcon.Warning);
          } else if (egpuFanGuardActive && t <= EgpuFanGuardReleaseTemp) {
            ApplyEgpuFanCore("koruma bitti");
          }
        }
      } catch (Exception ex) {
        // 驱动重启或 eGPU 掉线后句柄失效：标记不可用，等待下一次检测（eGPU 重新出现或打开菜单时）
        egpuFanRpm = 0;
        egpuFanGpu = null;
        egpuFanAvailable = false;
        if ((DateTime.Now - egpuFanLastError).TotalMinutes >= 5) {
          egpuFanLastError = DateTime.Now;
          Logger.Error($"eGPU fan okunamadi: {ex.Message}");
        }
      } finally {
        Monitor.Exit(egpuFanLock);
      }
    }

    /// <summary>退出时把 eGPU 风扇交还给驱动，避免 OSH 不在时风扇卡在手动低转速。</summary>
    static void RestoreEgpuFanOnExit() {
      try { egpuFanTimer?.Dispose(); } catch { }
      if (!egpuFanAvailable || egpuFanPercent == EgpuFanAutoMode) return;
      lock (egpuFanLock) {
        try {
          PhysicalGPU gpu = egpuFanGpu;
          if (gpu == null) return;
          NVIDIA.Initialize();
          gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
          Logger.Info("eGPU fan: cikista surucu otomatigine dondu");
        } catch (Exception ex) {
          Logger.Error($"eGPU fan cikista geri alinamadi: {ex.Message}");
        }
      }
    }

    // ── 菜单 ────────────────────────────────────────────────────────────────

    static ToolStripMenuItem BuildEgpuFanMenu() {
      // 菜单在 InitEgpuFan 之前构建，先读取已保存的设置，否则初始勾选和滑块位置不对
      egpuFanPercent = ReadEgpuFanPercent();
      egpuFanCurve = ReadEgpuFanCurve();
      egpuFanSeparator = new ToolStripSeparator { Visible = false };
      egpuFanMenu = new ToolStripMenuItem(Strings.EgpuFanMenu) { Visible = false };
      egpuFanStatusItem = new ToolStripMenuItem(Strings.EgpuFanUnavailable) { Enabled = false };
      egpuFanMenu.DropDownItems.Add(egpuFanStatusItem);
      egpuFanMenu.DropDownItems.Add(new ToolStripSeparator());
      egpuFanMenu.DropDownItems.Add(CreateMenuItem(Strings.EgpuFanAuto, "egpuFanGroup", (s, e) => SetEgpuFanPercent(EgpuFanAutoMode), egpuFanPercent == EgpuFanAutoMode, Strings.EgpuFanAutoTip));
      egpuFanMenu.DropDownItems.Add(CreateMenuItem(Strings.EgpuFanMax, "egpuFanGroup", (s, e) => SetEgpuFanPercent(100), egpuFanPercent == 100));
      // 曲线项不用 CreateMenuItem：点击只打开编辑器，在编辑器里“应用”后才切换到曲线模式并打勾
      egpuFanCurveItem = new ToolStripMenuItem(Strings.FanCurveSimpleMenu) { Tag = "egpuFanGroup", Checked = egpuFanPercent == EgpuFanCurveMode };
      egpuFanCurveItem.Click += (s, e) => OpenEgpuFanCurveEditor();
      egpuFanMenu.DropDownItems.Add(egpuFanCurveItem);
      egpuFanMenu.DropDownItems.Add(CreateMenuItem(Strings.EgpuFanSlider, "egpuFanGroup", (s, e) => { }, egpuFanPercent >= 0 && egpuFanPercent < 100));

      egpuFanTrackBar = new ToolStripTrackBar();
      egpuFanTrackBar.Minimum = 30;
      egpuFanTrackBar.Maximum = 100;
      egpuFanTrackBar.TickFrequency = 10;
      egpuFanTrackBar.Width = 800;
      egpuFanTrackBar.Value = egpuFanPercent >= 30 ? egpuFanPercent : 60;
      egpuFanValueLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"%{egpuFanTrackBar.Value}")) { Enabled = false };
      egpuFanTrackBar.ValueChanged += (s, e) => {
        egpuFanValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"%{egpuFanTrackBar.Value}");
        SetEgpuFanPercent(egpuFanTrackBar.Value);
        UpdateCheckedState("egpuFanGroup", Strings.EgpuFanSlider);
      };
      egpuFanMenu.DropDownItems.Add(egpuFanTrackBar);
      egpuFanMenu.DropDownItems.Add(egpuFanValueLabel);
      egpuFanMenu.DropDownOpening += (s, e) => RefreshEgpuFanStatus();
      return egpuFanMenu;
    }

    /// <summary>风扇控制菜单展开时：只有检测到可控 eGPU 时才显示子菜单。</summary>
    static void RefreshEgpuFanMenu() {
      if (egpuFanMenu == null) return;
      if (!egpuFanAvailable && hasEGPU) DetectEgpuFan("menu");
      egpuFanMenu.Visible = egpuFanAvailable;
      egpuFanSeparator.Visible = egpuFanAvailable;
      if (egpuFanAvailable && egpuFanRpm > 0)
        egpuFanMenu.Text = $"{Strings.EgpuFanMenu} ({egpuFanRpm} RPM)";
      else
        egpuFanMenu.Text = Strings.EgpuFanMenu;
    }

    static void RefreshEgpuFanStatus() {
      if (egpuFanStatusItem == null) return;
      string mode = egpuFanPercent == EgpuFanCurveMode ? Strings.EgpuFanModeCurve
        : egpuFanManual ? Strings.EgpuFanModeManual : Strings.EgpuFanModeAuto;
      egpuFanStatusItem.Text = egpuFanAvailable
        ? Strings.EgpuFanStatus(egpuFanGpuName, egpuTempC, egpuFanRpm, egpuFanLevel, mode)
        : Strings.EgpuFanUnavailable;
    }

    /// <summary>打开（或激活已打开的）风扇曲线编辑器。</summary>
    static void OpenEgpuFanCurveEditor() {
      if (egpuFanCurveForm != null && !egpuFanCurveForm.IsDisposed) {
        egpuFanCurveForm.Activate();
        return;
      }
      egpuFanCurveForm = new SimpleFanCurveForm(new SimpleFanCurveOptions {
        Title = $"{Strings.EgpuFanCurveTitle} — {egpuFanGpuName}",
        Hint = Strings.EgpuFanCurveHint,
        ValueAxisTitle = Strings.EgpuFanCurveFanAxis,
        ValueMin = egpuFanMinPercent,
        ValueMax = 100,
        ValueInterval = 10,
        FormatValue = v => Strings.Current == AppLanguage.Turkish ? $"%{v}" : $"{v}%",
        Points = egpuFanCurve,
        Defaults = EgpuFanDefaultCurve,
        Live = () => new SimpleFanCurveForm.LiveState {
          Available = egpuFanAvailable,
          Temperature = egpuTempC,
          Value = egpuFanLevel,
          Text = Strings.EgpuFanCurveLiveNow(egpuTempC, egpuFanLevel, egpuFanRpm)
        },
        Apply = ApplyEgpuFanCurveFromEditor
      });
      egpuFanCurveForm.FormClosed += (s, e) => egpuFanCurveForm = null;
      egpuFanCurveForm.Show();
    }

    /// <summary>编辑器点击“应用”：按用户自己调的曲线运行，不再属于任何风扇档位。</summary>
    static void ApplyEgpuFanCurveFromEditor(List<Point> points) {
      ApplyEgpuFanCurveCore(points);
      SaveFanProfile("");
    }

    /// <summary>保存曲线并切换到曲线模式；eGPU 不在时只保存，等它出现时再应用。</summary>
    static void ApplyEgpuFanCurveCore(List<Point> points) {
      egpuFanCurve = points;
      SaveEgpuFanCurve();
      Logger.Info($"eGPU fan egrisi kaydedildi: {FormatEgpuFanCurve(points)}");
      if (egpuFanAvailable) {
        SetEgpuFanPercent(EgpuFanCurveMode);
      } else {
        egpuFanPercent = EgpuFanCurveMode;
        SaveEgpuFanPercent();
      }
      if (egpuFanCurveItem != null) UpdateCheckedState("egpuFanGroup", null, egpuFanCurveItem);
    }
  }
}
