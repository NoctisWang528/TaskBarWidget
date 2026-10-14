#include <Windows.h>
#include <d3d10_1.h>
#include <dxgi1_2.h>
#include <d2d1_1.h>
#include <wrl/client.h>
#include <iostream>
#include "../TrafficMonitor/CompositionFade.h"
#pragma comment(lib,"d3d10_1.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"d2d1.lib")
#pragma comment(lib,"dcomp.lib")
#pragma comment(lib,"user32.lib")
using Microsoft::WRL::ComPtr;
void Check(HRESULT hr) { if(FAILED(hr)) throw hr; }
int main()
{
    try
    {
        ComPtr<ID3D10Device1> graphics;
        Check(D3D10CreateDevice1(nullptr,D3D10_DRIVER_TYPE_WARP,nullptr,D3D10_CREATE_DEVICE_BGRA_SUPPORT,D3D10_FEATURE_LEVEL_10_1,D3D10_1_SDK_VERSION,&graphics));
        ComPtr<IDXGIDevice> dxgi; Check(graphics.As(&dxgi));
        ComPtr<ID2D1Factory1> factory; Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,IID_PPV_ARGS(&factory)));
        ComPtr<ID2D1Device> device; Check(factory->CreateDevice(dxgi.Get(),&device));
        ComPtr<ID2D1DeviceContext> context; Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&context));
        ComPtr<IDXGIFactory2> dxgiFactory; Check(CreateDXGIFactory1(IID_PPV_ARGS(&dxgiFactory)));
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width=315;desc.Height=32;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
        desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;
        desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;
        ComPtr<IDXGISwapChain1> swap; Check(dxgiFactory->CreateSwapChainForComposition(graphics.Get(),&desc,nullptr,&swap));
        auto cachedContext=context; // The production icon cache keeps this indirect reference.
        for (const UINT width : {420u,380u,315u,420u})
        {
            ComPtr<IDXGISurface> surface;Check(swap->GetBuffer(0,IID_PPV_ARGS(&surface)));
            auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
            ComPtr<ID2D1Bitmap1> bitmap;Check(context->CreateBitmapFromDxgiSurface(surface.Get(),&properties,&bitmap));
            context->SetTarget(bitmap.Get());surface.Reset();bitmap.Reset();context.Reset();
            if (swap->ResizeBuffers(0,width,32,DXGI_FORMAT_UNKNOWN,0)!=DXGI_ERROR_INVALID_CALL) return 2;
            cachedContext->SetTarget(nullptr);
            Check(swap->ResizeBuffers(0,width,32,DXGI_FORMAT_UNKNOWN,0));
            context=cachedContext;
        }
        ComPtr<IDCompositionDevice> composition;Check(DCompositionCreateDevice(nullptr,IID_PPV_ARGS(&composition)));
        ComPtr<IDCompositionVisual> visual;Check(composition->CreateVisual(&visual));
        CompositionFade fade;
        Check(fade.Set(composition.Get(),visual.Get(),false,false));
        Check(fade.Set(composition.Get(),visual.Get(),true));
        Check(fade.Set(composition.Get(),visual.Get(),false));
        Check(composition->Commit());
        std::cout<<"Cached-target resize reproduced; repeated released-target resizes and composition fades passed (WARP, no window).\n";
    }
    catch (HRESULT result) { std::cerr<<"Graphics regression HRESULT=0x"<<std::hex<<static_cast<unsigned>(result)<<"\n";return 1; }
}

