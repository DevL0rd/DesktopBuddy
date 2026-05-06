#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <inspectable.h>
#include <unknwn.h>

#include <mutex>
#include <string>
#include <cstring>
#include <atomic>
#include <fstream>
#include <iomanip>
#include <sstream>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

namespace wgc = winrt::Windows::Graphics::Capture;
namespace wfmeta = winrt::Windows::Foundation::Metadata;
namespace wgdx = winrt::Windows::Graphics::DirectX;
namespace wgd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
    constexpr int E_DB_INVALIDARG = static_cast<int>(0x80070057);
    constexpr int E_DB_UNEXPECTED = static_cast<int>(0x8000FFFF);

    thread_local std::string g_lastError;
    std::mutex g_logMutex;
    std::string g_logPath;
    std::atomic<int> g_nextSessionId{ 1 };

    extern "C" IMAGE_DOS_HEADER __ImageBase;

    std::string Hex(uint64_t value)
    {
        std::ostringstream ss;
        ss << "0x" << std::uppercase << std::hex << value;
        return ss.str();
    }

    std::string HrHex(HRESULT hr)
    {
        std::ostringstream ss;
        ss << "0x" << std::uppercase << std::hex << static_cast<unsigned int>(hr);
        return ss.str();
    }

    const std::string& LogPath()
    {
        if (!g_logPath.empty()) return g_logPath;

        char path[MAX_PATH]{};
        DWORD length = GetModuleFileNameA(reinterpret_cast<HMODULE>(&__ImageBase), path, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            g_logPath = "DesktopBuddyRendererNative.diagnostics.log";
            return g_logPath;
        }

        std::string full(path, length);
        size_t slash = full.find_last_of("\\/");
        std::string dir = slash == std::string::npos ? "." : full.substr(0, slash);
        g_logPath = dir + "\\DesktopBuddyRendererNative.diagnostics.log";
        return g_logPath;
    }

    void NativeLog(const std::string& message)
    {
        SYSTEMTIME st{};
        GetLocalTime(&st);

        std::ostringstream line;
        line << '['
             << std::setfill('0') << std::setw(4) << st.wYear << '-'
             << std::setw(2) << st.wMonth << '-'
             << std::setw(2) << st.wDay << ' '
             << std::setw(2) << st.wHour << ':'
             << std::setw(2) << st.wMinute << ':'
             << std::setw(2) << st.wSecond << '.'
             << std::setw(3) << st.wMilliseconds
             << "] [tid " << GetCurrentThreadId() << "] "
             << message << "\n";

        std::lock_guard<std::mutex> guard(g_logMutex);
        std::ofstream file(LogPath(), std::ios::app | std::ios::binary);
        file << line.str();
        OutputDebugStringA(line.str().c_str());
    }

    void SetLastErrorText(const char* text)
    {
        g_lastError = text ? text : "";
        if (!g_lastError.empty())
            NativeLog("last-error: " + g_lastError);
    }

    void SetLastErrorText(const std::string& text)
    {
        g_lastError = text;
        if (!g_lastError.empty())
            NativeLog("last-error: " + g_lastError);
    }

    std::string HResultMessage(winrt::hresult_error const& ex)
    {
        std::wstring wide = ex.message().c_str();
        if (wide.empty()) return "WinRT call failed";

        int required = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (required <= 1) return "WinRT call failed";

        std::string result(static_cast<size_t>(required - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, result.data(), required, nullptr, nullptr);
        return result;
    }

    void EnsureApartment()
    {
        static std::once_flag once;
        std::call_once(once, []()
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            NativeLog("CoInitializeEx(MTA) -> " + HrHex(hr));
            if (hr == RPC_E_CHANGED_MODE || hr == S_FALSE)
                return;
            winrt::check_hresult(hr);
        });
    }

    wgc::GraphicsCaptureItem CreateItemForWindow(HWND hwnd)
    {
        NativeLog("CreateItemForWindow begin hwnd=" + Hex(reinterpret_cast<uint64_t>(hwnd)));
        auto factory = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::com_ptr<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem> itemAbi;
        winrt::check_hresult(factory->CreateForWindow(
            hwnd,
            winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
            itemAbi.put_void()));
        NativeLog("CreateItemForWindow returned abi=" + Hex(reinterpret_cast<uint64_t>(itemAbi.get())));
        return itemAbi.as<wgc::GraphicsCaptureItem>();
    }

    wgc::GraphicsCaptureItem CreateItemForMonitor(HMONITOR monitor)
    {
        NativeLog("CreateItemForMonitor begin monitor=" + Hex(reinterpret_cast<uint64_t>(monitor)));
        auto factory = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::com_ptr<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem> itemAbi;
        winrt::check_hresult(factory->CreateForMonitor(
            monitor,
            winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
            itemAbi.put_void()));
        NativeLog("CreateItemForMonitor returned abi=" + Hex(reinterpret_cast<uint64_t>(itemAbi.get())));
        return itemAbi.as<wgc::GraphicsCaptureItem>();
    }

    wgd3d::IDirect3DDevice CreateWinrtDevice(ID3D11Device* device)
    {
        NativeLog("CreateWinrtDevice begin d3dDevice=" + Hex(reinterpret_cast<uint64_t>(device)));
        winrt::com_ptr<IDXGIDevice> dxgiDevice;
        winrt::check_hresult(device->QueryInterface(__uuidof(IDXGIDevice), dxgiDevice.put_void()));
        NativeLog("QueryInterface IDXGIDevice -> " + Hex(reinterpret_cast<uint64_t>(dxgiDevice.get())));

        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()));
        NativeLog("CreateDirect3D11DeviceFromDXGIDevice -> " + Hex(reinterpret_cast<uint64_t>(inspectable.get())));
        return inspectable.as<wgd3d::IDirect3DDevice>();
    }

    struct Session
    {
        int Id = g_nextSessionId.fetch_add(1);
        std::mutex Mutex;
        winrt::com_ptr<ID3D11Device> Device;
        winrt::com_ptr<ID3D11DeviceContext> Context;
        winrt::com_ptr<ID3D11Texture2D> TargetTexture;
        wgd3d::IDirect3DDevice WinrtDevice{ nullptr };
        wgc::GraphicsCaptureItem Item{ nullptr };
        wgc::Direct3D11CaptureFramePool FramePool{ nullptr };
        wgc::GraphicsCaptureSession CaptureSession{ nullptr };
        winrt::event_token FrameToken{};
        winrt::event_token ClosedToken{};
        int Width = 0;
        int Height = 0;
        unsigned int Version = 0;
        unsigned int FrameCount = 0;
        unsigned int InfoCallCount = 0;
        unsigned int LastLoggedInfoMilestoneVersion = 0;
        unsigned int NullFrameCount = 0;
        unsigned int BadSizeCount = 0;
        DXGI_FORMAT TargetFormat = DXGI_FORMAT_UNKNOWN;
        unsigned int WaitingForTargetCount = 0;
        bool SourceDescLogged = false;
        bool Started = false;
        bool Closed = false;

        ~Session()
        {
            Shutdown();
        }

        int Start(ID3D11Device* d3dDevice, HWND hwnd, HMONITOR monitor)
        {
            NativeLog("session " + std::to_string(Id) +
                " Start begin d3dDevice=" + Hex(reinterpret_cast<uint64_t>(d3dDevice)) +
                " hwnd=" + Hex(reinterpret_cast<uint64_t>(hwnd)) +
                " monitor=" + Hex(reinterpret_cast<uint64_t>(monitor)));
            if (!d3dDevice)
            {
                NativeLog("session " + std::to_string(Id) + " Start failed: null d3dDevice");
                return E_DB_INVALIDARG;
            }
            EnsureApartment();

            Device.copy_from(d3dDevice);
            NativeLog("session " + std::to_string(Id) + " Device.copy_from complete");
            Device->GetImmediateContext(Context.put());
            NativeLog("session " + std::to_string(Id) + " GetImmediateContext -> " + Hex(reinterpret_cast<uint64_t>(Context.get())));
            if (!Context)
            {
                NativeLog("session " + std::to_string(Id) + " Start failed: null context");
                return E_DB_UNEXPECTED;
            }

            WinrtDevice = CreateWinrtDevice(Device.get());
            NativeLog("session " + std::to_string(Id) + " WinRT device created");

            if (hwnd)
                Item = CreateItemForWindow(hwnd);
            else
            {
                if (!monitor)
                    monitor = MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
                NativeLog("session " + std::to_string(Id) + " monitor after fallback=" + Hex(reinterpret_cast<uint64_t>(monitor)));
                Item = CreateItemForMonitor(monitor);
            }
            NativeLog("session " + std::to_string(Id) + " capture item created");

            auto size = Item.Size();
            Width = size.Width;
            Height = size.Height;
            NativeLog("session " + std::to_string(Id) + " item size=" + std::to_string(Width) + "x" + std::to_string(Height));
            if (Width <= 0 || Height <= 0)
            {
                NativeLog("session " + std::to_string(Id) + " Start failed: invalid item size");
                return E_DB_UNEXPECTED;
            }

            ClosedToken = Item.Closed([this](auto const&, auto const&)
            {
                std::lock_guard<std::mutex> guard(Mutex);
                Closed = true;
                NativeLog("session " + std::to_string(Id) + " item closed event");
            });
            NativeLog("session " + std::to_string(Id) + " closed handler attached");

            FramePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
                WinrtDevice,
                wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                size);
            NativeLog("session " + std::to_string(Id) + " frame pool created");

            FrameToken = FramePool.FrameArrived([this](wgc::Direct3D11CaptureFramePool const& sender, auto const&)
            {
                OnFrameArrived(sender);
            });
            NativeLog("session " + std::to_string(Id) + " frame handler attached");

            CaptureSession = FramePool.CreateCaptureSession(Item);
            NativeLog("session " + std::to_string(Id) + " capture session created");
            if (wfmeta::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired"))
            {
                CaptureSession.IsBorderRequired(false);
                NativeLog("session " + std::to_string(Id) + " border disabled");
            }
            else
            {
                NativeLog("session " + std::to_string(Id) + " border disable unsupported on this Windows API");
            }
            if (wfmeta::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IncludeSecondaryWindows"))
            {
                CaptureSession.IncludeSecondaryWindows(true);
                NativeLog("session " + std::to_string(Id) + " secondary windows included");
            }
            else
            {
                NativeLog("session " + std::to_string(Id) + " secondary windows unsupported on this Windows API");
            }
            CaptureSession.IsCursorCaptureEnabled(true);
            NativeLog("session " + std::to_string(Id) + " cursor capture enabled");
            CaptureSession.StartCapture();
            Started = true;
            NativeLog("session " + std::to_string(Id) + " StartCapture complete");
            return 0;
        }

        void Shutdown()
        {
            std::lock_guard<std::mutex> guard(Mutex);
            NativeLog("session " + std::to_string(Id) + " Shutdown begin");

            try
            {
                if (FramePool)
                    FramePool.FrameArrived(FrameToken);
            }
            catch (...) {}

            try
            {
                if (Item)
                    Item.Closed(ClosedToken);
            }
            catch (...) {}

            CaptureSession = nullptr;
            FramePool = nullptr;
            Item = nullptr;
            WinrtDevice = nullptr;
            TargetTexture = nullptr;
            TargetFormat = DXGI_FORMAT_UNKNOWN;
            Context = nullptr;
            Device = nullptr;
            Started = false;
            Closed = true;
            NativeLog("session " + std::to_string(Id) + " Shutdown complete");
        }

        void OnFrameArrived(wgc::Direct3D11CaptureFramePool const& sender)
        {
            std::lock_guard<std::mutex> guard(Mutex);
            if (!Started || Closed) return;

            try
            {
                auto frame = sender.TryGetNextFrame();
                if (!frame)
                {
                    NullFrameCount++;
                    if (NullFrameCount <= 10 || (NullFrameCount % 120) == 0)
                        NativeLog("session " + std::to_string(Id) + " TryGetNextFrame returned null count=" + std::to_string(NullFrameCount));
                    return;
                }

                auto size = frame.ContentSize();
                if (size.Width <= 0 || size.Height <= 0)
                {
                    BadSizeCount++;
                    NativeLog("session " + std::to_string(Id) + " bad frame size=" + std::to_string(size.Width) + "x" + std::to_string(size.Height) +
                        " count=" + std::to_string(BadSizeCount));
                    return;
                }

                if (size.Width != Width || size.Height != Height)
                {
                    NativeLog("session " + std::to_string(Id) + " resize " + std::to_string(Width) + "x" + std::to_string(Height) +
                        " -> " + std::to_string(size.Width) + "x" + std::to_string(size.Height));
                    Width = size.Width;
                    Height = size.Height;
                    SourceDescLogged = false;
                    FramePool.Recreate(
                        WinrtDevice,
                        wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                        2,
                        size);
                    return;
                }

                auto surface = frame.Surface();
                auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                winrt::com_ptr<ID3D11Texture2D> sourceTexture;
                winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), sourceTexture.put_void()));
                if (!sourceTexture)
                {
                    NativeLog("session " + std::to_string(Id) + " GetInterface returned null source texture");
                    return;
                }

                LogSourceDescOnce(sourceTexture.get());
                if (!TargetTexture)
                {
                    WaitingForTargetCount++;
                    if (WaitingForTargetCount <= 10 || (WaitingForTargetCount % 120) == 0)
                        NativeLog("session " + std::to_string(Id) + " waiting for Unity target texture count=" +
                            std::to_string(WaitingForTargetCount));
                    return;
                }

                Context->CopyResource(TargetTexture.get(), sourceTexture.get());
                Context->Flush();
                Version++;
                FrameCount++;
                if (FrameCount <= 10 || (FrameCount % 120) == 0)
                    NativeLog("session " + std::to_string(Id) +
                        " copied frame count=" + std::to_string(FrameCount) +
                        " version=" + std::to_string(Version) +
                        " size=" + std::to_string(Width) + "x" + std::to_string(Height) +
                        " src=" + Hex(reinterpret_cast<uint64_t>(sourceTexture.get())) +
                        " unityTarget=" + Hex(reinterpret_cast<uint64_t>(TargetTexture.get())) +
                        " dxgiFormat=" + std::to_string(static_cast<unsigned int>(TargetFormat)));
            }
            catch (winrt::hresult_error const& ex)
            {
                NativeLog("session " + std::to_string(Id) + " FrameArrived hresult_error hr=" +
                    HrHex(static_cast<HRESULT>(ex.code())) + " msg=" + HResultMessage(ex));
                Closed = true;
            }
            catch (std::exception const& ex)
            {
                NativeLog("session " + std::to_string(Id) + " FrameArrived std::exception " + ex.what());
                Closed = true;
            }
            catch (...)
            {
                NativeLog("session " + std::to_string(Id) + " FrameArrived unknown exception");
                Closed = true;
            }
        }

        void LogSourceDescOnce(ID3D11Texture2D* sourceTexture)
        {
            if (SourceDescLogged || !sourceTexture) return;

            D3D11_TEXTURE2D_DESC sourceDesc{};
            sourceTexture->GetDesc(&sourceDesc);
            SourceDescLogged = true;

            NativeLog("session " + std::to_string(Id) + " source texture desc " +
                std::to_string(sourceDesc.Width) + "x" + std::to_string(sourceDesc.Height) +
                " mip=" + std::to_string(sourceDesc.MipLevels) +
                " array=" + std::to_string(sourceDesc.ArraySize) +
                " format=" + std::to_string(static_cast<unsigned int>(sourceDesc.Format)) +
                " sampleCount=" + std::to_string(sourceDesc.SampleDesc.Count) +
                " sampleQuality=" + std::to_string(sourceDesc.SampleDesc.Quality) +
                " usage=" + std::to_string(sourceDesc.Usage) +
                " bind=0x" + Hex(sourceDesc.BindFlags) +
                " cpu=0x" + Hex(sourceDesc.CPUAccessFlags) +
                " misc=0x" + Hex(sourceDesc.MiscFlags));
        }

        int SetTargetTexture(ID3D11Texture2D* texture, int width, int height)
        {
            std::lock_guard<std::mutex> guard(Mutex);
            NativeLog("session " + std::to_string(Id) + " SetTargetTexture ptr=" +
                Hex(reinterpret_cast<uint64_t>(texture)) +
                " expected=" + std::to_string(width) + "x" + std::to_string(height));
            if (!texture || width <= 0 || height <= 0)
            {
                NativeLog("session " + std::to_string(Id) + " SetTargetTexture failed: invalid args");
                TargetTexture = nullptr;
                TargetFormat = DXGI_FORMAT_UNKNOWN;
                return E_DB_INVALIDARG;
            }

            D3D11_TEXTURE2D_DESC desc{};
            texture->GetDesc(&desc);
            NativeLog("session " + std::to_string(Id) + " Unity target desc " +
                std::to_string(desc.Width) + "x" + std::to_string(desc.Height) +
                " mip=" + std::to_string(desc.MipLevels) +
                " array=" + std::to_string(desc.ArraySize) +
                " format=" + std::to_string(static_cast<unsigned int>(desc.Format)) +
                " sampleCount=" + std::to_string(desc.SampleDesc.Count) +
                " sampleQuality=" + std::to_string(desc.SampleDesc.Quality) +
                " usage=" + std::to_string(desc.Usage) +
                " bind=0x" + Hex(desc.BindFlags) +
                " cpu=0x" + Hex(desc.CPUAccessFlags) +
                " misc=0x" + Hex(desc.MiscFlags));

            if (desc.Width != static_cast<UINT>(width) || desc.Height != static_cast<UINT>(height))
            {
                NativeLog("session " + std::to_string(Id) + " SetTargetTexture failed: descriptor size mismatch");
                return E_DB_INVALIDARG;
            }

            TargetTexture.copy_from(texture);
            TargetFormat = desc.Format;
            WaitingForTargetCount = 0;
            NativeLog("session " + std::to_string(Id) + " Unity target accepted");
            return 0;
        }
    };
}

