// eGPU（外接显卡）功率限制：通过 nvidia-smi -pl 设置外接 NVIDIA 卡的功率上限（需要管理员权限，OSH 以最高权限运行）。
// 笔记本自身独显的功率限制由 HP BIOS 管理，nvidia-smi 对其返回 [N/A]，因此只作用于功率限制可调的非内置 GPU。
// nvidia-smi 设置的限制在重启 / 驱动重载后失效，所以启动时、eGPU 重新出现时以及每 30 秒校验一次并在需要时重新应用。
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using static OmenSuperHub.GpuAppManager;

namespace OmenSuperHub {
  static partial class Program {
    // -1 = 不接管（保持当前/驱动默认）；其余为用户设定的瓦数
    static int egpuPowerLimitW = -1;
    static volatile bool egpuPowerAvailable = false;
    static string egpuPowerBusId, egpuPowerGpuName = "eGPU";
    static int egpuPowerMinW = 100, egpuPowerMaxW = 450, egpuPowerDefaultW = 350;
    static volatile int egpuPowerCurrentLimitW = 0, egpuPowerDrawW = 0;
    static int egpuPowerApplySeq = 0;
    static int egpuPowerChecking = 0;
    static readonly object egpuPowerLock = new object();
    static ToolStripMenuItem egpuPowerMenu, egpuPowerStatusItem, egpuPowerDefaultItem, egpuPowerMaxItem, egpuPowerValueLabel;
    static ToolStripTrackBar egpuPowerTrackBar;
    static bool egpuPowerSliderUpdating = false;

