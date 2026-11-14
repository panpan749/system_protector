#define NOMINMAX
#include <windows.h>
#include <wtsapi32.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <chrono>
#include <string>
#include <thread>
#include <vector>
#include <iostream>
#include <sstream>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <atomic>
#include <memory>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

// Exit codes: 2 arguments, 3 identity/visibility changed, 4 no usable frame,
// 5 inaccessible desktop, 6 capture unsupported/failed. Never capture a display.
static bool DesktopAvailable()
{
    DWORD sessionId=0, bytes=0; LPWSTR buffer=nullptr;
    if(!ProcessIdToSessionId(GetCurrentProcessId(),&sessionId) ||
        !WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE,sessionId,WTSSessionInfoEx,&buffer,&bytes)) return false;
    bool unlocked=false;
    if(bytes>=sizeof(WTSINFOEXW)) {
        auto info=reinterpret_cast<WTSINFOEXW*>(buffer);
        unlocked=info->Level==1 && info->Data.WTSInfoExLevel1.SessionState==WTSActive && info->Data.WTSInfoExLevel1.SessionFlags==WTS_SESSIONSTATE_UNLOCK;
    }
    WTSFreeMemory(buffer); if(!unlocked) return false;
    auto desktop=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS); if(!desktop) return false;
    wchar_t name[128]{}; DWORD needed=0;
    bool okay=GetUserObjectInformationW(desktop,UOI_NAME,name,sizeof(name),&needed) && wcscmp(name,L"Default")==0;
    CloseDesktop(desktop); return okay;
}
static bool SameWindow(HWND window,DWORD expectedPid,ULONGLONG expectedCreated)
{
    DWORD pid=0; GetWindowThreadProcessId(window,&pid); int cloaked=0;
    if(pid!=expectedPid || !IsWindowVisible(window) || IsIconic(window) || GetAncestor(window,GA_ROOT)!=window ||
        (SUCCEEDED(DwmGetWindowAttribute(window,DWMWA_CLOAKED,&cloaked,sizeof(cloaked))) && cloaked)) return false;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid); if(!process) return false;
    FILETIME created{},exited{},kernel{},user{};
    bool same=GetProcessTimes(process,&created,&exited,&kernel,&user) &&
        ((static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime)==expectedCreated;
    CloseHandle(process); return same;
}
static bool HasContent(const std::vector<BYTE>& pixels,UINT width,UINT height)
{
    size_t opaque=0,total=0; int low=255,high=0;
    for(UINT y=0;y<height;y+=3) for(UINT x=0;x<width;x+=3) {
        auto p=&pixels[(static_cast<size_t>(y)*width+x)*4]; ++total;
        if(p[3]<64) continue;
        ++opaque; int value=(static_cast<int>(p[0])+p[1]+p[2])/3;
        low=std::min(low,value); high=std::max(high,value);
    }
    return opaque>=std::max<size_t>(32,total/100) && high-low>=16;
}
static void WritePng(const wchar_t* path,const std::vector<BYTE>& pixels,UINT width,UINT height)
{
    auto factory=create_instance<IWICImagingFactory>(CLSID_WICImagingFactory);
    com_ptr<IWICStream> stream; check_hresult(factory->CreateStream(stream.put()));
    check_hresult(stream->InitializeFromFilename(path,GENERIC_WRITE));
    com_ptr<IWICBitmapEncoder> encoder; check_hresult(factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,encoder.put()));
    check_hresult(encoder->Initialize(stream.get(),WICBitmapEncoderNoCache));
    com_ptr<IWICBitmapFrameEncode> frame; check_hresult(encoder->CreateNewFrame(frame.put(),nullptr));
    check_hresult(frame->Initialize(nullptr)); check_hresult(frame->SetSize(width,height));
    auto format=GUID_WICPixelFormat32bppBGRA; check_hresult(frame->SetPixelFormat(&format));
    if(format!=GUID_WICPixelFormat32bppBGRA) throw_hresult(E_FAIL);
    check_hresult(frame->WritePixels(height,width*4,static_cast<UINT>(pixels.size()),const_cast<BYTE*>(pixels.data())));
    check_hresult(frame->Commit()); check_hresult(encoder->Commit());
}
struct GraphicsDevice {
    com_ptr<ID3D11Device> device; com_ptr<ID3D11DeviceContext> context; IDirect3DDevice projected{nullptr};
    GraphicsDevice() {
        HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,context.put());
        if(FAILED(hr)) check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,context.put()));
        auto dxgi=device.as<IDXGIDevice>(); com_ptr<::IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(),inspectable.put())); projected=inspectable.as<IDirect3DDevice>();
    }
};
struct Request { uint64_t id=0,handle=0,created=0; DWORD pid=0; std::wstring title,klass,path; };
struct SessionScope {
    Direct3D11CaptureFramePool pool{nullptr}; GraphicsCaptureSession session{nullptr};
    ~SessionScope() { try { if(session) session.Close(); if(pool) pool.Close(); } catch(...) {} }
};
static bool Identity(const Request& r) {
    if(!SameWindow(reinterpret_cast<HWND>(r.handle),r.pid,r.created)) return false;
    wchar_t title[1024]{},klass[256]{};
    GetWindowTextW(reinterpret_cast<HWND>(r.handle),title,1024); GetClassNameW(reinterpret_cast<HWND>(r.handle),klass,256);
    return r.title==title&&r.klass==klass;
}
static int Capture(GraphicsDevice& graphics,const Request& r,int64_t& capturedAt)
{
    try {
        auto window=reinterpret_cast<HWND>(r.handle);
        if(!DesktopAvailable()) return 5;
        if(!Identity(r)) return 3;
        if(!GraphicsCaptureSession::IsSupported()) return 6;
        auto interop=get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{nullptr}; check_hresult(interop->CreateForWindow(window,guid_of<GraphicsCaptureItem>(),put_abi(item)));
        auto size=item.Size();
        if(size.Width<20 || size.Height<20 || static_cast<int64_t>(size.Width)*size.Height>12000000) return 3;
        auto& device=graphics.device; auto& context=graphics.context;
        SessionScope scope;
        auto pool=scope.pool=Direct3D11CaptureFramePool::CreateFreeThreaded(graphics.projected,DirectXPixelFormat::B8G8R8A8UIntNormalized,2,size);
        auto session=scope.session=pool.CreateCaptureSession(item);
        if(auto options=session.try_as<IGraphicsCaptureSession2>()) options.IsCursorCaptureEnabled(false);
        session.StartCapture();
        auto started=std::chrono::steady_clock::now(); std::vector<BYTE> candidate; UINT width=0,height=0;
        while(std::chrono::steady_clock::now()-started<std::chrono::milliseconds(1100)) {
            if(!Identity(r)) return 3;
            if(!DesktopAvailable()) return 5;
            if(auto frame=pool.TryGetNextFrame()) {
                auto content=frame.ContentSize();
                if(content.Width!=size.Width || content.Height!=size.Height) { frame.Close(); return 3; }
                auto access=frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                com_ptr<ID3D11Texture2D> texture; check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),texture.put_void()));
                D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
                if(desc.Width<static_cast<UINT>(size.Width)||desc.Height<static_cast<UINT>(size.Height)) { frame.Close(); continue; }
                desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=0; desc.MiscFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                com_ptr<ID3D11Texture2D> staging; check_hresult(device->CreateTexture2D(&desc,nullptr,staging.put()));
                context->CopyResource(staging.get(),texture.get()); D3D11_MAPPED_SUBRESOURCE mapped{};
                check_hresult(context->Map(staging.get(),0,D3D11_MAP_READ,0,&mapped));
                width=static_cast<UINT>(size.Width); height=static_cast<UINT>(size.Height);
                std::vector<BYTE> pixels(static_cast<size_t>(width)*height*4);
                for(UINT y=0;y<height;++y) memcpy(pixels.data()+static_cast<size_t>(y)*width*4,static_cast<BYTE*>(mapped.pData)+static_cast<size_t>(y)*mapped.RowPitch,width*4);
                context->Unmap(staging.get(),0);
                frame.Close();
                if(HasContent(pixels,width,height)&&DesktopAvailable()&&Identity(r)) {
                    // Freeze the first valid frame; file encoding must not require the window to survive.
                    FILETIME utc{}; GetSystemTimePreciseAsFileTime(&utc);
                    capturedAt=(static_cast<int64_t>(utc.dwHighDateTime)<<32)|utc.dwLowDateTime;
                    candidate=std::move(pixels); break;
                }
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
        session.Close(); pool.Close();
        if(candidate.empty()) return 4;
        WritePng(r.path.c_str(),candidate,width,height); return 0;
    } catch(const hresult_error& e) { fwprintf(stderr,L"Capture HRESULT: %08X\n",static_cast<unsigned>(e.code())); return 6; }
    catch(...) { return 6; }
}
static std::wstring Decode(const std::string& value) {
    if(value=="-") return L"";
    if(value.size()%4||value.size()>65536) throw std::invalid_argument("encoding");
    std::wstring result;
    for(size_t i=0;i<value.size();i+=4) { size_t used=0; auto c=std::stoul(value.substr(i,4),&used,16); if(used!=4||!c) throw std::invalid_argument("character"); result.push_back(static_cast<wchar_t>(c)); }
    return result;
}
static int WorkerServer() {
    std::mutex gate,output; std::condition_variable wake; std::deque<Request> queue; bool stopping=false;
    std::atomic<int> ready{0},usable{0}; std::vector<std::thread> workers;
    auto reply=[&](uint64_t id,int code,int64_t stamp) { std::lock_guard<std::mutex> lock(output); std::cout<<id<<" "<<code<<" "<<stamp<<std::endl; };
    for(int i=0;i<4;++i) workers.emplace_back([&] {
        init_apartment(apartment_type::multi_threaded); std::unique_ptr<GraphicsDevice> graphics;
        try { graphics=std::make_unique<GraphicsDevice>(); ++usable; } catch(...) {}
        ++ready;
        for(;;) {
            Request request;
            { std::unique_lock<std::mutex> lock(gate); wake.wait(lock,[&]{return stopping||!queue.empty();}); if(stopping) return; request=std::move(queue.front()); queue.pop_front(); }
            int64_t stamp=0; int code=graphics?Capture(*graphics,request,stamp):6; reply(request.id,code,stamp);
            if(code==6) { try { graphics=std::make_unique<GraphicsDevice>(); } catch(...) { graphics.reset(); } }
        }
    });
    while(ready<4) std::this_thread::sleep_for(std::chrono::milliseconds(5));
    std::cout<<"READY "<<usable<<std::endl;
    std::string line;
    while(std::getline(std::cin,line)) {
        if(line=="QUIT") break;
        Request request; std::string title,klass,path; std::istringstream input(line);
        if(!(input>>request.id>>request.handle>>request.pid>>request.created>>title>>klass>>path)) continue;
        try { request.title=Decode(title); request.klass=Decode(klass); request.path=Decode(path); if(request.path.empty()) throw std::invalid_argument("path"); }
        catch(...) { reply(request.id,2,0); continue; }
        { std::lock_guard<std::mutex> lock(gate); if(queue.size()>=16) { reply(request.id,7,0); continue; } queue.push_back(std::move(request)); }
        wake.notify_one();
    }
    { std::lock_guard<std::mutex> lock(gate); stopping=true; } wake.notify_all();
    for(auto& worker:workers) worker.join(); return 0;
}
int wmain(int argc,wchar_t** argv) {
    if(argc==2&&wcscmp(argv[1],L"--worker")==0) return WorkerServer();
    if(argc!=5) return 2;
    try {
        init_apartment(apartment_type::multi_threaded); GraphicsDevice graphics; Request r;
        r.handle=std::stoull(argv[1]); r.pid=static_cast<DWORD>(std::stoul(argv[2])); r.created=std::stoull(argv[3]); r.path=argv[4];
        wchar_t title[1024]{},klass[256]{}; GetWindowTextW(reinterpret_cast<HWND>(r.handle),title,1024); GetClassNameW(reinterpret_cast<HWND>(r.handle),klass,256); r.title=title; r.klass=klass;
        int64_t stamp=0; return Capture(graphics,r,stamp);
    } catch(...) { return 6; }
}
