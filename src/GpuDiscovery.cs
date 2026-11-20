using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

sealed record GpuAdapter(string Name, uint Vendor, uint Device, string Luid, string? Pci, bool? Integrated, string? NvidiaUuid)
{
    public bool CanUseNvidia => NvidiaUuid is not null;
    public override string ToString() =>
        $"{Name}   ·   {Pci ?? "location unavailable"}   ·   {(CanUseNvidia ? "NVIDIA verified" : "monitor only")}";
}

static class GpuDiscovery
{
    public static async Task<string> Command(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"{executable} exited with code {process.ExitCode}.");
            await stderr.ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException($"{executable} timed out.");
        }
    }

    public static async Task<IReadOnlyList<GpuAdapter>> Detect()
    {
        var adapters = Enumerate();
        var nvidia = new List<(string Pci, string Uuid)>();
        if (!adapters.Any(a => a.Vendor == 0x10de)) return adapters;
        try
        {
            var lines = await Command("nvidia-smi", "--query-gpu=pci.bus_id,uuid", "--format=csv,noheader,nounits");
            foreach (var line in lines.Split('\n'))
            {
                var row = line.Split(',', StringSplitOptions.TrimEntries);
                if (row.Length != 2 || !row[1].StartsWith("GPU-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(row[1][4..], "D", out _)) continue;
                var parts = row[0].Split(':');
                // KMT exposes bus/device/function without a PCI domain. Refuse other domains.
                if (parts.Length == 3 && parts[0].All(c => c == '0'))
                    nvidia.Add(($"{parts[1]}:{parts[2]}".ToLowerInvariant(), row[1]));
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
        return adapters.Select(a =>
        {
            var ids = nvidia.Where(n => n.Pci == a.Pci).ToArray();
            bool unique = a.Pci is not null && adapters.Count(x => x.Pci == a.Pci) == 1;
            return a with { NvidiaUuid = a.Vendor == 0x10de && unique && ids.Length == 1 ? ids[0].Uuid : null };
        }).ToArray();
    }

    public static async Task<string> VerifyEngines(GpuAdapter adapter)
    {
        // Supply only an encoded command containing a fixed, verified LUID and the app directory.
        var module = Path.Combine(AppContext.BaseDirectory, "GpuAdmission.psm1").Replace("'", "''");
        var command = $"$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; Import-Module '{module}'; " +
            $"$samples=(Get-Counter '\\GPU Engine(*)\\Utilization Percentage').CounterSamples; " +
            $"(Get-GpuEngineUtilization $samples '{adapter.Luid}' 0).ToString([cultureinfo]::InvariantCulture)";
        return (await Command(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)))).Trim();
    }

    public static async Task<string?> VendorMemory(GpuAdapter adapter)
    {
        // Diagnostics only. The guard admits no v2 workloads until vendor hardware is qualified.
        string? provider = adapter.Vendor switch { 0x1002 => "adlx", 0x8086 => "igcl", _ => null };
        string probe = Path.Combine(AppContext.BaseDirectory, "GpuAdmissionVendorProbe.exe");
        if (adapter.Integrated != false || provider is null || !File.Exists(probe)) return null;
        try
        {
            var data = JsonNode.Parse(await Command(probe, provider, adapter.Luid));
            if (AdmissionFiles.Value(data, "adapter_luid") != adapter.Luid ||
                AdmissionFiles.Value(data, "provider") != provider ||
                data?["free"] is not JsonValue freeValue || data["total"] is not JsonValue totalValue ||
                !freeValue.TryGetValue<ulong>(out var free) || !totalValue.TryGetValue<ulong>(out var total) ||
                total == 0 || free > total) return null;
            return provider == "adlx"
                ? $"ADLX reports {free:N0} / {total:N0} MB free"
                : $"IGCL reports {free / 1048576:N0} / {total / 1048576:N0} MiB free";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { return null; }
    }

    public static string Key(GpuAdapter adapter) => adapter.NvidiaUuid ??
        $"pci-{adapter.Vendor:x4}-{adapter.Device:x4}-{adapter.Pci?.Replace(':', '-').Replace('.', '-')}";

    public static JsonObject Configuration(string root, GpuAdapter adapter, JsonObject consumers) => new()
    {
        ["root"] = root,
        ["gpu"] = Key(adapter),
        ["protocol"] = adapter.CanUseNvidia ? AdmissionFiles.V1 : AdmissionFiles.V2,
        ["telemetry"] = adapter.CanUseNvidia ? "nvidia" : "monitor-only",
        ["adapter_pci"] = adapter.Pci,
        ["adapter_vendor"] = adapter.Vendor,
        ["adapter_device"] = adapter.Device,
        ["windows_engine_luid"] = adapter.Luid,
        ["windows_physical_index"] = 0,
        ["consumers"] = consumers
    };

    enum AdapterProperty : uint
    {
        InstanceLuid = 0, DriverDescription = 2, HardwareId = 3,
        IsHardware = 11, IsIntegrated = 12, PhysicalAdapterCount = 15
    }

    static List<GpuAdapter> Enumerate()
    {
        var factoryId = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
        var listId = new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
        var adapterId = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        var graphicsAttribute = new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
        Marshal.ThrowExceptionForHR(DXCoreCreateAdapterFactory(ref factoryId, out var factory));
        try
        {
            Marshal.ThrowExceptionForHR(Method<CreateAdapterList>(factory, 3)(factory, 1, ref graphicsAttribute, ref listId, out var list));
            try
            {
                var result = new List<GpuAdapter>();
                var count = Method<GetCount>(list, 4)(list);
                var get = Method<GetAdapter>(list, 3);
                for (uint index = 0; index < count; index++)
                {
                    Marshal.ThrowExceptionForHR(get(list, index, ref adapterId, out var adapter));
                    try
                    {
                        if (Property<byte>(adapter, AdapterProperty.IsHardware) != 1 ||
                            Property<uint>(adapter, AdapterProperty.PhysicalAdapterCount) is > 1 ||
                            Property<Luid>(adapter, AdapterProperty.InstanceLuid) is not { } luid ||
                            Property<HardwareId>(adapter, AdapterProperty.HardwareId) is not { } id)
                            continue;
                        bool? integrated = Property<byte>(adapter, AdapterProperty.IsIntegrated) is { } flag ? flag != 0 : null;
                        result.Add(new(Description(adapter), id.Vendor, id.Device, luid.ToString(), BusAddress(luid), integrated, null));
                    }
                    finally { Marshal.Release(adapter); }
                }
                return result;
            }
            finally { Marshal.Release(list); }
        }
        finally { Marshal.Release(factory); }
    }

    static T? Property<T>(IntPtr adapter, AdapterProperty property) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return Method<GetProperty>(adapter, 6)(adapter, property, (nuint)size, buffer) == 0
                ? Marshal.PtrToStructure<T>(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string Description(IntPtr adapter)
    {
        const string fallback = "Windows GPU";
        if (Method<GetPropertySize>(adapter, 7)(adapter, AdapterProperty.DriverDescription, out var size) != 0 ||
            size is 0 or > 4096) return fallback;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return Method<GetProperty>(adapter, 6)(adapter, AdapterProperty.DriverDescription, size, buffer) == 0
                ? Marshal.PtrToStringUTF8(buffer, (int)size).TrimEnd('\0') : fallback;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string? BusAddress(Luid luid)
    {
        var adapter = new OpenAdapter { Luid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref adapter) != 0) return null;
        var buffer = Marshal.AllocHGlobal(12);
        try
        {
            var info = new AdapterQuery { Handle = adapter.Handle, Type = 6, Data = buffer, Length = 12 };
            if (D3DKMTQueryAdapterInfo(ref info) != 0) return null;
            return $"{Marshal.ReadInt32(buffer, 0):x2}:{Marshal.ReadInt32(buffer, 4):x2}.{Marshal.ReadInt32(buffer, 8):x}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            D3DKMTCloseAdapter(ref adapter.Handle);
        }
    }

    static T Method<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    // IDXCoreAdapterFactory, IDXCoreAdapterList and IDXCoreAdapter use the SDK's IUnknown vtable.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateAdapterList(IntPtr instance, uint count, ref Guid attribute, ref Guid iid, out IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint GetCount(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetAdapter(IntPtr instance, uint index, ref Guid iid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetProperty(IntPtr instance, AdapterProperty property, nuint size, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetPropertySize(IntPtr instance, AdapterProperty property, out nuint size);
    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint Low;
        public int High;
        public override readonly string ToString() => $"0x{High:x8}_0x{Low:x8}";
    }
    [StructLayout(LayoutKind.Sequential)]
    struct HardwareId { public uint Vendor, Device, Subsystem, Revision; }
    [StructLayout(LayoutKind.Sequential)]
    struct OpenAdapter { public Luid Luid; public uint Handle; }
    [StructLayout(LayoutKind.Sequential)]
    struct AdapterQuery { public uint Handle, Type; public IntPtr Data; public uint Length; }
    [DllImport("dxcore.dll", ExactSpelling = true)]
    static extern int DXCoreCreateAdapterFactory(ref Guid iid, out IntPtr factory);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter adapter);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern int D3DKMTQueryAdapterInfo(ref AdapterQuery query);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern int D3DKMTCloseAdapter(ref uint handle);
}
