#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cstdio>
#include <cwchar>
#include <vector>

using Microsoft::WRL::ComPtr;

int wmain(int argc, wchar_t** argv)
{
    unsigned high = 0, low = 0;
    int chars = 0;
    if (argc != 3 || std::swscanf(argv[1], L"0x%8x_0x%8x%n", &high, &low, &chars) != 2 ||
        chars != 21 || argv[1][chars] != L'\0')
        return 2;

    LUID luid{low, static_cast<LONG>(high)};
    ComPtr<IDXGIFactory4> factory;
    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) ||
        FAILED(factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter))))
        return 3;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &device, nullptr, &context)))
        return 4;

    D3D11_BUFFER_DESC description{};
    description.Usage = D3D11_USAGE_DEFAULT;
    description.ByteWidth = 128 * 1024 * 1024;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    description.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    std::vector<unsigned char> bytes(description.ByteWidth, 17);
    D3D11_SUBRESOURCE_DATA initial{};
    initial.pSysMem = bytes.data();
    ComPtr<ID3D11Buffer> from, to;
    if (FAILED(device->CreateBuffer(&description, &initial, &from)) ||
        FAILED(device->CreateBuffer(&description, &initial, &to)))
        return 5;
    ComPtr<ID3D11Query> done;
    const D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
    if (FAILED(device->CreateQuery(&query, &done)))
        return 6;

    auto report = _wfopen(argv[2], L"w");
    if (!report)
        return 7;
    std::fprintf(report, "%lu\n", GetCurrentProcessId());
    std::fclose(report);

    for (const ULONGLONG end = GetTickCount64() + 18000; GetTickCount64() < end;) {
        context->CopyResource(to.Get(), from.Get());
        context->End(done.Get());
        context->Flush();
        const ULONGLONG deadline = GetTickCount64() + 2000;
        HRESULT status;
        do {
            Sleep(3);
            status = context->GetData(done.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        } while (status == S_FALSE && GetTickCount64() < deadline);
        if (status != S_OK)
            return 8;
        Sleep(16);
    }
    return 0;
}
