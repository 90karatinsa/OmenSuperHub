// 内置独显超频守护：驱动重置（TDR）会把 P0 频率偏移清零，之前只有重启 OSH 才会重新应用。
// 每 30 秒读回一次；偏移被清零就重新应用（Dynamic Boost 修复也会因 NPCF 重新初始化而丢失，一并重做）。
// 2 小时内第二次被清零 = 当前超频在实际负载下不稳定：不再应用，保持默认频率直到用户动滑块或重启 OSH。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using static OmenSuperHub.GpuAppManager;

namespace OmenSuperHub {
  static partial class Program {
    static int gpuOcGuardRunning = 0;
    static volatile bool gpuOcGuardPaused = false;
    static readonly List<DateTime> gpuOcGuardResets = new List<DateTime>();

    static void CheckInternalGpuOc() {
      int wantCore = Math.Max(0, gpuCoreOverclock), wantMem = Math.Max(0, gpuMemoryOverclock);
      if (wantCore == 0 && wantMem == 0) return;
      // 暂停后保持到 OSH 重启（2026-11-14：原先用 gpuOcUserReapplied 解除暂停，该标志启动后一直为真，导致暂停无效并刷屏日志）
      if (gpuOcGuardPaused || ShouldSkipGpuOc()) return;
      if (Interlocked.CompareExchange(ref gpuOcGuardRunning, 1, 0) != 0) return;
      Task.Run(() => {
        try {
          NVIDIA.Initialize();
          PhysicalGPU gpu = GpuAppManager.PickInternalGpu(PhysicalGPU.GetPhysicalGPUs());
          if (gpu.FullName.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) < 0) return;   // 只有 eGPU 可见时不要误操作
          GetPState20Offsets(gpu, out int core, out int mem);
          if (core == wantCore && mem == wantMem) return;
          if (core != 0 || mem != 0) return;                 // 不是清零（例如用户正在别处调整）：不干预
          DateTime now = DateTime.Now;
          gpuOcGuardResets.RemoveAll(t => (now - t).TotalHours > 2);
          gpuOcGuardResets.Add(now);
          if (gpuOcGuardResets.Count >= 2) {
            gpuOcGuardPaused = true;
            Logger.Info($"GPU hiz asirtma duraklatildi: 2 saat icinde ikinci kez sifirlandi (+{wantCore}/+{wantMem} MHz kararsiz gorunuyor)");
            if (uiContext != null) uiContext.Post(_ => ShowBalloon(Strings.GpuCoreOverclock, $"+{wantCore}/+{wantMem} MHz", ToolTipIcon.Warning), null);
            return;
          }
          if (wantCore > 0) SetCoreClockOffset(wantCore);
          if (wantMem > 0) SetMemoryClockOffset(wantMem);
          Logger.Info($"GPU hiz asirtma yeniden uygulandi (surucu sifirlanmasi sonrasi): cekirdek +{wantCore} MHz, bellek +{wantMem} MHz");
          TryDynamicBoostFix("surucu sifirlanmasi", 5000);
        } catch (Exception ex) {
          if (ex.Message.IndexOf("NOT_POWERED", StringComparison.OrdinalIgnoreCase) < 0) Logger.Error($"GPU hiz asirtma kontrolu: {ex.Message}");
        } finally {
          Interlocked.Exchange(ref gpuOcGuardRunning, 0);
        }
      });
    }
  }
}
