using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace LemonPlayer.Windows
{
    public static class DirectxHelper
    {
        static readonly D3D11 D3D11;
        static readonly DXGI DXGI;
        static readonly D3DCompiler D3DCompiler;
        static ComPtr<IDXGIFactory2> _dxgiFactory;

        public static ComPtr<IDXGIFactory2> DXGIFactory
        {
            get
            {
                if (_dxgiFactory.IsEmpty())
                    DXGI.CreateDXGIFactory(out _dxgiFactory);
                return _dxgiFactory;
            }
        }

        static DirectxHelper()
        {
            D3D11 = D3D11.GetApi(null);
            DXGI = DXGI.GetApi(null);
            D3DCompiler = D3DCompiler.GetApi();
        }

        public static void D3D11CreateDevice(out ComPtr<ID3D11Device1> device, out ComPtr<ID3D11DeviceContext1> context)
        {
            // 支持D3D11.1的显卡
            // Intel: HD4000
            // Nvidia: 400 
            // AMD: HD5000
            // win7sp1可以支持D3D11.1，需KB2670838，这个更新是否已经集成在大部分win7镜像中？
            // https://walbourn.github.io/directx-sdks-of-a-certain-age/

            // WRAP设备不支持视频编码或解码
            // https://learn.microsoft.com/zh-cn/windows/win32/direct3d11/direct3d-11-1-features

            // d3d11va是否支持win7？
            // 从D3D11.1开始支持D3D11_CREATE_DEVICE_VIDEO_SUPPORT
            // “当显示驱动程序未实现到 WDDM 1.2 时，只有 使用功能级别 9.1、9.2 或 9.3 创建的 Direct3D 设备支持视频”
            // https://learn.microsoft.com/zh-cn/windows/win32/api/d3d11/ne-d3d11-d3d11_create_device_flag

            CreateDeviceFlag creationFlags = CreateDeviceFlag.BgraSupport;
            //creationFlags |= CreateDeviceFlag.VideoSupport;
//#if DEBUG
//            creationFlags |= CreateDeviceFlag.Debug;
//#endif
            device = null;
            context = null;
            D3DFeatureLevel pFeatureLevels = D3DFeatureLevel.Level111;
            D3DFeatureLevel featureLevel = default;
            // 如果指定了adapter，则DriverType应为Unknown
            HResult hr = D3D11.CreateDevice(ref Unsafe.NullRef<IDXGIAdapter>(), D3DDriverType.Hardware, IntPtr.Zero, (uint)creationFlags, ref pFeatureLevels, 1, D3D11.SdkVersion, ref device, ref featureLevel, ref context);
            if (!hr.IsSuccess)
                throw new Exception($"创建D3D11Device失败：0x{hr.Value:X}");
        }

        public unsafe static HResult CompilerShader(string shaderCode, string entryPoint, string target, ref ComPtr<ID3D10Blob> codeData, out string error)
        {
            byte[] sharderBytes = Encoding.ASCII.GetBytes(shaderCode); // ASCII
            ComPtr<ID3D10Blob> errorBlob = default;
            HResult hr = D3DCompiler.Compile(
                ref sharderBytes[0],
                (UIntPtr)sharderBytes.Length,
                nameof(shaderCode),
                null,
                ref Unsafe.NullRef<ID3DInclude>(),
                entryPoint,
                target,
                0,
                0,
                ref codeData,
                ref errorBlob);
            if (hr.IsSuccess)
            {
                error = null;
            }
            else
            {
                error = SilkMarshal.PtrToString((IntPtr)errorBlob.GetBufferPointer());
            }
            errorBlob.Dispose();
            return hr;
        }
    }
}
