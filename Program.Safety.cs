// 本文件中的四项功能（实际功耗限制读取、冲突软件检测、异常关机后的 GPU 超频保护、风扇校准）
// 的思路参考自 OmenCore（MIT License, Copyright (c) 2024-2026 TheAntiPopAU，
// https://github.com/theantipopau/omencore），实现为针对 OSH 重新编写。详见 THIRD_PARTY_NOTICES.md。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  static partial class Program {

    static void ShowBalloon(string title, string text, ToolTipIcon icon) {
      if (trayIcon == null) return;
      if (uiContext != null) uiContext.Post(_ => trayIcon.ShowBalloonTip(5000, title, text, icon), null);
      else trayIcon.ShowBalloonTip(5000, title, text, icon);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 1. 实际生效的 CPU 功耗限制（MSR 0x610）与降频状态（MSR 0x1B1）
    //    HP BIOS 命令 0x29 写入的值与其他软件（OGH、XTU…）写入的值最终都体现在这里，
    //    因此它回答的是“CPU 现在到底被限制在多少瓦”。
    // ═════════════════════════════════════════════════════════════════════════
    const uint MSR_RAPL_POWER_UNIT = 0x606;
    const uint MSR_PKG_POWER_LIMIT = 0x610;
    const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;

    /// <summary>硬件监控子进程中使用：通过 LHM 已加载的 PawnIO IntelMSR 模块读取功耗限制。</summary>
    sealed class MsrPowerReader {
      readonly LibreHardwareMonitor.PawnIo.IntelMsr _msr;
      readonly double _unitWatts;

      MsrPowerReader(LibreHardwareMonitor.PawnIo.IntelMsr msr, double unitWatts) {
        _msr = msr;
        _unitWatts = unitWatts;
      }

      public static MsrPowerReader TryCreate(out string error) {
        error = null;
        try {
          if (!HasIntelCpu()) { error = "Intel olmayan CPU"; return null; }
          var msr = new LibreHardwareMonitor.PawnIo.IntelMsr();
          if (!msr.ReadMsr(MSR_RAPL_POWER_UNIT, out ulong unitRaw)) {
            error = "MSR 0x606 okunamadi";
            try { msr.Close(); } catch { }
            return null;
          }
          // 功率单位 = 1 / 2^(bit 3:0) W；常见为 1/8 W，但按规范读取而不是写死
          return new MsrPowerReader(msr, 1.0 / (1UL << (int)(unitRaw & 0xF)));
        } catch (Exception ex) {
          error = ex.Message;
          return null;
        }
      }

      /// <summary>返回 "PL1;PL2;锁定;温度墙;功耗墙"，读取失败时返回 null。</summary>
      public string ReadField() {
        try {
          if (!_msr.ReadMsr(MSR_PKG_POWER_LIMIT, out ulong limit)) return null;
          double pl1 = (limit & 0x7FFF) * _unitWatts;
          double pl2 = ((limit >> 32) & 0x7FFF) * _unitWatts;
          bool locked = (limit >> 63) != 0;
          int thermal = 0, power = 0;
          // 用封装级的 0x1B1 而非核心级的 0x19C：混合架构（P+E 核）下结果不随线程所在核心变化
          if (_msr.ReadMsr(IA32_PACKAGE_THERM_STATUS, out ulong status)) {
            thermal = (int)(status & 1);          // bit 0：封装温度墙
            power = (int)((status >> 10) & 1);    // bit 10：封装功耗限制生效
          }
          return string.Format(CultureInfo.InvariantCulture, "{0:F1};{1:F1};{2};{3};{4}",
              pl1, pl2, locked ? 1 : 0, thermal, power);
        } catch {
          return null;
        }
      }
    }

    const string MsrFieldsUnavailable = "-1;-1;0;0;0";

    static volatile float msrPl1 = -1f, msrPl2 = -1f;
    static volatile bool msrPlLocked, cpuThermalThrottle, cpuPowerThrottle;
    static bool msrPlLogged;          // 首次读数写入日志，便于核对与设置是否一致
    static bool msrPlMatchedOnce;     // PL1 至少一次与 OSH 设置一致 → 证明本机 BIOS 写入的就是 MSR，此后才对不一致报警
    static int msrPlMismatchSeconds;
    static bool msrPlOverrideNotified;

    static int ParseWatts(string value) {
      if (value != null && value.Contains(" W") && int.TryParse(value.Replace(" W", "").Trim(), out int w)) return w;
      return -1;
    }

    /// <summary>主进程：解析子进程输出的第 10-14 个字段（约每秒一次）。</summary>
    static void OnMsrPowerFields(string[] parts, int idx) {
      if (!float.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out float pl1)) return;
      float.TryParse(parts[idx + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pl2);
      msrPl1 = pl1;
      msrPl2 = pl2;
      msrPlLocked = parts[idx + 2] == "1";
      cpuThermalThrottle = parts[idx + 3] == "1";
      cpuPowerThrottle = parts[idx + 4] == "1";
      if (pl1 <= 0) return;

      int set = ParseWatts(cpuPower);
      if (!msrPlLogged) {
        msrPlLogged = true;
        Logger.Info($"MSR guc limiti: PL1={pl1:F1}W PL2={pl2:F1}W kilit={msrPlLocked} | OSH ayari={cpuPower}");
      }
      if (set <= 0) { msrPlMismatchSeconds = 0; return; }

      bool match = Math.Abs(pl1 - set) <= 2f;
      if (match) {
        if (!msrPlMatchedOnce) Logger.Info($"MSR PL1 OSH ayariyla eslesti ({set} W); dis degisiklik uyarisi etkin");
        msrPlMatchedOnce = true;
        msrPlMismatchSeconds = 0;
        msrPlOverrideNotified = false;
        return;
      }
      // 调节滑块后 BIOS 需要一两秒才生效，持续 10 秒不一致才算被外部覆盖
      if (++msrPlMismatchSeconds >= 10 && msrPlMatchedOnce && !msrPlOverrideNotified) {
        msrPlOverrideNotified = true;
        Logger.Info($"CPU guc limiti disaridan degisti: ayar {set} W, MSR PL1 {pl1:F1} W");
        ShowBalloon(Strings.PlOverrideTitle, Strings.PlOverrideText(set, pl1), ToolTipIcon.Warning);
      }
    }

    static string FormatAppliedPowerLimit() {
      if (msrPl1 < 0) return Strings.MsrUnavailable;
      string s = $"PL1 {msrPl1:F0} W / PL2 {msrPl2:F0} W";
      return msrPlLocked ? $"{s} ({Strings.PlLocked})" : s;
    }

    static List<string> ActiveThrottles() {
      var list = new List<string>();
      if (msrPl1 < 0) return list;
      if (cpuThermalThrottle) list.Add(Strings.ThrottleThermal);
      if (cpuPowerThrottle) list.Add(Strings.ThrottlePower);
      return list;
    }

    static string FormatThrottle() {
      if (msrPl1 < 0) return Strings.MsrUnavailable;
      var list = ActiveThrottles();
      return list.Count == 0 ? Strings.GpuAppsNone : string.Join(", ", list);
    }

    /// <summary>浮窗 CPU 行的后缀，例如 " (温度墙)"；未降频时为空。</summary>
    static string ThrottleSuffix() {
      var list = ActiveThrottles();
      return list.Count == 0 ? "" : $" ({string.Join(", ", list)})";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. 冲突软件检测：会写入风扇/功耗设置的其他软件
    //    进程名按本机实测补全（OGH 实际为 HP.Omen.OmenCommandCenter / OmenCommandCenterBackground）。
    // ═════════════════════════════════════════════════════════════════════════
    sealed class ConflictDef {
      public string Name;
      public string[] Processes;
      public Func<bool> OnlyWhen;   // 为空表示总是检查
    }

    static readonly ConflictDef[] KnownConflicts = {
      new ConflictDef { Name = "OMEN Gaming Hub", Processes = new[] { "OmenCommandCenterBackground", "HP.Omen.OmenCommandCenter", "HPOmenCommandCenter", "OGHAgent", "HP.OMEN.GameHub" } },
      new ConflictDef { Name = "Intel XTU", Processes = new[] { "XtuService", "XTU3Service", "IntelXTU" } },
      new ConflictDef { Name = "ThrottleStop", Processes = new[] { "ThrottleStop" } },
      new ConflictDef { Name = "FanControl", Processes = new[] { "FanControl" } },
      new ConflictDef { Name = "MSI Center", Processes = new[] { "MSI Center", "MSI.CentralServer", "MSI_Central_Service", "MSI.TerminalServer" } },
      new ConflictDef { Name = "OmenCore", Processes = new[] { "OmenCore", "OmenCore.HardwareWorker" } },
      new ConflictDef { Name = "OmenMon", Processes = new[] { "OmenMon" } },
      // 只有 OSH 自身也在设置 GPU 超频或 eGPU 风扇时，两者才会争抢同一组时钟偏移 / 风扇档位
      new ConflictDef { Name = "MSI Afterburner", Processes = new[] { "MSIAfterburner" }, OnlyWhen = () => gpuCoreOverclock > 0 || gpuMemoryOverclock > 0 || egpuFanPercent != EgpuFanAutoMode || egpuPowerLimitW > 0 },
    };

    static readonly object _conflictLock = new object();
    static List<string> currentConflicts = new List<string>();
    static readonly HashSet<string> notifiedConflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static List<string> DetectConflicts() {
      var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var p in Process.GetProcesses()) {
        try { running.Add(p.ProcessName); } catch { } finally { p.Dispose(); }
      }
      var found = new List<string>();
      foreach (var c in KnownConflicts) {
        if (c.OnlyWhen != null && !c.OnlyWhen()) continue;
        if (c.Processes.Any(running.Contains)) found.Add(c.Name);
      }
      return found;
    }

    /// <summary>启动时及每 30 秒检查一次；只在冲突软件“新出现”时提醒，消失后再出现会再次提醒。</summary>
    static void CheckConflicts() {
      Task.Run(() => {
        try {
          var found = DetectConflicts();
          List<string> fresh;
          lock (_conflictLock) {
            currentConflicts = found;
            notifiedConflicts.RemoveWhere(n => !found.Contains(n));
            fresh = found.Where(n => notifiedConflicts.Add(n)).ToList();
          }
          if (fresh.Count > 0) {
            string list = string.Join(", ", fresh);
            Logger.Info($"Cakisan yazilim: {list}");
            ShowBalloon(Strings.ConflictTitle, Strings.ConflictText(list), ToolTipIcon.Warning);
          }
        } catch (Exception ex) {
          Logger.Error($"CheckConflicts: {ex.Message}");
        }
      });
    }

    static string FormatConflicts() {
      lock (_conflictLock) {
        return currentConflicts.Count == 0 ? Strings.GpuAppsNone : string.Join(", ", currentConflicts);
      }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. 异常关机后的 GPU 超频保护
    //    Windows 每次启动都会写入 Kernel-Boot 事件 20，说明上一次关机是否正常。
    //    上次关机异常（崩溃、蓝屏、断电）时，本次开机不应用 GPU 超频；用户拖动超频滑块即可重新应用。
    //    读取失败一律视为未知，此时照常应用——不能因为日志不可读就扣下用户确认过的设置。
    // ═════════════════════════════════════════════════════════════════════════
    static bool lastShutdownRead;
    static bool? lastShutdownClean;
    static bool gpuOcUserReapplied;
    static bool gpuOcSkipNotified;

    static bool? ReadLastShutdownClean() {
      try {
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=20]]") { ReverseDirection = true };
        using (var reader = new EventLogReader(query))
        using (var record = reader.ReadEvent()) {
          if (record == null || record.Properties == null || record.Properties.Count == 0) return null;
          if (record.Properties[0].Value is bool clean) return clean;
          return null;
        }
      } catch {
        return null;
      }
    }

    static bool? GetLastShutdownClean() {
      if (!lastShutdownRead) {
        lastShutdownClean = ReadLastShutdownClean();
        lastShutdownRead = true;
        Logger.Info($"Son kapanis: {(lastShutdownClean == null ? "bilinmiyor" : lastShutdownClean.Value ? "temiz" : "beklenmedik")}");
      }
      return lastShutdownClean;
    }

    /// <summary>恢复配置时调用：是否应跳过本次开机的 GPU 超频。</summary>
    static bool ShouldSkipGpuOc() {
      if (gpuOcUserReapplied) return false;
      if (gpuCoreOverclock <= 0 && gpuMemoryOverclock <= 0) return false;
      if (GetLastShutdownClean() != false) return false;
      if (!gpuOcSkipNotified) {
        gpuOcSkipNotified = true;
        Logger.Info($"Son kapanis beklenmedik: GPU hiz asirtma (+{gpuCoreOverclock}/+{gpuMemoryOverclock}) bu acilista uygulanmadi");
        ShowBalloon(Strings.GpuOcSkippedTitle, Strings.GpuOcSkippedText, ToolTipIcon.Warning);
      }
      return true;
    }

    static string FormatLastShutdown() {
      bool? clean = GetLastShutdownClean();
      return clean == null ? Strings.SysUnknown : clean.Value ? Strings.ShutdownClean : Strings.ShutdownUnclean;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. 风扇校准：测量每个风扇的实际最高转速
    //    与 OmenCore 不同：不假设最高等级为 55，而是直接下发安全上限（127），
    //    并且持续采样直到转速稳定，而不是固定等待几秒。
    // ═════════════════════════════════════════════════════════════════════════
    static volatile bool fanCalibrating;
    static bool fanCalibrationLoaded;
    static int[] fanCalibratedMax;
    static string fanCalibratedAt;

    static void LoadFanCalibration() {
      if (fanCalibrationLoaded) return;
      fanCalibrationLoaded = true;
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          var raw = key?.GetValue("FanCalibratedMaxRpm") as string;
          if (string.IsNullOrEmpty(raw)) return;
          var values = raw.Split(',').Select(s => int.TryParse(s.Trim(), out int v) ? v : 0).ToArray();
          if (values.Length > 0 && values.All(v => v >= 1000)) {
            fanCalibratedMax = values;
            fanCalibratedAt = key.GetValue("FanCalibratedAt") as string;
          }
        }
      } catch (Exception ex) {
        Logger.Error($"LoadFanCalibration: {ex.Message}");
      }
    }

    static int GetCalibratedMaxRpm() {
      LoadFanCalibration();
      return fanCalibratedMax == null ? 0 : fanCalibratedMax.Max();
    }

    static string FormatFanList(int[] rpms) =>
        string.Join(", ", rpms.Select((r, i) => $"Fan {i + 1} {r} RPM"));

    static string FormatCalibrationInfo() {
      LoadFanCalibration();
      return fanCalibratedMax == null ? null : Strings.FanCalibratedInfo(FormatFanList(fanCalibratedMax), fanCalibratedAt ?? "?");
    }

    static void StartFanCalibration(ToolStripMenuItem fanMenu) {
      if (fanCalibrating) return;
      var owner = Application.OpenForms.OfType<HelpForm>().FirstOrDefault();
      if (MessageBox.Show(owner, Strings.FanCalibrateConfirm, Strings.FanCalibrateTitle,
              MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        return;

      fanCalibrating = true;
      if (fanMenu != null) fanMenu.Enabled = false;
      ShowBalloon(Strings.FanCalibrateTitle, Strings.FanCalibrating, ToolTipIcon.Info);

      Task.Run(() => {
        int[] max = null;
        double seconds = 0;
        Exception error = null;
        try {
          max = MeasureFanMax(out seconds);
        } catch (Exception ex) {
          error = ex;
        }
        uiContext.Post(_ => FinishFanCalibration(fanMenu, max, seconds, error), null);
      });
    }

    static int[] MeasureFanMax(out double seconds) {
      fanControlTimer.Change(Timeout.Infinite, Timeout.Infinite);
      SetMaxFanSpeedOff();
      int fanCount = Is3FanNb ? 3 : 2;
      var max = new int[fanCount];
      var sw = Stopwatch.StartNew();
      int quietSeconds = 0;
      int lastAssert = -10;

      while (sw.Elapsed.TotalSeconds < 45) {
        int t = (int)sw.Elapsed.TotalSeconds;
        // EC 可能自行回落，每 5 秒重新下发一次
        if (t - lastAssert >= 5) {
          SetFanLevel(MaxSafeFanLevel, MaxSafeFanLevel, Is3FanNb);
          lastAssert = t;
        }
        Thread.Sleep(1000);

        var levels = GetFanLevel();
        bool improved = false;
        for (int i = 0; i < fanCount && i < levels.Count; i++) {
          int rpm = levels[i] * 100;
          if (rpm > max[i] + 100) improved = true;   // 读数精度为 100 RPM，忽略单级抖动
          if (rpm > max[i]) max[i] = rpm;
        }
        // 至少等风扇加速 8 秒，之后连续 6 秒没有明显上升即视为到顶
        if (improved) quietSeconds = 0;
        else if (sw.Elapsed.TotalSeconds >= 8) quietSeconds++;
        if (quietSeconds >= 6) break;
      }
      seconds = sw.Elapsed.TotalSeconds;
      return max;
    }

    static void FinishFanCalibration(ToolStripMenuItem fanMenu, int[] max, double seconds, Exception error) {
      fanCalibrating = false;
      if (fanMenu != null) fanMenu.Enabled = true;
      RestoreFanControl();

      var owner = Application.OpenForms.OfType<HelpForm>().FirstOrDefault();
      if (error != null || max == null || max.Length == 0 || max.Any(r => r < 1000)) {
        Logger.Error($"Fan kalibrasyonu basarisiz: {(error != null ? error.Message : string.Join(",", max ?? new int[0]))}");
        MessageBox.Show(owner, Strings.FanCalibrateFailed, Strings.FanCalibrateTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub")) {
          key.SetValue("FanCalibratedMaxRpm", string.Join(",", max));
          key.SetValue("FanCalibratedAt", DateTime.Now.ToString("yyyy-MM-dd"));
        }
      } catch (Exception ex) {
        Logger.Error($"Fan kalibrasyonu kaydedilemedi: {ex.Message}");
      }
      fanCalibratedMax = max;
      fanCalibratedAt = DateTime.Now.ToString("yyyy-MM-dd");
      fanCalibrationLoaded = true;

      int ceiling = GetFanSliderMaxRpm();
      // 当前滑块值超过新上限时不立即改动：缩小上限会触发 ValueChanged 并把风扇模式切成固定转速
      if (fanTrackBar != null && fanTrackBar.Value <= ceiling / 100) {
        fanTrackBar.Maximum = ceiling / 100;
        fanTrackBar.TickFrequency = fanTrackBar.Maximum - fanTrackBar.Minimum;
      }
      Logger.Info($"Fan kalibrasyonu: {FormatFanList(max)} ({seconds:F0} sn), kaydirici tavani {ceiling} RPM");
      MessageBox.Show(owner, Strings.FanCalibrateDone(FormatFanList(max), ceiling), Strings.FanCalibrateTitle,
          MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
  }
}
