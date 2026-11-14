#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <winrt/base.h>
#include <string>
#include <vector>
using winrt::com_ptr;
using winrt::check_hresult;
static com_ptr<ID2D1DeviceContext> drawing;
static com_ptr<IDWriteTextFormat> textFormat;
static com_ptr<IDXGISwapChain1> swapChain;
static bool changed=false;
static std::vector<HWND> windows; static UINT lifetime=30000;
static void Render(bool visible)
{
    drawing->BeginDraw(); drawing->Clear(D2D1::ColorF(0,0,0,0));
    if(visible) {
        com_ptr<ID2D1SolidColorBrush> blue,marker,white;
        check_hresult(drawing->CreateSolidColorBrush(D2D1::ColorF(22.f/255,45.f/255,63.f/255,1),blue.put()));
        check_hresult(drawing->CreateSolidColorBrush(changed?D2D1::ColorF(1,0,1,1):D2D1::ColorF(0,1,0,1),marker.put()));
        check_hresult(drawing->CreateSolidColorBrush(D2D1::ColorF(1,1,1,1),white.put()));
        drawing->FillRectangle(D2D1::RectF(0,0,720,160),blue.get());
        drawing->FillRectangle(D2D1::RectF(20,35,100,125),marker.get());
        std::wstring text=changed?L"GPU POPUP - UPDATED":L"GPU POPUP - READY";
        drawing->DrawTextW(text.c_str(),static_cast<UINT>(text.size()),textFormat.get(),D2D1::RectF(120,40,710,145),white.get());
    }
    check_hresult(drawing->EndDraw()); check_hresult(swapChain->Present(1,0));
}
static LRESULT CALLBACK WindowProc(HWND window,UINT msg,WPARAM wp,LPARAM lp)
{
    if(msg==WM_TIMER&&wp==1) { KillTimer(window,1); Render(true); return 0; }
    if(msg==WM_TIMER&&wp==2) { DestroyWindow(window); return 0; }
    if(msg==WM_APP+1) { changed=true; Render(true); return 0; }
    if(msg==WM_APP+2) { for(auto item:windows) { ShowWindow(item,SW_SHOWNOACTIVATE); SetTimer(item,2,lifetime,nullptr); } return 0; }
    if(msg==WM_KEYDOWN&&wp==VK_ESCAPE) { DestroyWindow(window); return 0; }
    if(msg==WM_DESTROY) { for(auto& item:windows) if(item==window) item=nullptr; bool alive=false; for(auto item:windows) alive=alive||item!=nullptr; if(!alive) PostQuitMessage(0); return 0; }
    return DefWindowProcW(window,msg,wp,lp);
}
int wmain(int argc,wchar_t** argv)
{
    try {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        bool blank=argc>1&&wcscmp(argv[1],L"--blank")==0;
        bool protect=argc>1&&wcscmp(argv[1],L"--protected")==0;
        bool armed=argc>1&&std::wstring(argv[1]).find(L"--armed-")==0;
        bool burst=argc>1&&wcscmp(argv[1],L"--burst")==0;
        if(armed) lifetime=static_cast<UINT>(std::stoul(std::wstring(argv[1]).substr(8)));
        if(burst) { armed=true; lifetime=500; }
        HINSTANCE instance=GetModuleHandleW(nullptr);
        WNDCLASSW wc{}; wc.lpfnWndProc=WindowProc; wc.hInstance=instance; wc.lpszClassName=L"PopupGuard.GpuFixture";
        RegisterClassW(&wc); RECT area{}; SystemParametersInfoW(SPI_GETWORKAREA,0,&area,0);
        HWND window=CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP|WS_EX_TOPMOST|WS_EX_TOOLWINDOW,wc.lpszClassName,L"PopupGuard GPU capture test",WS_POPUP,
            area.right-740,area.bottom-180,720,160,nullptr,nullptr,instance,nullptr);
        if(!window) return 2;
        windows.push_back(window);
        com_ptr<ID3D11Device> device;
        check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,nullptr));
        auto dxgi=device.as<IDXGIDevice>(); com_ptr<IDXGIAdapter> adapter; check_hresult(dxgi->GetAdapter(adapter.put()));
        com_ptr<IDXGIFactory2> factory; check_hresult(adapter->GetParent(__uuidof(IDXGIFactory2),factory.put_void()));
        DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width=720; desc.Height=160; desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1; desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount=2;
        desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;
        check_hresult(factory->CreateSwapChainForComposition(device.get(),&desc,nullptr,swapChain.put()));
        com_ptr<IDCompositionDevice> composition; check_hresult(DCompositionCreateDevice(dxgi.get(),__uuidof(IDCompositionDevice),composition.put_void()));
        com_ptr<IDCompositionTarget> target; check_hresult(composition->CreateTargetForHwnd(window,TRUE,target.put()));
        com_ptr<IDCompositionVisual> visual; check_hresult(composition->CreateVisual(visual.put()));
        check_hresult(visual->SetContent(swapChain.get())); check_hresult(target->SetRoot(visual.get())); check_hresult(composition->Commit());
        com_ptr<ID2D1Factory1> d2d; D2D1_FACTORY_OPTIONS options{};
        check_hresult(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),&options,d2d.put_void()));
        com_ptr<ID2D1Device> d2dDevice; check_hresult(d2d->CreateDevice(dxgi.get(),d2dDevice.put()));
        check_hresult(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,drawing.put()));
        com_ptr<IDXGISurface> surface; check_hresult(swapChain->GetBuffer(0,__uuidof(IDXGISurface),surface.put_void()));
        auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,D2D1::PixelFormat(desc.Format,D2D1_ALPHA_MODE_PREMULTIPLIED));
        com_ptr<ID2D1Bitmap1> bitmap; check_hresult(drawing->CreateBitmapFromDxgiSurface(surface.get(),&properties,bitmap.put())); drawing->SetTarget(bitmap.get());
        com_ptr<IDWriteFactory> writer; check_hresult(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(writer.put())));
        check_hresult(writer->CreateTextFormat(L"Segoe UI",nullptr,DWRITE_FONT_WEIGHT_BOLD,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,36,L"en-us",textFormat.put()));
        std::vector<com_ptr<IDCompositionTarget>> moreTargets; std::vector<com_ptr<IDCompositionVisual>> moreVisuals;
        if(burst) for(int i=1;i<3;++i) {
            HWND extra=CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP|WS_EX_TOPMOST|WS_EX_TOOLWINDOW,wc.lpszClassName,(L"广告测试 GPU burst "+std::to_wstring(i)).c_str(),WS_POPUP,
                area.right-740-i*740,area.bottom-180,720,160,nullptr,nullptr,instance,nullptr);
            if(!extra) return 2; windows.push_back(extra);
            com_ptr<IDCompositionTarget> extraTarget; com_ptr<IDCompositionVisual> extraVisual;
            check_hresult(composition->CreateTargetForHwnd(extra,TRUE,extraTarget.put())); check_hresult(composition->CreateVisual(extraVisual.put()));
            check_hresult(extraVisual->SetContent(swapChain.get())); check_hresult(extraTarget->SetRoot(extraVisual.get())); moreTargets.push_back(extraTarget); moreVisuals.push_back(extraVisual);
        }
        check_hresult(composition->Commit()); Render(armed); if(protect&&!SetWindowDisplayAffinity(window,WDA_EXCLUDEFROMCAPTURE)) return 3;
        if(!armed) { ShowWindow(window,SW_SHOWNOACTIVATE); if(!blank) SetTimer(window,1,600,nullptr); }
        else { SetWindowTextW(window,L"广告测试 GPU brief popup"); SetWindowTextW(window,L"广告测试 GPU brief READY"); }
        for(auto item:windows) SetTimer(item,2,30000,nullptr);
        MSG msg{}; while(GetMessageW(&msg,nullptr,0,0)>0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        drawing=nullptr; textFormat=nullptr; swapChain=nullptr; return 0;
    } catch(const winrt::hresult_error& e) { fwprintf(stderr,L"Fixture HRESULT %08X\n",static_cast<unsigned>(e.code())); return 1; }
}
