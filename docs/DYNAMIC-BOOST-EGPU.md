# Laptop GPU stuck at base power while an NVIDIA eGPU is attached (Dynamic Boost fix)

*Türkçe özet aşağıda.*

## Symptom

- Laptop with an NVIDIA GPU (tested: HP Victus 16, RTX 4060 Laptop) and an external NVIDIA card (tested: RTX 3090 over M.2 → OCuLink).
- With the eGPU attached, the internal GPU never gets its Dynamic Boost share. `nvidia-smi --query-gpu=enforced.power.limit,power.default_limit,power.max_limit --format=csv` shows the enforced limit pinned at the default (here 115 W) although the maximum is higher (here 140 W). FurMark holds exactly the default wattage.
- Without the eGPU the same laptop reaches the maximum.
- Things that do **not** help: other `nvpcf` driver versions, restarting or disabling the "NVIDIA Platform Controllers and Framework" device, cTGP/PPAB/Tpp settings, boot order.

## Cause

This is the behaviour described in [NVIDIA/open-gpu-kernel-modules discussion #1075](https://github.com/NVIDIA/open-gpu-kernel-modules/discussions/1075) (user analysis, not confirmed by NVIDIA): NPCF (`nvpcf.sys`) picks its Dynamic Boost target among every device whose driver service is `nvlddmkm`. The eGPU matches too, gets picked, cannot use the boost, and the internal GPU is left at base power.

## What OmenSuperHub does

`Program.DynamicBoostFix.cs` automates the workaround from that discussion. About 15 s after start-up, and again 20 s after an eGPU is detected, if the internal GPU's enforced limit is still at its default and an eGPU is present:

1. the eGPU's `Service` value under `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\…` is changed from `nvlddmkm` to `nvlddmkm_npcf_masked`;
2. the NPCF device is restarted (`pnputil /restart-device "ACPI\NVDA0820\NPCF"`), so it only sees the internal GPU;
3. after 3 s the value is restored to `nvlddmkm` (in a `finally` block).

The eGPU's driver stays loaded the whole time; running CUDA work on it was not interrupted in our tests. NPCF keeps its choice until it is re-initialised, so the fix is lost on reboot and re-applied automatically.

Measured on the test machine: enforced limit 115 W → 135–140 W, FurMark (1080p preset) 138 → 170 fps.

## Safety and limits

- Unsupported registry edit. If Windows re-enumerates the eGPU during the ~3 s window, its device node points at a non-existent service until the value is restored. On every start OmenSuperHub looks for a leftover masked value, restores it and restarts that device.
- Needs an elevated OmenSuperHub (administrators must have write access to the eGPU's Enum key; on some systems only SYSTEM does, and the fix will log an error instead).
- Gives up after three attempts without effect.
- Sleep/resume is untested; if the limit drops back, restart OmenSuperHub.
- Disable with `HKCU\Software\OmenSuperHub\DynamicBoostFix` = 0 (DWORD).
- Log lines start with `Dynamic Boost duzeltmesi`.

---

## Türkçe özet

**Belirti:** Harici NVIDIA kart (eGPU) takılıyken dizüstünün kendi ekran kartı taban gücünde kalır (örnek: 140 W yerine 115 W). eGPU yokken tam güce çıkar. Sürücü değiştirmek, NPCF aygıtını yeniden başlatmak, cTGP/PPAB ayarları işe yaramaz.

**Sebep:** NPCF, Dynamic Boost hedefini sürücü servisi `nvlddmkm` olan tüm aygıtlar arasından seçer; eGPU da eşleşir ve yanlışlıkla o seçilir.

**OmenSuperHub ne yapar:** Açılıştan yaklaşık 15 sn sonra (ve eGPU algılanınca) eGPU'nun kayıt defterindeki `Service` değerini 3 saniyeliğine değiştirir, NPCF aygıtını yeniden başlatır, değeri geri alır. eGPU'nun sürücüsü bu sırada yüklü kalır. Her yeniden başlatmada sıfırlanır, OSH kendisi tekrar uygular.

**Ölçülen:** tavan 115 W → 135–140 W, FurMark 1080p 138 → 170 fps.

**Kapatmak için:** `HKCU\Software\OmenSuperHub\DynamicBoostFix` = 0. Resmi bir yöntem değildir; yarıda kesilirse OSH bir sonraki açılışta kalan değeri geri alır.
