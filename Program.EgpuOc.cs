// eGPU（外接显卡）超频：通过 NVAPI 设置外接 NVIDIA 卡 P0 的核心/显存频率偏移（与 MSI Afterburner 的 Core/Memory Clock 同一接口）。
// OSH 默认不碰 eGPU 频率；用户拖动滑块后才接管。偏移在重启、驱动重载或 TDR 后会被清零，所以启动时、eGPU 重新出现时
// 以及每 30 秒校验一次并在需要时重新应用。若偏移被清零且最近 10 分钟内有显卡驱动重置（TDR / nvlddmkm 事件），
// 说明超频可能不稳定：此时暂停而不是再次应用，直到用户重新拖动滑块。上次关机异常时本次开机也不应用。
// CUDA 强制 P2：GeForce 卡上 CUDA 程序（如 LM Studio）默认跑在 P2，显存低一档且不吃 P0 的显存偏移；可在驱动全局配置中关闭。
using System;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace OmenSuperHub {
  static partial class Program {
    const int EgpuOcCoreStep = 15, EgpuOcMemStep = 50;   // Ampere 核心频率以 15 MHz 为一档
    const int EgpuOcCoreMin = -195, EgpuOcCoreMax = 300, EgpuOcMemMin = 0, EgpuOcMemMax = 3000;
    static bool egpuOcManaged;                 // false = 不接管（注册表里没有偏移值）
    static int egpuCoreOffset, egpuMemOffset;  // 用户设定（MHz）
    static volatile bool egpuOcPaused;         // 驱动重置后暂停，直到用户再次拖动滑块
    static volatile bool egpuOcBootSkip;       // 上次关机异常：本次运行不自动应用
    static volatile bool egpuOcApplied;        // 本次运行中是否已成功写入
    static volatile int egpuOcReadCore = int.MinValue, egpuOcReadMem = int.MinValue;   // 最近一次从显卡读回的值
    static bool cudaP2Off;
    static int egpuOcApplySeq, egpuOcChecking;
    static readonly object egpuOcLock = new object();
    static ToolStripMenuItem egpuOcStatusItem, egpuOcCoreLabel, egpuOcMemLabel, egpuOcResetItem, cudaP2Item;
    static ToolStripTrackBar egpuOcCoreTrackBar, egpuOcMemTrackBar;
    static bool egpuOcSliderUpdating;

    static int SnapEgpuCore(int v) => Math.Max(EgpuOcCoreMin, Math.Min(EgpuOcCoreMax, (int)Math.Round(v / (double)EgpuOcCoreStep) * EgpuOcCoreStep));
    static int SnapEgpuMem(int v) => Math.Max(EgpuOcMemMin, Math.Min(EgpuOcMemMax, (int)Math.Round(v / (double)EgpuOcMemStep) * EgpuOcMemStep));

    static void ReadEgpuOcSettings() {
      egpuOcManaged = false;
      egpuCoreOffset = egpuMemOffset = 0;
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          object core = key?.GetValue("EgpuCoreOffsetMHz"), mem = key?.GetValue("EgpuMemOffsetMHz");
          if (core != null || mem != null) {
            egpuOcManaged = true;
            egpuCoreOffset = SnapEgpuCore(Convert.ToInt32(core ?? 0));
            egpuMemOffset = SnapEgpuMem(Convert.ToInt32(mem ?? 0));
          }
          egpuOcPaused = Convert.ToInt32(key?.GetValue("EgpuOcPaused") ?? 0) == 1;
          cudaP2Off = Convert.ToInt32(key?.GetValue("CudaP2Off") ?? 0) == 1;
        }
      } catch (Exception ex) {
        Logger.Error($"eGPU hiz asirtma ayarlari okunamadi: {ex.Message}");
      }
    }

    static void SaveEgpuOcSettings() {
      try {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\OmenSuperHub")) {
          if (egpuOcManaged) {
            key.SetValue("EgpuCoreOffsetMHz", egpuCoreOffset);
            key.SetValue("EgpuMemOffsetMHz", egpuMemOffset);
          } else {
            key.DeleteValue("EgpuCoreOffsetMHz", false);
            key.DeleteValue("EgpuMemOffsetMHz", false);
          }
          key.SetValue("EgpuOcPaused", egpuOcPaused ? 1 : 0);
          key.SetValue("CudaP2Off", cudaP2Off ? 1 : 0);
        }
      } catch (Exception ex) {
        Logger.Error($"eGPU hiz asirtma ayarlari kaydedilemedi: {ex.Message}");
      }
    }

    /// <summary>外接 NVIDIA GPU；没有时返回 null。调用方持有 egpuOcLock。</summary>
    static PhysicalGPU FindEgpuForOc() {
      NVIDIA.Initialize();
      return GpuAppManager.PickExternalGpu(PhysicalGPU.GetPhysicalGPUs());
    }

    /// <summary>写入偏移并读回确认。调用方持有 egpuOcLock；失败时抛出异常。</summary>
    static void ApplyEgpuOcCore(PhysicalGPU gpu, int core, int mem, string reason) {
      GpuAppManager.SetPState20Offset(gpu, PublicClockDomain.Graphics, core);
      GpuAppManager.SetPState20Offset(gpu, PublicClockDomain.Memory, mem);
      GpuAppManager.GetPState20Offsets(gpu, out int rc, out int rm);
      egpuOcReadCore = rc;
      egpuOcReadMem = rm;
      if (rc != core || rm != mem) throw new InvalidOperationException($"geri okunan cekirdek {rc}, bellek {rm} MHz");
      egpuOcApplied = core != 0 || mem != 0;
      Logger.Info($"eGPU hiz asirtma uygulandi ({reason}): cekirdek {core:+0;-0;0} MHz, bellek {mem:+0;-0;0} MHz");
    }

    /// <summary>最近一段时间内是否有显卡驱动重置（TDR，Display 4101）或 nvlddmkm 错误事件。读取失败视为没有。</summary>
    static bool RecentDisplayDriverReset(TimeSpan window, out string description) {
      description = null;
      try {
        long ms = (long)window.TotalMilliseconds;
        var query = new EventLogQuery("System", PathType.LogName,
            $"*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
        using (var reader = new EventLogReader(query))
        using (EventRecord record = reader.ReadEvent()) {
          if (record == null) return false;
          description = $"{record.ProviderName} {record.Id} {record.TimeCreated:HH:mm:ss}";
          return true;
        }
      } catch {
        return false;
      }
    }

    static void InitEgpuOc() {
      ReadEgpuOcSettings();
      if (egpuOcManaged && (egpuCoreOffset > 0 || egpuMemOffset > 0) && !egpuOcPaused && GetLastShutdownClean() == false) {
        egpuOcBootSkip = true;
        Logger.Info($"Son kapanis beklenmedik: eGPU hiz asirtma ({egpuCoreOffset:+0;-0;0}/{egpuMemOffset:+0;-0;0} MHz) bu acilista uygulanmadi");
        ShowBalloon(Strings.GpuOcSkippedTitle, Strings.GpuOcSkippedText, ToolTipIcon.Warning);
      }
      if (egpuOcPaused) Logger.Info($"eGPU hiz asirtma duraklatilmis ({egpuCoreOffset:+0;-0;0}/{egpuMemOffset:+0;-0;0} MHz): kaydirici oynatilana kadar uygulanmaz");
      if (cudaP2Off) Task.Run(() => EnsureCudaP2(true, "baslangic"));
      CheckEgpuOc("baslangic");
    }

    /// <summary>后台读取 eGPU 偏移；接管时若与设定不同则重新应用（驱动重置后则暂停）。</summary>
    static void CheckEgpuOc(string reason) {
      if (Interlocked.CompareExchange(ref egpuOcChecking, 1, 0) != 0) return;
      Task.Run(() => {
        try {
          lock (egpuOcLock) {
            PhysicalGPU gpu = FindEgpuForOc();
            if (gpu == null) return;
            GpuAppManager.GetPState20Offsets(gpu, out int rc, out int rm);
            egpuOcReadCore = rc;
            egpuOcReadMem = rm;
            if (!egpuOcManaged || egpuOcPaused || egpuOcBootSkip) return;
            if (rc == egpuCoreOffset && rm == egpuMemOffset) {
              egpuOcApplied = rc != 0 || rm != 0;
              return;
            }
            // 本次运行中写入过的偏移被清零：多半是驱动重置。最近有 TDR/nvlddmkm 事件 → 超频可能不稳定，暂停
            if (egpuOcApplied && RecentDisplayDriverReset(TimeSpan.FromMinutes(10), out string evt)) {
              egpuOcPaused = true;
              egpuOcApplied = false;
              SaveEgpuOcSettings();
              Logger.Info($"eGPU hiz asirtma duraklatildi ({reason}): ofsetler {rc}/{rm} MHz'e dondu, son surucu olayi: {evt}");
              ShowBalloon(Strings.EgpuOcPausedTitle, Strings.EgpuOcPausedText(egpuCoreOffset, egpuMemOffset), ToolTipIcon.Warning);
              return;
            }
            ApplyEgpuOcCore(gpu, egpuCoreOffset, egpuMemOffset, $"{reason}; okunan {rc:+0;-0;0}/{rm:+0;-0;0} MHz");
          }
        } catch (Exception ex) {
          Logger.Error($"eGPU hiz asirtma ({reason}): {ex.Message}");
        } finally {
          Interlocked.Exchange(ref egpuOcChecking, 0);
          if (uiContext != null) uiContext.Post(_ => SyncEgpuOcMenu(), null);
        }
      });
    }

    /// <summary>滑块变化：保存并在 400 ms 后应用（拖动时只写最后一次）。用户操作同时解除暂停和“异常关机跳过”。</summary>
    static void OnEgpuOcSliderChanged() {
      if (egpuOcSliderUpdating) return;
      egpuCoreOffset = egpuOcCoreTrackBar.Value * EgpuOcCoreStep;
      egpuMemOffset = egpuOcMemTrackBar.Value * EgpuOcMemStep;
      egpuOcManaged = true;
      egpuOcPaused = false;
      egpuOcBootSkip = false;
      egpuOcCoreLabel.Text = Strings.EgpuOcCoreValue(egpuCoreOffset);
      egpuOcMemLabel.Text = Strings.EgpuOcMemValue(egpuMemOffset);
      egpuOcResetItem.Checked = false;
      SaveEgpuOcSettings();
      ApplyEgpuOcDeferred(egpuCoreOffset, egpuMemOffset, "menu");
    }

    static void ApplyEgpuOcDeferred(int core, int mem, string reason) {
      int seq = Interlocked.Increment(ref egpuOcApplySeq);
      Task.Run(() => {
        Thread.Sleep(400);
        if (seq != Volatile.Read(ref egpuOcApplySeq)) return;
        lock (egpuOcLock) {
          try {
            PhysicalGPU gpu = FindEgpuForOc() ?? throw new InvalidOperationException("eGPU bulunamadi");
            ApplyEgpuOcCore(gpu, core, mem, reason);
          } catch (Exception ex) {
            Logger.Error($"eGPU hiz asirtma uygulanamadi: {ex.Message}");
            ShowBalloon(Strings.EgpuPowerMenu, Strings.EgpuOcApplyFailed(ex.Message), ToolTipIcon.Warning);
          }
        }
        if (uiContext != null) uiContext.Post(_ => SyncEgpuOcMenu(), null);
      });
    }

    static void ResetEgpuOc() {
      egpuOcManaged = false;
      egpuOcPaused = false;
      egpuCoreOffset = egpuMemOffset = 0;
      SaveEgpuOcSettings();
      SyncEgpuOcMenu();
      ApplyEgpuOcDeferred(0, 0, "menu: stok");
    }

    /// <summary>让驱动全局配置与设定一致；返回是否成功。</summary>
    static bool EnsureCudaP2(bool off, string reason) {
      lock (egpuOcLock) {
        try {
          NVIDIA.Initialize();
          if (GpuAppManager.IsCudaForceP2Off() == off) {
            Logger.Info($"CUDA P2 ({reason}): zaten {(off ? "kilit kapali (CUDA P0'da)" : "surucu varsayilani (CUDA P2'de)")}");
            return true;
          }
          GpuAppManager.SetCudaForceP2Off(off);
          bool now = GpuAppManager.IsCudaForceP2Off();
          if (now != off) throw new InvalidOperationException("ayar geri okunamadi");
          Logger.Info($"CUDA P2 ({reason}): {(off ? "kilit kaldirildi, CUDA P0'da calisacak" : "surucu varsayilanina donuldu")}");
          return true;
        } catch (Exception ex) {
          Logger.Error($"CUDA P2 ayari ({reason}): {ex.Message}");
          ShowBalloon(Strings.EgpuPowerMenu, Strings.CudaP2Failed(ex.Message), ToolTipIcon.Warning);
          return false;
        }
      }
    }

    static void ToggleCudaP2() {
      bool off = !cudaP2Off;
      cudaP2Off = off;
      cudaP2Item.Checked = off;
      SaveEgpuOcSettings();
      Task.Run(() => {
        if (EnsureCudaP2(off, "menu")) ShowBalloon(Strings.EgpuPowerMenu, Strings.CudaP2Applied(off), ToolTipIcon.Info);
      });
    }

    // ── 菜单（放在 eGPU 功率子菜单里，不新增顶层项）──────────────────────────────

    static ToolStripTrackBar NewEgpuOcTrackBar(int minStep, int maxStep, int valueStep) {
      var tb = new ToolStripTrackBar();
      tb.Minimum = minStep;
      tb.Maximum = maxStep;
      tb.Value = Math.Max(minStep, Math.Min(maxStep, valueStep));
      tb.TickFrequency = 5;
      tb.Width = 800;
      tb.TrackBarControl.SmallChange = 1;
      tb.TrackBarControl.LargeChange = 2;
      tb.ValueChanged += (s, e) => OnEgpuOcSliderChanged();
      return tb;
    }

    static void AddEgpuOcItems(ToolStripMenuItem menu) {
      ReadEgpuOcSettings();   // 菜单在 InitEgpuOc 之前构建
      menu.DropDownItems.Add(new ToolStripSeparator());
      egpuOcStatusItem = new ToolStripMenuItem(Strings.EgpuOcHeader(egpuCoreOffset, egpuMemOffset)) { Enabled = false };
      menu.DropDownItems.Add(egpuOcStatusItem);
      egpuOcResetItem = new ToolStripMenuItem(Strings.EgpuOcReset) { Checked = !egpuOcManaged, ToolTipText = Strings.EgpuOcResetTip };
      egpuOcResetItem.Click += (s, e) => ResetEgpuOc();
      menu.DropDownItems.Add(egpuOcResetItem);

      menu.DropDownItems.Add(new ToolStripMenuItem(Strings.EgpuOcCoreSlider) { Enabled = false });
      egpuOcCoreTrackBar = NewEgpuOcTrackBar(EgpuOcCoreMin / EgpuOcCoreStep, EgpuOcCoreMax / EgpuOcCoreStep, egpuCoreOffset / EgpuOcCoreStep);
      egpuOcCoreLabel = new ToolStripMenuItem(Strings.EgpuOcCoreValue(egpuCoreOffset)) { Enabled = false };
      menu.DropDownItems.Add(egpuOcCoreTrackBar);
      menu.DropDownItems.Add(egpuOcCoreLabel);

      menu.DropDownItems.Add(new ToolStripMenuItem(Strings.EgpuOcMemSlider) { Enabled = false });
      egpuOcMemTrackBar = NewEgpuOcTrackBar(EgpuOcMemMin / EgpuOcMemStep, EgpuOcMemMax / EgpuOcMemStep, egpuMemOffset / EgpuOcMemStep);
      egpuOcMemLabel = new ToolStripMenuItem(Strings.EgpuOcMemValue(egpuMemOffset)) { Enabled = false };
      menu.DropDownItems.Add(egpuOcMemTrackBar);
      menu.DropDownItems.Add(egpuOcMemLabel);

      menu.DropDownItems.Add(new ToolStripSeparator());
      cudaP2Item = new ToolStripMenuItem(Strings.CudaP2Item) { Checked = cudaP2Off, ToolTipText = Strings.CudaP2Tip };
      cudaP2Item.Click += (s, e) => ToggleCudaP2();
      menu.DropDownItems.Add(cudaP2Item);
    }

    /// <summary>在 UI 线程同步滑块、数值和状态行。</summary>
    static void SyncEgpuOcMenu() {
      if (egpuOcStatusItem == null) return;
      egpuOcSliderUpdating = true;
      try {
        egpuOcCoreTrackBar.Value = Math.Max(egpuOcCoreTrackBar.Minimum, Math.Min(egpuOcCoreTrackBar.Maximum, egpuCoreOffset / EgpuOcCoreStep));
        egpuOcMemTrackBar.Value = Math.Max(egpuOcMemTrackBar.Minimum, Math.Min(egpuOcMemTrackBar.Maximum, egpuMemOffset / EgpuOcMemStep));
      } finally {
        egpuOcSliderUpdating = false;
      }
      egpuOcCoreLabel.Text = Strings.EgpuOcCoreValue(egpuCoreOffset);
      egpuOcMemLabel.Text = Strings.EgpuOcMemValue(egpuMemOffset);
      int core = egpuOcReadCore != int.MinValue ? egpuOcReadCore : egpuCoreOffset;
      int mem = egpuOcReadMem != int.MinValue ? egpuOcReadMem : egpuMemOffset;
      egpuOcStatusItem.Text = Strings.EgpuOcHeader(core, mem) + (egpuOcPaused ? Strings.EgpuOcPausedSuffix : "");
      egpuOcResetItem.Checked = !egpuOcManaged;
      cudaP2Item.Checked = cudaP2Off;
    }
  }
}
