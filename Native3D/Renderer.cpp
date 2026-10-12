#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <DirectXMath.h>
#include <vector>
#include <string>
#include <algorithm>
#include <cmath>
#include <cstring>
using Microsoft::WRL::ComPtr;
using namespace DirectX;
#define API extern "C" __declspec(dllexport)

struct Gpu { long long id; wchar_t name[128]; unsigned long long memory; int integrated; };
struct Vertex { float p[3], n[3], c[4]; };
struct Options { float matrix[16]; float light[4]; float effect[4]; float background[4]; };
struct Renderer {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> color, depth, staging;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11DepthStencilView> depthView;
    ComPtr<ID3D11Buffer> vertices, constants;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11InputLayout> layout;
    ComPtr<ID3D11RasterizerState> solid, wire;
    ComPtr<ID3D11BlendState> blend;
    unsigned count=0, width=0,height=0;
    long long adapterId=0;
};
static std::vector<ComPtr<IDXGIAdapter1>> adapters() {
    ComPtr<IDXGIFactory1> factory; std::vector<ComPtr<IDXGIAdapter1>> out;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return out;
    ComPtr<IDXGIFactory6> modern; factory.As(&modern);
    for(UINT i=0;i<32;i++) {
        ComPtr<IDXGIAdapter1> a;
        HRESULT hr=modern?modern->EnumAdapterByGpuPreference(i,DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,IID_PPV_ARGS(&a)):factory->EnumAdapters1(i,&a);
        if(FAILED(hr)) break;
        DXGI_ADAPTER_DESC1 d{};a->GetDesc1(&d);
        if(!(d.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)) out.push_back(a);
    }
    return out;
}
static bool integrated(IDXGIAdapter1* a) {
    ComPtr<ID3D11Device> d;
    if(SUCCEEDED(D3D11CreateDevice(a,D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&d,nullptr,nullptr))) {
        D3D11_FEATURE_DATA_D3D11_OPTIONS2 opt{};
        if(SUCCEEDED(d->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS2,&opt,sizeof(opt)))) return opt.UnifiedMemoryArchitecture!=0;
    }
    DXGI_ADAPTER_DESC1 info{};a->GetDesc1(&info);
    return info.DedicatedVideoMemory<256ull*1024*1024;
}
API int pd_gpu_list(Gpu* output,int capacity) {
    try { auto list=adapters();int n=0;
        for(auto& a:list) { if(n>=capacity) break;DXGI_ADAPTER_DESC1 d{};a->GetDesc1(&d);
            auto& o=output[n++];memcpy(&o.id,&d.AdapterLuid,8);wcsncpy_s(o.name,d.Description,_TRUNCATE);o.memory=d.DedicatedVideoMemory;o.integrated=integrated(a.Get())?1:0;
        }return n;
    }catch(...){return 0;}
}
static const char* shader=R"(
cbuffer Params:register(b0) { row_major float4x4 mvp; float4 light; float4 effect; float4 bg; };
struct In { float3 p:POSITION; float3 n:NORMAL; float4 c:COLOR; };
struct Out { float4 p:SV_POSITION; float3 n:NORMAL; float4 c:COLOR; float3 world:TEXCOORD; };
Out VS(In a){Out o;o.p=mul(float4(a.p,1),mvp);o.n=a.n;o.c=a.c;o.world=a.p;return o;}
float4 PS(Out a):SV_TARGET {
 if(effect.z>0.5 && a.world.z>effect.w) discard;
 float3 n=normalize(a.n);float3 l=normalize(light.xyz);float diffuse=abs(dot(n,l));
 float3 color=a.c.rgb;float spec=pow(saturate(abs(dot(n,normalize(l+float3(0,0,1))))),max(2,effect.y));
 if(effect.x<0.5) color*=light.w+(1-light.w)*diffuse;
 else if(effect.x<1.5) color=color*(light.w+(1-light.w)*diffuse)+spec*.45;
 else if(effect.x<2.5) color=float3(.80,.84,.89)*(light.w+(1-light.w)*diffuse)+spec*.7;
 else if(effect.x<3.5) color*=floor(diffuse*4)/4*.8+.2;
 else color=float3(.15,.20,.28);
 return float4(saturate(color),1);
})";
API void* pd_renderer_create(long long preferred,long long* actual) {
    try {
        auto r=new Renderer();auto list=adapters();ComPtr<IDXGIAdapter1> chosen;
        if(preferred!=-1) {
            if(preferred!=0) for(auto& a:list){DXGI_ADAPTER_DESC1 d{};a->GetDesc1(&d);long long id;memcpy(&id,&d.AdapterLuid,8);if(id==preferred){chosen=a;break;}}
            if(preferred!=0&&!chosen){delete r;return nullptr;}
            if(!chosen) {for(auto& a:list)if(!integrated(a.Get())){chosen=a;break;}if(!chosen&&!list.empty())chosen=list.front();}
        }
        HRESULT hr=D3D11CreateDevice(chosen.Get(),chosen?D3D_DRIVER_TYPE_UNKNOWN:D3D_DRIVER_TYPE_WARP,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&r->device,nullptr,&r->context);
        if(FAILED(hr)){delete r;return nullptr;}
        if(chosen){DXGI_ADAPTER_DESC1 d{};chosen->GetDesc1(&d);memcpy(&r->adapterId,&d.AdapterLuid,8);}else r->adapterId=-1;
        *actual=r->adapterId;
        ComPtr<ID3DBlob> vs,ps,error;
        hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"VS","vs_4_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error);
        if(FAILED(hr)){delete r;return nullptr;}
        hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"PS","ps_4_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error);
        if(FAILED(hr)){delete r;return nullptr;}
        if(FAILED(r->device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&r->vs)) || FAILED(r->device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&r->ps))){delete r;return nullptr;}
        D3D11_INPUT_ELEMENT_DESC elements[]={{"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},{"NORMAL",0,DXGI_FORMAT_R32G32B32_FLOAT,0,12,D3D11_INPUT_PER_VERTEX_DATA,0},{"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,24,D3D11_INPUT_PER_VERTEX_DATA,0}};
        if(FAILED(r->device->CreateInputLayout(elements,3,vs->GetBufferPointer(),vs->GetBufferSize(),&r->layout))){delete r;return nullptr;}
        D3D11_BUFFER_DESC cb{};cb.ByteWidth=sizeof(Options);cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;cb.Usage=D3D11_USAGE_DEFAULT;
        if(FAILED(r->device->CreateBuffer(&cb,nullptr,&r->constants))){delete r;return nullptr;}
        D3D11_RASTERIZER_DESC rs{};rs.CullMode=D3D11_CULL_NONE;rs.FillMode=D3D11_FILL_SOLID;rs.DepthClipEnable=TRUE;rs.MultisampleEnable=TRUE;
        r->device->CreateRasterizerState(&rs,&r->solid);rs.FillMode=D3D11_FILL_WIREFRAME;r->device->CreateRasterizerState(&rs,&r->wire);
        return r;
    }catch(...){return nullptr;}
}
API void pd_renderer_destroy(void* handle){delete static_cast<Renderer*>(handle);}
API int pd_renderer_upload(void* handle,const Vertex* vertices,unsigned count) {
    try{auto r=static_cast<Renderer*>(handle);if(!r||!count||count>15000000)return 0;
        D3D11_BUFFER_DESC d{};d.ByteWidth=count*sizeof(Vertex);d.Usage=D3D11_USAGE_IMMUTABLE;d.BindFlags=D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA data{};data.pSysMem=vertices;ComPtr<ID3D11Buffer> b;
        if(FAILED(r->device->CreateBuffer(&d,&data,&b)))return 0;r->vertices=b;r->count=count;return 1;
    }catch(...){return 0;}
}
static bool resize(Renderer* r,unsigned w,unsigned h) {
    r->context->OMSetRenderTargets(0,nullptr,nullptr);r->color.Reset();r->depth.Reset();r->target.Reset();r->depthView.Reset();r->staging.Reset();
    D3D11_TEXTURE2D_DESC d{};d.Width=w;d.Height=h;d.MipLevels=1;d.ArraySize=1;d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.SampleDesc.Count=1;d.Usage=D3D11_USAGE_DEFAULT;d.BindFlags=D3D11_BIND_RENDER_TARGET;
    if(FAILED(r->device->CreateTexture2D(&d,nullptr,&r->color))||FAILED(r->device->CreateRenderTargetView(r->color.Get(),nullptr,&r->target)))return false;
    d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    if(FAILED(r->device->CreateTexture2D(&d,nullptr,&r->staging)))return false;
    d.CPUAccessFlags=0;d.Usage=D3D11_USAGE_DEFAULT;d.Format=DXGI_FORMAT_D24_UNORM_S8_UINT;d.BindFlags=D3D11_BIND_DEPTH_STENCIL;
    if(FAILED(r->device->CreateTexture2D(&d,nullptr,&r->depth))||FAILED(r->device->CreateDepthStencilView(r->depth.Get(),nullptr,&r->depthView)))return false;
    r->width=w;r->height=h;return true;
}
API int pd_renderer_render(void* handle,unsigned width,unsigned height,const Options* options,int wire,unsigned char* output,unsigned bytes) {
    try{auto r=static_cast<Renderer*>(handle);if(!r||!options||!output||width<1||height<1||width>4096||height>4096||bytes<(size_t)width*height*4)return 0;
        if((width!=r->width||height!=r->height)&&!resize(r,width,height))return 0;
        r->context->ClearRenderTargetView(r->target.Get(),options->background);r->context->ClearDepthStencilView(r->depthView.Get(),D3D11_CLEAR_DEPTH,1,0);
        ID3D11RenderTargetView* target=r->target.Get();r->context->OMSetRenderTargets(1,&target,r->depthView.Get());
        D3D11_VIEWPORT viewport{0,0,(float)width,(float)height,0,1};r->context->RSSetViewports(1,&viewport);
        r->context->RSSetState(wire?r->wire.Get():r->solid.Get());r->context->IASetInputLayout(r->layout.Get());
        UINT stride=sizeof(Vertex),offset=0;ID3D11Buffer* vb=r->vertices.Get();r->context->IASetVertexBuffers(0,1,&vb,&stride,&offset);r->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        r->context->UpdateSubresource(r->constants.Get(),0,nullptr,options,0,0);ID3D11Buffer* cb=r->constants.Get();
        r->context->VSSetConstantBuffers(0,1,&cb);r->context->PSSetConstantBuffers(0,1,&cb);r->context->VSSetShader(r->vs.Get(),nullptr,0);r->context->PSSetShader(r->ps.Get(),nullptr,0);
        if(r->vertices)r->context->Draw(r->count,0);
        r->context->CopyResource(r->staging.Get(),r->color.Get());D3D11_MAPPED_SUBRESOURCE mapped{};
        if(FAILED(r->context->Map(r->staging.Get(),0,D3D11_MAP_READ,0,&mapped)))return 0;
        for(unsigned y=0;y<height;y++)memcpy(output+(size_t)y*width*4,(char*)mapped.pData+(size_t)y*mapped.RowPitch,width*4);
        r->context->Unmap(r->staging.Get(),0);return 1;
    }catch(...){return 0;}
}
