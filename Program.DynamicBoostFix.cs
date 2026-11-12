// eGPU 接入时内置独显 Dynamic Boost 失效的修复。
// 现象：外接 NVIDIA 显卡在场时，NPCF（nvpcf.sys）把 Dynamic Boost 给错了显卡，内置独显停在基础功率（本机 115 W，而不是 140 W）。
// 参考：github.com/NVIDIA/open-gpu-kernel-modules/discussions/1075（用户分析，NVIDIA 未确认）。NPCF 按 Service == "nvlddmkm" 选卡，
// 两张卡都匹配。做法：把 eGPU 设备节点的 Service 值临时改成不匹配的字符串 → 重启 NPCF 设备（它只看到内置独显）→ 立刻把值改回。
// eGPU 的驱动在整个过程中保持加载。NPCF 重新初始化（重启、设备重启）后会失效，所以开机和 eGPU 重新出现时各做一次。
// 安全：值只在几秒内被改动，finally 里必定恢复；OSH 启动时先检查并恢复任何残留的屏蔽值。
// 注册表 DynamicBoostFix = 0 可关闭。
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using static OmenSuperHub.GpuAppManager;

namespace OmenSuperHub {
  static partial class Program {
    const string DbFixMaskedService = "nvlddmkm_npcf_masked";
    const string DbFixNpcfInstance = @"ACPI\NVDA0820\NPCF";
    const string DbFixEnumRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";
    static int dbFixRunning = 0, dbFixFailures = 0;
    static DateTime dbFixLastAttempt = DateTime.MinValue;

