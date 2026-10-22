// 笔记本独显（RTX 4060 Laptop 等）一键最大功率。
// 驱动不允许软件提高笔记本 GPU 的功率上限（nvidia-smi -pl 返回 "not supported"，NVAPI 没有功率策略），
// 默认上限（本机 115 W）之上的部分（本机 +25 W，最高 140 W）只能由 Dynamic Boost 给出。
// Dynamic Boost 只在 CPU 功率低于 Tpp（BIOS 0x29 第 4 字节，本机平台默认 50 W）时生效，
// 所以游戏中 CPU 一旦超过 50 W，GPU 就停在 115 W。一键：cTGP 开、PPAB 开、dState 标准、Tpp 254 W。
using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OmenSuperHub.GpuAppManager;
using static OmenSuperHub.OmenHardware;

namespace OmenSuperHub {
  static partial class Program {
    static ToolStripMenuItem gpuCeilingStatusItem;
    static volatile int internalGpuCeilingW = 0, internalGpuMaxCeilingW = 0, internalGpuDrawW = 0;
    static int lastLoggedGpuCeilingW = 0;
    static int gpuCeilingQuerying = 0;

    static void AddGpuMaxPowerItems(ToolStripMenuItem gpuPowerMenu) {
      gpuCeilingStatusItem = new ToolStripMenuItem(Strings.GpuCeilingUnknown) { Enabled = false };
      gpuPowerMenu.DropDownItems.Add(gpuCeilingStatusItem);
      var maxItem = new ToolStripMenuItem(Strings.GpuMaxPowerApply) { ToolTipText = Strings.GpuMaxPowerTip };
      maxItem.Click += (s, e) => ApplyGpuMaxPower();
      gpuPowerMenu.DropDownItems.Add(maxItem);
      gpuPowerMenu.DropDownItems.Add(new ToolStripSeparator());
      gpuPowerMenu.DropDownOpening += (s, e) => QueryGpuCeiling("menu");
    }

    static void ApplyGpuMaxPower() {
      tgpPower = "on";
      ppabPower = "on";
      dState = "normal";
      tppPower = "254 W";
      SaveConfig("TgpPower");
      SaveConfig("PpabPower");
      SaveConfig("DState");
      SaveConfig("TppPower");
      UpdateCheckedState("tgpPowerGroup", Strings.Enable);
      UpdateCheckedState("ppabPowerGroup", Strings.Enable);
      UpdateCheckedState("dStateGroup", Strings.Standard);
      if (tppTrackBar != null && tppTrackBar.Maximum >= 254) {
        tppTrackBar.Value = 254;   // 值未变化时不会触发 ValueChanged，下面仍会直接写入 BIOS
        if (tppValueLabel != null) tppValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, "254 W");
        UpdateCheckedState("tppPowerGroup", Strings.SetTppSlider);
      }
      Task.Run(() => {
        SetGpuPowerState(true, true, 1);
        SetConcurrentTdp(254);
        Logger.Info("GPU maksimum guc: cTGP acik, PPAB acik, D1, Tpp 254 W");
        QueryGpuCeiling("maksimum");
      });
      ShowBalloon(Strings.GpuPowerControlMenu, Strings.GpuMaxPowerDone(internalGpuMaxCeilingW > 0 ? internalGpuMaxCeilingW : 140), ToolTipIcon.Info);
    }

    /// <summary>后台读取内置 GPU 当前功率上限 / 最高上限 / 实时功率，更新菜单；上限变化时写日志（用于确认 Dynamic Boost 是否生效）。</summary>
    static void QueryGpuCeiling(string reason) {
      if (System.Threading.Interlocked.CompareExchange(ref gpuCeilingQuerying, 1, 0) != 0) return;
      Task.Run(() => {
        try {
          ProcessResult r = ExecuteCommand($"nvidia-smi {NvSmiInternalTarget()}--query-gpu=enforced.power.limit,power.max_limit,power.draw --format=csv,noheader,nounits");
          if (r.ExitCode != 0) return;
          string[] p = r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Split(',');
          if (p.Length < 3) return;
          int W(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? (int)Math.Round(v) : 0;
          internalGpuCeilingW = W(p[0]);
          internalGpuMaxCeilingW = W(p[1]);
          internalGpuDrawW = W(p[2]);
          if (internalGpuCeilingW > 0 && internalGpuCeilingW != lastLoggedGpuCeilingW) {
            lastLoggedGpuCeilingW = internalGpuCeilingW;
            Logger.Info($"GPU tavani ({reason}): {internalGpuCeilingW} W (maks {internalGpuMaxCeilingW} W), cekis {internalGpuDrawW} W, CPU {CPUPower:F0} W");
          }
          if (uiContext != null) uiContext.Post(_ => {
            if (gpuCeilingStatusItem != null && internalGpuCeilingW > 0)
              gpuCeilingStatusItem.Text = Strings.GpuCeilingStatus(internalGpuCeilingW, internalGpuMaxCeilingW, internalGpuDrawW);
          }, null);
        } catch (Exception ex) {
          Logger.Error($"GPU tavani okunamadi: {ex.Message}");
        } finally {
          System.Threading.Interlocked.Exchange(ref gpuCeilingQuerying, 0);
        }
      });
    }
  }
}
