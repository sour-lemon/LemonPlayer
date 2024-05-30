using FFmpeg.AutoGen;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using System;

namespace LemonPlayer.Windows
{
    public unsafe class Dx11Context : IDeviceContext, IDisposable
    {
        readonly ComPtr<ID3D11Device1> device;
        readonly ComPtr<ID3D11DeviceContext1> context;
        readonly AVBufferRef* hw_device_ctx;

        public AVHWDeviceType DeviceType => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;

        public AVPixelFormat HWPixelFormat => AVPixelFormat.AV_PIX_FMT_D3D11;

        public ComPtr<ID3D11Device1> Device => device;

        public ComPtr<ID3D11DeviceContext1> Context => context;

        public D3DFeatureLevel Level { get; }

        public string PsShaderMode { get; }
        public string VsShaderMode { get; }

        public Dx11Context(ComPtr<ID3D11Device1> device, ComPtr<ID3D11DeviceContext1> context)
        {
            if (device.IsEmpty())
                throw new ArgumentNullException(nameof(device));
            if (context.IsEmpty())
                throw new ArgumentNullException(nameof(context));

            this.device = device;
            device.AddRef();
            this.context = context;
            context.AddRef();
            Level = device.GetFeatureLevel();
            if (Level == D3DFeatureLevel.Level93)
            {
                PsShaderMode = "ps_4_0_level_9_3";
                VsShaderMode = "vs_4_0_level_9_3";
            }
            else
            {
                PsShaderMode = "ps_5_0";
                VsShaderMode = "vs_5_0";
            }

            // 从D3D11.4开始支持，win10 14393即支持D3D11.4
            // 如果不支持该功能，则ffmpeg与渲染器不能使用同一个设备
            ComPtr<ID3D11Multithread> mthread;
            int hr = device.QueryInterface(out mthread);
            if (hr == 0)
            {
                mthread.SetMultithreadProtected(true);
                mthread.Dispose();
            }

            AVBufferRef* hw_device_ctx = ffmpeg.av_hwdevice_ctx_alloc(DeviceType);
            if (hw_device_ctx == null)
                throw new Exception("av_hwdevice_ctx_alloc failed");
            var device_ctx = (AVHWDeviceContext*)hw_device_ctx->data;
            var d3d11va_device_ctx = (AVD3D11VADeviceContext*)device_ctx->hwctx;
            d3d11va_device_ctx->device = (FFmpeg.AutoGen.ID3D11Device*)device.Handle;
            int ret = ffmpeg.av_hwdevice_ctx_init(hw_device_ctx);
            if (ret == 0)
            {
                device.AddRef();
            }
            else
            {
                ffmpeg.av_buffer_unref(&hw_device_ctx);
                Dispose();
                throw new Exception($"failed to init AVHWDeviceContext: {ret}");
            }
            this.hw_device_ctx = hw_device_ctx;
        }

        public unsafe bool ApplyDeviceContext(AVCodecContext* avctx)
        {
            avctx->hw_device_ctx = ffmpeg.av_buffer_ref(hw_device_ctx);
            return true;
        }

        public unsafe bool ApplyFrameContext(AVCodecContext* avctx)
        {
            int ret;
            ret = ffmpeg.avcodec_get_hw_frames_parameters(avctx, avctx->hw_device_ctx, HWPixelFormat, &avctx->hw_frames_ctx);
            if (ret != 0)
            {
                if (avctx->hw_frames_ctx != null)
                    ffmpeg.av_buffer_unref(&avctx->hw_frames_ctx);
                return false;
            }
            var hw_frames_ctx = (AVHWFramesContext*)avctx->hw_frames_ctx->data;
            var hwctx = (AVD3D11VAFramesContext*)hw_frames_ctx->hwctx;
            hwctx->BindFlags |= (uint)(BindFlag.Decoder | BindFlag.ShaderResource);
            hwctx->MiscFlags |= (uint)ResourceMiscFlag.Shared;
            ret = ffmpeg.av_hwframe_ctx_init(avctx->hw_frames_ctx);

            if (ret != 0)
            {
                ffmpeg.av_buffer_unref(&avctx->hw_frames_ctx);
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            device.Release();
            context.Release();
            if (hw_device_ctx != null)
                fixed (AVBufferRef** ptr = &hw_device_ctx)
                    ffmpeg.av_buffer_unref(ptr);
        }
    }
}
