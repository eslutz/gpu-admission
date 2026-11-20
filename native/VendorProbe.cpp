#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <type_traits>
#include <vector>
#include <ADLX.h>
#include <ADLXVersion.h>
#include <ISystem2.h>
#include <IPerformanceMonitoring.h>
#include <igcl_api.h>

struct MemorySample {
    uint64_t total = 0;
    uint64_t free = 0;
};

static bool AmdMemory(adlx::IADLXSystem* system, LUID wanted, MemorySample& result)
{
    using namespace adlx;
    IADLXGPUListPtr adapters;
    if (system->GetGPUs(&adapters) != ADLX_OK || !adapters)
        return false;
    IADLXGPUPtr selected;
    for (adlx_uint index = 0; index < adapters->Size(); ++index) {
        IADLXGPUPtr gpu;
        if (adapters->At(index, &gpu) != ADLX_OK || !gpu)
            continue;
        IADLXGPU2Ptr gpu2(gpu);
        ADLX_LUID luid = {};
        if (!gpu2 || gpu2->LUID(&luid) != ADLX_OK ||
            luid.lowPart != wanted.LowPart || luid.highPart != wanted.HighPart)
            continue;
        if (selected)
            return false;
        selected = gpu;
    }
    ADLX_GPU_TYPE type;
    adlx_uint total = 0;
    if (!selected || selected->Type(&type) != ADLX_OK || type != GPUTYPE_DISCRETE ||
        selected->TotalVRAM(&total) != ADLX_OK || total == 0)
        return false;
    IADLXPerformanceMonitoringServicesPtr service;
    IADLXGPUMetricsSupportPtr support;
    adlx_bool gpuVram = false;
    IADLXGPUMetricsPtr metrics;
    adlx_int used = -1;
    if (system->GetPerformanceMonitoringServices(&service) != ADLX_OK || !service ||
        service->GetSupportedGPUMetrics(selected, &support) != ADLX_OK || !support ||
        support->IsSupportedGPUVRAM(&gpuVram) != ADLX_OK || !gpuVram ||
        service->GetCurrentGPUMetrics(selected, &metrics) != ADLX_OK || !metrics ||
        metrics->GPUVRAM(&used) != ADLX_OK || used < 0 || static_cast<adlx_uint>(used) > total)
        return false;
    // ADLX reports the device figures in megabytes. Keep the driver's units for qualification.
    result = {total, total - static_cast<adlx_uint>(used)};
    return true;
}

static bool SampleAmd(LUID wanted, MemorySample& result)
{
    const auto release = [](HINSTANCE module) { FreeLibrary(module); };
    std::unique_ptr<std::remove_pointer_t<HMODULE>, decltype(release)> module(
        LoadLibraryExW(ADLX_DLL_NAMEW, nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32), release);
    if (!module)
        return false;
    const auto initialize = reinterpret_cast<ADLXInitialize_Fn>(GetProcAddress(module.get(), ADLX_INIT_FUNCTION_NAME));
    const auto terminate = reinterpret_cast<ADLXTerminate_Fn>(GetProcAddress(module.get(), ADLX_TERMINATE_FUNCTION_NAME));
    adlx::IADLXSystem* system = nullptr;
    if (!initialize || !terminate)
        return false;
    if (initialize(ADLX_FULL_VERSION, &system) != ADLX_OK)
        return false;
    const bool available = system && AmdMemory(system, wanted, result);
    return terminate() == ADLX_OK && available;
}

static bool IntelMemory(ctl_api_handle_t api, LUID wanted, MemorySample& result)
{
    uint32_t count = 0;
    if (ctlEnumerateDevices(api, &count, nullptr) != CTL_RESULT_SUCCESS || count == 0 || count > 64)
        return false;
    std::vector<ctl_device_adapter_handle_t> adapters(count);
    if (ctlEnumerateDevices(api, &count, adapters.data()) != CTL_RESULT_SUCCESS || count > adapters.size())
        return false;
    ctl_device_adapter_handle_t selected = nullptr;
    for (uint32_t index = 0; index < count; ++index) {
        LUID luid{};
        ctl_device_adapter_properties_t properties{};
        properties.Size = sizeof(properties);
        properties.pDeviceID = &luid;
        properties.device_id_size = sizeof(luid);
        if (ctlGetDeviceProperties(adapters[index], &properties) != CTL_RESULT_SUCCESS ||
            properties.device_type != CTL_DEVICE_TYPE_GRAPHICS || properties.pci_vendor_id != 0x8086 ||
            luid.LowPart != wanted.LowPart || luid.HighPart != wanted.HighPart)
            continue;
        if (selected || (properties.graphics_adapter_properties & CTL_ADAPTER_PROPERTIES_FLAG_INTEGRATED))
            return false;
        selected = adapters[index];
    }
    count = 0;
    if (!selected || ctlEnumMemoryModules(selected, &count, nullptr) != CTL_RESULT_SUCCESS || count == 0 || count > 64)
        return false;
    std::vector<ctl_mem_handle_t> modules(count);
    if (ctlEnumMemoryModules(selected, &count, modules.data()) != CTL_RESULT_SUCCESS || count > modules.size())
        return false;
    for (uint32_t index = 0; index < count; ++index) {
        ctl_mem_properties_t properties{};
        properties.Size = sizeof(properties);
        if (ctlMemoryGetProperties(modules[index], &properties) != CTL_RESULT_SUCCESS)
            return false;
        if (properties.location != CTL_MEM_LOC_DEVICE)
            continue;
        // Driver modules can represent overlapping memory. Qualify only an unambiguous single local module.
        ctl_mem_state_t state{};
        state.Size = sizeof(state);
        if (result.total || ctlMemoryGetState(modules[index], &state) != CTL_RESULT_SUCCESS ||
            state.size == 0 || state.free > state.size ||
            (properties.physicalSize && state.size > properties.physicalSize))
            return false;
        result = {state.size, state.free};
    }
    return result.total > 0;
}

static bool SampleIntel(LUID wanted, MemorySample& result)
{
    ctl_init_args_t settings{};
    settings.Size = sizeof(settings);
    settings.AppVersion = CTL_IMPL_VERSION;
    settings.flags = CTL_INIT_FLAG_USE_LEVEL_ZERO;
    ctl_api_handle_t api = nullptr;
    if (ctlInit(&settings, &api) != CTL_RESULT_SUCCESS || !api)
        return false;
    const bool available = IntelMemory(api, wanted, result);
    return ctlClose(api) == CTL_RESULT_SUCCESS && available;
}

int main(int argc, char** argv)
{
    unsigned high = 0, low = 0;
    int consumed = 0;
    if (argc != 3 || std::strlen(argv[2]) != 21 ||
        std::sscanf(argv[2], "0x%8x_0x%8x%n", &high, &low, &consumed) != 2 || consumed != 21 ||
        !SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32))
        return 2;
    LUID luid{low, static_cast<LONG>(high)};
    MemorySample memory;
    const bool amd = std::strcmp(argv[1], "adlx") == 0;
    if (!amd && std::strcmp(argv[1], "igcl") != 0)
        return 2;
    if (!(amd ? SampleAmd(luid, memory) : SampleIntel(luid, memory)))
        return 3;
    std::printf("{\"adapter_luid\":\"%s\",\"provider\":\"%s\",\"total\":%llu,\"free\":%llu}\n",
        argv[2], argv[1], static_cast<unsigned long long>(memory.total),
        static_cast<unsigned long long>(memory.free));
    return 0;
}