    static int ReadEgpuPowerLimit() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          object raw = key?.GetValue("EgpuPowerLimitW");
          if (raw != null) return Convert.ToInt32(raw);
        }
      } catch (Exception ex) {
        Logger.Error($"EgpuPowerLimitW okunamadi: {ex.Message}");
      }
      return -1;
    }

    static void SaveEgpuPowerLimit() {
      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub"))
          key.SetValue("EgpuPowerLimitW", egpuPowerLimitW);
      } catch (Exception ex) {
        Logger.Error($"EgpuPowerLimitW kaydedilemedi: {ex.Message}");
      }
    }

    static int ParseSmiWatts(string s) {
      s = s.Trim();
      return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? (int)Math.Round(v) : -1;
    }

    /// <summary>用 nvidia-smi 查询功率限制可调的非内置 NVIDIA GPU，更新缓存的数值。调用方持有 egpuPowerLock。</summary>
    static bool QueryEgpuPower() {
      ProcessResult r = ExecuteCommand("nvidia-smi --query-gpu=pci.bus_id,name,power.limit,power.default_limit,power.min_limit,power.max_limit,power.draw --format=csv,noheader,nounits");
      if (r.ExitCode != 0) return false;
      foreach (string line in r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
        string[] p = line.Split(',');
        if (p.Length < 7) continue;
        string name = p[1].Trim();
        if (name.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
        int limit = ParseSmiWatts(p[2]), def = ParseSmiWatts(p[3]), min = ParseSmiWatts(p[4]), max = ParseSmiWatts(p[5]);
        if (limit <= 0 || min <= 0 || max <= min) continue;   // [N/A]：不可调
        egpuPowerBusId = p[0].Trim();
        egpuPowerGpuName = name;
        egpuPowerCurrentLimitW = limit;
        egpuPowerDefaultW = def > 0 ? def : limit;
        egpuPowerMinW = min;
        egpuPowerMaxW = max;
        egpuPowerDrawW = Math.Max(0, ParseSmiWatts(p[6]));
        return true;
      }
      return false;
    }

    /// <summary>把 watts 写入显卡。调用方持有 egpuPowerLock；失败时抛出异常。</summary>
    static void ApplyEgpuPowerCore(int watts, string reason) {
      if (string.IsNullOrEmpty(egpuPowerBusId)) throw new InvalidOperationException("eGPU bulunamadi");
      watts = Math.Max(egpuPowerMinW, Math.Min(egpuPowerMaxW, watts));
      ProcessResult r = ExecuteCommand($"nvidia-smi -i {egpuPowerBusId} -pl {watts}");
      if (r.ExitCode != 0) {
        string msg = (r.Output + " " + r.Error).Trim();
        throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"nvidia-smi cikis kodu {r.ExitCode}" : msg);
      }
      egpuPowerCurrentLimitW = watts;
      Logger.Info($"eGPU guc siniri uygulandi ({reason}): {watts} W");
    }

    static void InitEgpuPower() {
      egpuPowerLimitW = ReadEgpuPowerLimit();
      CheckEgpuPower("baslangic");
    }

    /// <summary>后台检测 eGPU 功率限制；若用户设定了数值且当前限制不同（重启、驱动重载、其他软件改动），则重新应用。</summary>
    static void CheckEgpuPower(string reason) {
      if (Interlocked.CompareExchange(ref egpuPowerChecking, 1, 0) != 0) return;
      Task.Run(() => {
        try {
          lock (egpuPowerLock) {
            bool wasAvailable = egpuPowerAvailable;
            egpuPowerAvailable = QueryEgpuPower();
            if (!egpuPowerAvailable) {
              if (wasAvailable || reason == "baslangic") Logger.Info($"eGPU guc ({reason}): guc siniri ayarlanabilen harici GPU yok");
              return;
            }
            if (!wasAvailable)
              Logger.Info($"eGPU guc ({reason}): {egpuPowerGpuName} {egpuPowerBusId}, sinir {egpuPowerCurrentLimitW} W (varsayilan {egpuPowerDefaultW}, aralik {egpuPowerMinW}-{egpuPowerMaxW}), kayitli ayar: {(egpuPowerLimitW > 0 ? egpuPowerLimitW + " W" : "yok")}");
            if (egpuPowerLimitW > 0 && Math.Abs(egpuPowerCurrentLimitW - egpuPowerLimitW) > 1)
              ApplyEgpuPowerCore(egpuPowerLimitW, $"{reason}; olculen {egpuPowerCurrentLimitW} W");
          }
          if (uiContext != null) uiContext.Post(_ => SyncEgpuPowerMenu(), null);
        } catch (Exception ex) {
          Logger.Error($"eGPU guc siniri ({reason}): {ex.Message}");
        } finally {
          Interlocked.Exchange(ref egpuPowerChecking, 0);
        }
      });
    }

    /// <summary>菜单入口：watts = -1 表示恢复默认值并不再接管。拖动滑块的连续变化会被合并。</summary>
    static void SetEgpuPowerLimit(int watts) {
      egpuPowerLimitW = watts;
      SaveEgpuPowerLimit();
      int seq = Interlocked.Increment(ref egpuPowerApplySeq);
      Task.Run(() => {
        Thread.Sleep(400);
        if (seq != Volatile.Read(ref egpuPowerApplySeq)) return;
        lock (egpuPowerLock) {
          try {
            if (string.IsNullOrEmpty(egpuPowerBusId) && !QueryEgpuPower()) throw new InvalidOperationException("eGPU bulunamadi");
            ApplyEgpuPowerCore(watts > 0 ? watts : egpuPowerDefaultW, watts > 0 ? "menu" : "menu: varsayilan");
          } catch (Exception ex) {
            Logger.Error($"eGPU guc siniri uygulanamadi: {ex.Message}");
            ShowBalloon(Strings.EgpuPowerMenu, Strings.EgpuPowerApplyFailed(ex.Message), ToolTipIcon.Warning);
          }
        }
        if (uiContext != null) uiContext.Post(_ => RefreshEgpuPowerStatus(), null);
      });
    }

    // ── 菜单 ────────────────────────────────────────────────────────────────

    static ToolStripMenuItem BuildEgpuPowerMenu() {
      // 菜单在 InitEgpuPower 之前构建，先读取已保存的设置
      egpuPowerLimitW = ReadEgpuPowerLimit();
      egpuPowerMenu = new ToolStripMenuItem(Strings.EgpuPowerMenu) { Visible = false };
      egpuPowerStatusItem = new ToolStripMenuItem("") { Enabled = false };
      egpuPowerMenu.DropDownItems.Add(egpuPowerStatusItem);
      egpuPowerMenu.DropDownItems.Add(new ToolStripSeparator());
      egpuPowerDefaultItem = CreateMenuItem(Strings.EgpuPowerDefault(egpuPowerDefaultW), "egpuPowerGroup", (s, e) => SetEgpuPowerLimit(-1), egpuPowerLimitW <= 0, Strings.EgpuPowerDefaultTip);
      egpuPowerMaxItem = CreateMenuItem(Strings.EgpuPowerMax(egpuPowerMaxW), "egpuPowerGroup", (s, e) => SetEgpuPowerLimit(egpuPowerMaxW), false);
      egpuPowerMenu.DropDownItems.Add(egpuPowerDefaultItem);
      egpuPowerMenu.DropDownItems.Add(egpuPowerMaxItem);
      egpuPowerMenu.DropDownItems.Add(CreateMenuItem(Strings.EgpuPowerSlider, "egpuPowerGroup", (s, e) => { }, false));

      egpuPowerTrackBar = new ToolStripTrackBar();
      egpuPowerTrackBar.Minimum = egpuPowerMinW;
      egpuPowerTrackBar.Maximum = egpuPowerMaxW;
      egpuPowerTrackBar.TickFrequency = 50;
      egpuPowerTrackBar.Width = 800;
      egpuPowerTrackBar.TrackBarControl.SmallChange = 5;
      egpuPowerTrackBar.TrackBarControl.LargeChange = 25;
      egpuPowerTrackBar.Value = Math.Max(egpuPowerMinW, Math.Min(egpuPowerMaxW, egpuPowerLimitW > 0 ? egpuPowerLimitW : egpuPowerDefaultW));
      egpuPowerValueLabel = new ToolStripMenuItem(string.Format(Strings.CurrentSliderValueTemp, $"{egpuPowerTrackBar.Value} W")) { Enabled = false };
      egpuPowerTrackBar.ValueChanged += (s, e) => {
        if (egpuPowerSliderUpdating) return;
        // 5 W 一档，避免无意义的 1 W 抖动
        int w = (int)Math.Round(egpuPowerTrackBar.Value / 5.0) * 5;
        w = Math.Max(egpuPowerMinW, Math.Min(egpuPowerMaxW, w));
        egpuPowerValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{w} W");
        SetEgpuPowerLimit(w);
        UpdateCheckedState("egpuPowerGroup", w == egpuPowerMaxW ? egpuPowerMaxItem.Text : Strings.EgpuPowerSlider);
      };
      egpuPowerMenu.DropDownItems.Add(egpuPowerTrackBar);
      egpuPowerMenu.DropDownItems.Add(egpuPowerValueLabel);
      egpuPowerMenu.DropDownOpening += (s, e) => {
        RefreshEgpuPowerStatus();
        CheckEgpuPower("menu");
      };
      return egpuPowerMenu;
    }

    /// <summary>检测完成后在 UI 线程同步菜单：可见性、滑块范围、默认/最大文字和勾选状态。</summary>
    static void SyncEgpuPowerMenu() {
      if (egpuPowerMenu == null) return;
      egpuPowerMenu.Visible = egpuPowerAvailable;
      if (!egpuPowerAvailable) return;
      egpuPowerSliderUpdating = true;
      try {
        egpuPowerTrackBar.Minimum = egpuPowerMinW;
        egpuPowerTrackBar.Maximum = egpuPowerMaxW;
        int v = egpuPowerLimitW > 0 ? egpuPowerLimitW : egpuPowerCurrentLimitW;
        egpuPowerTrackBar.Value = Math.Max(egpuPowerMinW, Math.Min(egpuPowerMaxW, v));
        egpuPowerValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, $"{egpuPowerTrackBar.Value} W");
      } finally {
        egpuPowerSliderUpdating = false;
      }
      egpuPowerDefaultItem.Text = Strings.EgpuPowerDefault(egpuPowerDefaultW);
      egpuPowerMaxItem.Text = Strings.EgpuPowerMax(egpuPowerMaxW);
      egpuPowerDefaultItem.Checked = egpuPowerLimitW <= 0;
      egpuPowerMaxItem.Checked = egpuPowerLimitW == egpuPowerMaxW;
      foreach (ToolStripMenuItem item in egpuPowerMenu.DropDownItems.OfType<ToolStripMenuItem>())
        if (item.Text == Strings.EgpuPowerSlider) item.Checked = egpuPowerLimitW > 0 && egpuPowerLimitW != egpuPowerMaxW;
      RefreshEgpuPowerStatus();
    }

    static void RefreshEgpuPowerStatus() {
      if (egpuPowerStatusItem == null) return;
      egpuPowerStatusItem.Text = Strings.EgpuPowerStatus(egpuPowerGpuName, egpuPowerCurrentLimitW, egpuPowerDrawW, egpuPowerMinW, egpuPowerMaxW);
      egpuPowerMenu.Text = egpuPowerCurrentLimitW > 0 ? $"{Strings.EgpuPowerMenu} ({egpuPowerCurrentLimitW} W)" : Strings.EgpuPowerMenu;
    }
  }
}
