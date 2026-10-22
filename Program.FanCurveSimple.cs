// 笔记本风扇的简单曲线（类似 MSI Afterburner）：一条曲线，横轴为 CPU 和 GPU 中较热者的温度，纵轴为风扇转速（RPM）。
// 应用时写入 custom.txt（CPU、GPU 两张表相同，原有逻辑取两者结果的较大值，即按较热的芯片调速），
// 风扇表切到“自定义”、风扇控制切到“自动”，由原有的自动调速逻辑（温度平滑、高温保护等）执行。
// 右键“自定义”打开的原有双曲线编辑器保持不变。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  static partial class Program {
    static SimpleFanCurveForm laptopFanCurveForm;
    static ToolStripMenuItem laptopFanCurveItem;

    // ── 风扇档位（笔记本风扇与 eGPU 风扇共用）：一次选择同时设置两条曲线 ──────────
    // "" = 未使用档位（用户在图表里自己调过曲线，或从未选择过）
    static string fanProfile = ReadFanProfile();
    static readonly string[] FanProfileKeys = { "aggressive", "normal", "silent" };

    static string ReadFanProfile() {
      try {
        using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub"))
          return key?.GetValue("FanProfile") as string ?? "";
      } catch {
        return "";
      }
    }

    static void SaveFanProfile(string key) {
      fanProfile = key ?? "";
      try {
        using (Microsoft.Win32.RegistryKey reg = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub"))
          reg.SetValue("FanProfile", fanProfile);
      } catch (Exception ex) {
        Logger.Error($"FanProfile kaydedilemedi: {ex.Message}");
      }
    }

    static string FanProfileName(string key) =>
      key == "aggressive" ? Strings.FanProfileAggressive : key == "silent" ? Strings.FanProfileSilent : Strings.FanProfileNormal;

    static string FanProfileTip(string key) =>
      key == "aggressive" ? Strings.FanProfileAggressiveTip : key == "silent" ? Strings.FanProfileSilentTip : Strings.FanProfileNormalTip;

    /// <summary>笔记本风扇曲线（横轴为 CPU/GPU 中较热者的温度，纵轴 RPM）；最高点取风扇滑块上限，即硬件实际最高转速。</summary>
    static List<Point> LaptopProfileCurve(string key) {
      int max = GetFanSliderMaxRpm();
      int M(int rpm) => Math.Min(max, rpm);
      switch (key) {
        case "aggressive":
          return new List<Point> { new Point(40, M(3000)), new Point(50, M(4000)), new Point(60, M(5200)), new Point(70, M(6500)), new Point(78, M(7600)), new Point(85, max) };
        case "silent":
          return new List<Point> { new Point(50, M(1500)), new Point(62, M(2300)), new Point(72, M(3300)), new Point(82, M(4700)), new Point(90, M(6200)), new Point(96, max) };
        default:
          return new List<Point> { new Point(40, M(2000)), new Point(55, M(3000)), new Point(65, M(4000)), new Point(75, M(5500)), new Point(85, M(7000)), new Point(92, max) };
      }
    }

    /// <summary>eGPU 风扇曲线（%）。85°C 以上另有安全上限强制 100%。</summary>
    static List<Point> EgpuProfileCurve(string key) {
      switch (key) {
        case "aggressive":
          return new List<Point> { new Point(30, 40), new Point(45, 50), new Point(55, 65), new Point(65, 80), new Point(72, 95), new Point(78, 100) };
        case "silent":
          return new List<Point> { new Point(40, 30), new Point(55, 35), new Point(65, 45), new Point(73, 60), new Point(80, 80), new Point(85, 100) };
        default:
          return new List<Point>(EgpuFanDefaultCurve);
      }
    }

    static List<Point> DefaultLaptopFanCurve() => LaptopProfileCurve("normal");

    /// <summary>档位是否正在生效：笔记本按自定义表自动调速，且（有 eGPU 时）eGPU 也处于曲线模式。</summary>
    static bool IsFanProfileActive(string key) =>
      fanProfile == key && fanControl == "auto" && fanTable.Contains("custom")
      && (!egpuFanAvailable || egpuFanPercent == EgpuFanCurveMode);

    static ToolStripMenuItem fanProfileMenu;

    static ToolStripMenuItem BuildFanProfileMenu() {
      var menu = new ToolStripMenuItem(Strings.FanProfileMenu);
      fanProfileMenu = menu;
      foreach (string key in FanProfileKeys) {
        string k = key;
        menu.DropDownItems.Add(CreateMenuItem(FanProfileName(k), "fanProfileGroup", (s, e) => ApplyFanProfile(k), IsFanProfileActive(k), FanProfileTip(k)));
      }
      menu.DropDownOpening += (s, e) => RefreshFanProfileMenu(menu);
      RefreshFanProfileMenu(menu);
      return menu;
    }

    static void RefreshFanProfileMenu(ToolStripMenuItem menu) {
      string active = FanProfileKeys.FirstOrDefault(IsFanProfileActive);
      foreach (ToolStripMenuItem item in menu.DropDownItems.OfType<ToolStripMenuItem>())
        item.Checked = active != null && item.Text == FanProfileName(active);
      menu.Text = active != null ? $"{Strings.FanProfileMenu}: {FanProfileName(active)}" : Strings.FanProfileMenu;
    }

    /// <summary>选择档位：笔记本风扇与 eGPU 风扇同时切到该档位的曲线。</summary>
    static void ApplyFanProfile(string key) {
      ApplyLaptopFanCurveCore(LaptopProfileCurve(key));
      ApplyEgpuFanCurveCore(EgpuProfileCurve(key));
      SaveFanProfile(key);
      Logger.Info($"Fan profili: {key} (dizustu + eGPU)");
      ShowBalloon(Strings.FanProfileMenu, Strings.FanProfileAppliedText(FanProfileName(key), egpuFanAvailable), ToolTipIcon.Info);
    }

    /// <summary>已有 custom.txt 且点数足够时沿用其 CPU 曲线，否则用默认曲线（点太少不好调）。</summary>
    static List<Point> LoadLaptopFanCurve() {
      try {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.txt");
        if (File.Exists(path)) {
          FanCurveProfile profile = FanCurveProfile.Load(path);
          if (profile.CpuPoints.Count >= 4)
            return profile.CpuPoints.Select(p => new Point(p.Temperature, p.FanSpeed)).ToList();
        }
      } catch (Exception ex) {
        Logger.Error($"custom.txt okunamadi: {ex.Message}");
      }
      return DefaultLaptopFanCurve();
    }

    static ToolStripMenuItem BuildLaptopFanCurveItem() {
      laptopFanCurveItem = new ToolStripMenuItem(Strings.FanCurveSimpleMenu) {
        Checked = fanControl == "auto" && fanTable.Contains("custom")
      };
      laptopFanCurveItem.Click += (s, e) => OpenLaptopFanCurveEditor();
      return laptopFanCurveItem;
    }

    static void RefreshLaptopFanCurveItem() {
      if (laptopFanCurveItem != null) laptopFanCurveItem.Checked = fanControl == "auto" && fanTable.Contains("custom");
      if (fanProfileMenu != null) RefreshFanProfileMenu(fanProfileMenu);
    }

    static void OpenLaptopFanCurveEditor() {
      if (laptopFanCurveForm != null && !laptopFanCurveForm.IsDisposed) {
        if (laptopFanCurveForm.WindowState == FormWindowState.Minimized) laptopFanCurveForm.WindowState = FormWindowState.Normal;
        laptopFanCurveForm.Activate();
        return;
      }
      laptopFanCurveForm = new SimpleFanCurveForm(new SimpleFanCurveOptions {
        Title = Strings.LaptopFanCurveTitle,
        Hint = Strings.LaptopFanCurveHint,
        ValueAxisTitle = Strings.LaptopFanCurveAxis,
        ValueMin = 0,
        ValueMax = GetFanSliderMaxRpm(),
        ValueInterval = 1000,
        ValueSnap = 100,
        FormatValue = v => $"{v} RPM",
        Points = LoadLaptopFanCurve(),
        Defaults = DefaultLaptopFanCurve(),
        Live = GetLaptopFanLiveState,
        Apply = ApplyLaptopFanCurve
      });
      laptopFanCurveForm.FormClosed += (s, e) => laptopFanCurveForm = null;
      laptopFanCurveForm.Show();
    }

    /// <summary>与自动调速使用同一组数据：平滑后的 CPU/GPU 温度（仅已启用且已初始化的），风扇为 BIOS 报告的当前转速。</summary>
    static SimpleFanCurveForm.LiveState GetLaptopFanLiveState() {
      bool cpuOk = tempReady && monitorCPU && cpuTempReady;
      bool gpuOk = tempReady && monitorGPU && gpuTempReady;
      int cpuT = cpuOk ? (int)Math.Round(smoothedCPUTemp) : 0;
      int gpuT = gpuOk ? (int)Math.Round(smoothedGPUTemp) : 0;
      int f1, f2;
      lock (fanSpeedNow) { f1 = fanSpeedNow[0] * 100; f2 = fanSpeedNow[1] * 100; }
      return new SimpleFanCurveForm.LiveState {
        Available = cpuOk || gpuOk,
        Temperature = Math.Max(cpuT, gpuT),
        Value = Math.Max(f1, f2),
        Text = Strings.LaptopFanCurveLiveNow(cpuOk ? cpuT.ToString() : "--", gpuOk ? gpuT.ToString() : "--", f1, f2)
      };
    }

    /// <summary>图表编辑器的“应用”：按用户自己调的曲线运行，不再属于任何档位。</summary>
    static void ApplyLaptopFanCurve(List<Point> points) {
      ApplyLaptopFanCurveCore(points);
      SaveFanProfile("");
    }

    /// <summary>写入 custom.txt（CPU/GPU 同一条曲线），切到自定义风扇表 + 自动调速。</summary>
    static void ApplyLaptopFanCurveCore(List<Point> points) {
      try {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.txt");
        var cpu = points.Select(p => new FanCurvePoint(p.X, p.Y)).ToList();
        var gpu = points.Select(p => new FanCurvePoint(p.X, p.Y)).ToList();
        new FanCurveProfile(cpu, gpu).Save(path);

        fanTable = "custom";
        LoadFanConfig("custom.txt");
        SaveConfig("FanTable");
        UpdateCheckedState("fanTableGroup", Strings.FanCustomMode);

        fanControl = "auto";
        SetMaxFanSpeedOff();
        fanControlTimer.Change(0, 1000);
        SaveConfig("FanControl");
        UpdateCheckedState("fanControlGroup", Strings.FanAuto);
        RefreshLaptopFanCurveItem();
        Logger.Info($"Fan egrisi uygulandi (custom.txt, otomatik): {string.Join(",", points.Select(p => $"{p.X}:{p.Y}"))}");
      } catch (Exception ex) {
        Logger.Error($"Fan egrisi uygulanamadi: {ex.Message}");
        MessageBox.Show(laptopFanCurveForm, Strings.FanCurveSaveFailed + Environment.NewLine + ex.Message, Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }
  }
}