extern "C"
{
    struct DbWgcFrameInfo
    {
        int Width;
        int Height;
        void* Texture;
        unsigned int Version;
        int IsValid;
        int IsClosed;
        unsigned int DxgiFormat;
    };

    __declspec(dllexport) int __cdecl DbWgcCreate(void* d3dDevice, void* hwnd, void* monitor, void** sessionOut)
    {
        NativeLog("DbWgcCreate called d3dDevice=" + Hex(reinterpret_cast<uint64_t>(d3dDevice)) +
            " hwnd=" + Hex(reinterpret_cast<uint64_t>(hwnd)) +
            " monitor=" + Hex(reinterpret_cast<uint64_t>(monitor)));
        if (!sessionOut) return E_DB_INVALIDARG;
        *sessionOut = nullptr;
        SetLastErrorText("");

        try
        {
            auto session = new Session();
            int hr = session->Start(
                static_cast<ID3D11Device*>(d3dDevice),
                static_cast<HWND>(hwnd),
                static_cast<HMONITOR>(monitor));
            if (hr < 0)
            {
                NativeLog("DbWgcCreate session " + std::to_string(session->Id) + " Start returned " + HrHex(static_cast<HRESULT>(hr)));
                delete session;
                SetLastErrorText("Native WGC session start failed");
                return hr;
            }

            *sessionOut = session;
            NativeLog("DbWgcCreate success session=" + std::to_string(session->Id) +
                " handle=" + Hex(reinterpret_cast<uint64_t>(session)));
            return 0;
        }
        catch (winrt::hresult_error const& ex)
        {
            auto message = HResultMessage(ex);
            NativeLog("DbWgcCreate hresult_error hr=" + HrHex(static_cast<HRESULT>(ex.code())) + " msg=" + message);
            SetLastErrorText(message);
            return static_cast<int>(ex.code());
        }
        catch (std::exception const& ex)
        {
            NativeLog(std::string("DbWgcCreate std::exception ") + ex.what());
            SetLastErrorText(ex.what());
            return E_DB_UNEXPECTED;
        }
        catch (...)
        {
            NativeLog("DbWgcCreate unknown exception");
            SetLastErrorText("Unknown native WGC error");
            return E_DB_UNEXPECTED;
        }
    }

    __declspec(dllexport) int __cdecl DbWgcGetFrameInfo(void* sessionHandle, DbWgcFrameInfo* info)
    {
        if (!sessionHandle || !info) return E_DB_INVALIDARG;
        auto session = static_cast<Session*>(sessionHandle);

        std::lock_guard<std::mutex> guard(session->Mutex);
        info->Width = session->Width;
        info->Height = session->Height;
        info->Texture = session->TargetTexture.get();
        info->Version = session->Version;
        info->IsValid = session->Started && !session->Closed && session->TargetTexture && session->Version > 0 ? 1 : 0;
        info->IsClosed = session->Closed ? 1 : 0;
        info->DxgiFormat = static_cast<unsigned int>(session->TargetFormat);
        session->InfoCallCount++;
        bool versionMilestone = session->Version > 0 &&
            (session->Version <= 10 || (session->Version % 120) == 0) &&
            session->Version != session->LastLoggedInfoMilestoneVersion;
        if (versionMilestone)
            session->LastLoggedInfoMilestoneVersion = session->Version;

        if (session->InfoCallCount <= 10 || (session->InfoCallCount % 120) == 0 || versionMilestone)
        {
            NativeLog("DbWgcGetFrameInfo session=" + std::to_string(session->Id) +
                " call=" + std::to_string(session->InfoCallCount) +
                " size=" + std::to_string(info->Width) + "x" + std::to_string(info->Height) +
                " tex=" + Hex(reinterpret_cast<uint64_t>(info->Texture)) +
                " version=" + std::to_string(info->Version) +
                " valid=" + std::to_string(info->IsValid) +
                " closed=" + std::to_string(info->IsClosed) +
                " dxgiFormat=" + std::to_string(info->DxgiFormat));
        }
        return 0;
    }

    __declspec(dllexport) int __cdecl DbWgcSetTargetTexture(void* sessionHandle, void* texture, int width, int height)
    {
        NativeLog("DbWgcSetTargetTexture handle=" + Hex(reinterpret_cast<uint64_t>(sessionHandle)) +
            " texture=" + Hex(reinterpret_cast<uint64_t>(texture)) +
            " size=" + std::to_string(width) + "x" + std::to_string(height));
        if (!sessionHandle) return E_DB_INVALIDARG;
        auto session = static_cast<Session*>(sessionHandle);
        return session->SetTargetTexture(static_cast<ID3D11Texture2D*>(texture), width, height);
    }

    __declspec(dllexport) void __cdecl DbWgcDestroy(void* sessionHandle)
    {
        if (!sessionHandle) return;
        NativeLog("DbWgcDestroy handle=" + Hex(reinterpret_cast<uint64_t>(sessionHandle)));
        delete static_cast<Session*>(sessionHandle);
    }

    __declspec(dllexport) int __cdecl DbWgcCopyLastError(char* buffer, int bufferLength)
    {
        if (!buffer || bufferLength <= 0) return 0;
        int copied = WideCharToMultiByte(CP_UTF8, 0, L"", -1, nullptr, 0, nullptr, nullptr);
        (void)copied;

        int count = static_cast<int>(g_lastError.size());
        if (count >= bufferLength)
            count = bufferLength - 1;
        if (count > 0)
            memcpy(buffer, g_lastError.data(), static_cast<size_t>(count));
        buffer[count] = '\0';
        return count;
    }
}