    static bool DbFixEnabled() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\OmenSuperHub")) {
          object raw = key?.GetValue("DynamicBoostFix");
          if (raw != null) return Convert.ToInt32(raw) != 0;
        }
      } catch { }
      return true;
    }

    /// <summary>启动时调用：若上次运行在屏蔽窗口内中断，把残留的屏蔽值恢复并重启该设备。</summary>
    static void DbFixRecover() {
      try {
        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(DbFixEnumRoot)) {
          if (root == null) return;
          foreach (string dev in root.GetSubKeyNames()) {
            if (!dev.StartsWith("VEN_10DE", StringComparison.OrdinalIgnoreCase)) continue;
            using (RegistryKey devKey = root.OpenSubKey(dev)) {
              if (devKey == null) continue;
              foreach (string inst in devKey.GetSubKeyNames()) {
                string path = $@"{DbFixEnumRoot}\{dev}\{inst}";
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path, true)) {
                  if (k == null || !string.Equals(k.GetValue("Service") as string, DbFixMaskedService, StringComparison.OrdinalIgnoreCase)) continue;
                  k.SetValue("Service", "nvlddmkm", RegistryValueKind.String);
                  Logger.Info($"Dynamic Boost duzeltmesi: kalan maske geri alindi ({dev}\\{inst})");
                  ExecuteCommand($"pnputil.exe /restart-device \"PCI\\{dev}\\{inst}\"");
                }
              }
            }
          }
        }
      } catch (Exception ex) {
        Logger.Error($"Dynamic Boost duzeltmesi (kurtarma): {ex.Message}");
      }
    }

    /// <summary>当前在场的 eGPU 的 Enum 子键路径（按 nvidia-smi 报告的 PCI 总线号匹配 LocationInformation），找不到返回 null。</summary>
    static string DbFixFindEgpuKey() {
      string busId = egpuPowerBusId;                       // 例如 00000000:09:00.0
      if (string.IsNullOrEmpty(busId)) return null;
      string[] parts = busId.Split(':');
      if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bus)) return null;
      string wanted = $"({bus},0,0)";                     // LocationInformation 末尾是 (总线,设备,功能)
      // 只看驱动当前实际绑定的实例（Services\nvlddmkm\Enum），Enum 下还留着旧的子系统 ID 实例
      using (RegistryKey active = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Enum")) {
        if (active == null) return null;
        string found = null;
        foreach (string name in active.GetValueNames()) {
          string id = active.GetValue(name) as string;
          if (id == null || !id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase)) continue;
          string path = $@"SYSTEM\CurrentControlSet\Enum\{id}";
          using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path)) {
            if (k == null) continue;
            if (!string.Equals(k.GetValue("Service") as string, "nvlddmkm", StringComparison.OrdinalIgnoreCase)) continue;
            string desc = (k.GetValue("DeviceDesc") as string) ?? "";
            if (desc.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            string loc = (k.GetValue("LocationInformation") as string) ?? "";
            if (!loc.TrimEnd().EndsWith(wanted, StringComparison.Ordinal)) continue;
            if (found != null) return null;                // 不唯一就不动
            found = path;
          }
        }
        return found;
      }
    }

    static int dbFixDefaultW = 0;   // 基础上限（无 Dynamic Boost 时的值）

    static bool DbFixReadCeiling(out int limit, out int max) {
      limit = 0; max = 0;
      ProcessResult r = ExecuteCommand($"nvidia-smi {NvSmiInternalTarget()}--query-gpu=enforced.power.limit,power.max_limit,power.default_limit --format=csv,noheader,nounits");
      if (r.ExitCode != 0) return false;
      string[] lines = r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length == 0) return false;
      string[] p = lines[0].Split(',');
      if (p.Length < 2) return false;
      int W(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? (int)Math.Round(v) : 0;
      limit = W(p[0]); max = W(p[1]);
      if (p.Length > 2) dbFixDefaultW = W(p[2]);
      return limit > 0 && max > 0;
    }

    /// <summary>内置独显上限低于最高值且 eGPU 在场时执行一次修复。后台运行；连续 3 次无效后不再尝试。</summary>
    static void TryDynamicBoostFix(string reason, int delayMs = 0) {
      if (!DbFixEnabled() || dbFixFailures >= 3) return;
      if (Interlocked.CompareExchange(ref dbFixRunning, 1, 0) != 0) return;
      Task.Run(() => {
        try {
          if (delayMs > 0) Thread.Sleep(delayMs);
          if ((DateTime.Now - dbFixLastAttempt).TotalSeconds < 60) return;
          if (!egpuPowerAvailable || string.IsNullOrEmpty(egpuPowerBusId)) return;      // 没有 eGPU：不是这个问题
          if (!DbFixReadCeiling(out int before, out int max) || before >= max) return;  // 已经是最高上限
          if (dbFixDefaultW > 0 && before > dbFixDefaultW) return;                      // 高于基础值 = Dynamic Boost 已在工作（上限随 CPU 浮动）
          string keyPath = DbFixFindEgpuKey();
          if (keyPath == null) { Logger.Info($"Dynamic Boost duzeltmesi ({reason}): eGPU kayit anahtari tek olarak bulunamadi, dokunulmadi"); dbFixFailures++; return; }
          dbFixLastAttempt = DateTime.Now;
          using (RegistryKey k = Registry.LocalMachine.OpenSubKey(keyPath, true)) {
            if (k == null) throw new InvalidOperationException("eGPU anahtari yazmaya acilamadi");
            try {
              k.SetValue("Service", DbFixMaskedService, RegistryValueKind.String);
              k.Flush();
              ProcessResult r = ExecuteCommand($"pnputil.exe /restart-device \"{DbFixNpcfInstance}\"");
              if (r.ExitCode != 0) Logger.Error($"Dynamic Boost duzeltmesi: NPCF yeniden baslatilamadi ({r.ExitCode}) {(r.Output + " " + r.Error).Trim()}");
              Thread.Sleep(3000);
            } finally {
              k.SetValue("Service", "nvlddmkm", RegistryValueKind.String);
              k.Flush();
            }
          }
          Thread.Sleep(1500);
          DbFixReadCeiling(out int after, out max);
          if (after > before) { dbFixFailures = 0; Logger.Info($"Dynamic Boost duzeltmesi ({reason}): dahili GPU tavani {before} W -> {after} W (maks {max} W)"); }
          else { dbFixFailures++; Logger.Info($"Dynamic Boost duzeltmesi ({reason}): etkisiz, tavan {after} W (maks {max} W), deneme {dbFixFailures}/3"); }
          QueryGpuCeiling("DB duzeltmesi");
        } catch (Exception ex) {
          dbFixFailures++;
          Logger.Error($"Dynamic Boost duzeltmesi ({reason}): {ex.Message}");
        } finally {
          Interlocked.Exchange(ref dbFixRunning, 0);
        }
      });
    }
  }
}
