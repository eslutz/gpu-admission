# GPU qualification

The tray selects a Windows hardware adapter through DXCore and uses its LUID for the GPU Engine counters. NVIDIA profiles use the NVIDIA GPU UUID and a uniquely matching PCI address. Non-NVIDIA profiles use `gpu-admission/v2` with a distinct hardware ID and stay in Hold.

## Build optional vendor probes

Use the pinned vendor SDK revisions below. From an x64 Visual Studio Native Tools shell, run:

```powershell
.\native\Build-VendorProbe.ps1 -AdlxSdk <ADLX-checkout> -IgclSdk <IGCL-checkout> -Destination out
```

| Provider | SDK revision | Memory reading |
| --- | --- | --- |
| AMD ADLX | [32b5a740](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/32b5a740d42295c5dfe9026b9f52683da0f3af91) | Subtracts adapter VRAM used from total VRAM. |
| Intel IGCL | [1db55c36](https://github.com/intel/drivers.gpu.control-library/tree/1db55c364b15649427a71a05d950ab9c1eaf9a07) | Reads total and free device memory from one unambiguous local module. |

Place `GpuAdmissionVendorProbe.exe` next to your development build of the tray. **Verify readings** in setup or repair will display the result if the driver reports valid memory for the exact dedicated adapter. For a manual probe, pass `adlx` or `igcl` and a Windows adapter LUID in the format `0x00000000_0x00001234`. The probe exits with code 3 if the driver cannot provide a reading.

## Qualify an adapter

- Record the GPU model, vendor device ID, PCI address, Windows build, and driver version.
- Verify the driver provider and the Windows counters read the same adapter with both idle and busy samples.
- Allocate and release a known amount of dedicated memory and compare the reported free memory before, during, and afterward.
- Restart and verify the saved hardware identity resolves to the current Windows LUID. Repeat the measurement with multiple adapters present when possible.
- Run a native job tied to that adapter and test Hold, stale telemetry, and recovery from a worker crash.

[A DXGI process memory budget](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo) describes a process’s memory allowance. Admission needs dedicated device memory from the selected adapter. Integrated adapters remain monitor-only. Qualify each Docker GPU runtime separately. [Docker Desktop GPU support](https://docs.docker.com/desktop/features/gpu/)
