using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D9;
using Silk.NET.DXGI;

namespace PlayerWpf
{
    internal class D3D9Helper
    {
#pragma warning disable CS0618 // 类型或成员已过时
        static readonly D3D9 D3D9 = D3D9.GetApi();
#pragma warning restore CS0618 // 类型或成员已过时
        static ComPtr<IDirect3D9Ex> D3D9Context;

        static D3D9Helper()
        {
            D3D9.Direct3DCreate9Ex(D3D9.SdkVersion, ref D3D9Context);
        }

        public unsafe static ComPtr<IDirect3DDevice9Ex> CreateDx9Device()
        {
            uint createFlags = D3D9.CreateHardwareVertexprocessing | D3D9.CreateMultithreaded;
            //| D3D9.CreateFpuPreserve;
            var presentParams = new Silk.NET.Direct3D9.PresentParameters
            {
                Windowed = true,
                BackBufferFormat = Silk.NET.Direct3D9.Format.Unknown,
                BackBufferHeight = 1,
                BackBufferWidth = 1,
                SwapEffect = Swapeffect.Discard,
                HDeviceWindow = IntPtr.Zero,
                Flags = D3D9.PresentflagVideo
            };
            Displaymodeex* displaymode = null;
            ComPtr<IDirect3DDevice9Ex> d3d9Device = default;
            D3D9Context.CreateDeviceEx(0, Devtype.Hal, IntPtr.Zero, createFlags, ref presentParams, displaymode, ref d3d9Device);
            return d3d9Device;
        }

        public unsafe static ComPtr<IDirect3DSurface9> OpenSharedResource(ComPtr<IDirect3DDevice9Ex> device, ComPtr<ID3D11Texture2D> texture11)
        {
            Texture2DDesc desc = default;
            texture11.GetDesc(ref desc);
            using var resource = texture11.QueryInterface<IDXGIResource>();
            void* handle = default;
            resource.GetSharedHandle(ref handle);
            var sharedHandle = (IntPtr)handle;
            ComPtr<IDirect3DTexture9> texture9 = default;
            device.CreateTexture(desc.Width, desc.Height, 1, D3D9.UsageRendertarget, Silk.NET.Direct3D9.Format.A8R8G8B8, Pool.Default, ref texture9, ref handle);
            ComPtr<IDirect3DSurface9> surface = default;
            texture9.GetSurfaceLevel(0, ref surface);
            texture9.Release();
            return surface;
        }
    }
}
