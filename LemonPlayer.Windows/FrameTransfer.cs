using LemonPlayer.Renderer;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LemonPlayer.Windows
{
    struct Texture2D : IDisposable
    {
        public ComPtr<ID3D11Texture2D> texture;
        public Texture2DDesc desc;

        public Texture2D(ComPtr<ID3D11Texture2D> texture, Texture2DDesc desc)
        {
            this.texture = texture;
            this.desc = desc;
        }

        public void Dispose()
        {
            texture.Dispose();
        }
    }

    enum TextureType
    {
        /// <summary>
        /// 单个Texture
        /// </summary>
        RGB,
        /// <summary>
        /// 单个Texture
        /// </summary>
        RGBA,
        /// <summary>
        /// 单个Texture
        /// </summary>
        YUVHW,
        /// <summary>
        /// 三个Texture，分别包含YUV分量
        /// </summary>
        YUVSW,
    }

    struct FrameTextureDesc : IDisposable
    {
        public TextureType type;
        public uint index1;
        public Texture2D tex1;
        public Texture2D tex2;
        public Texture2D tex3;
        public Vector4 srcRect;

        public void Dispose()
        {
            tex1.Dispose();
            tex2.Dispose();
            tex3.Dispose();
        }
    }

    /// <summary>
    /// 将<see cref="FFmpeg.AutoGen.AVFrame"/>转换成ID3D11Texture2D，获取的texture有以下几种类型：<see cref="TextureType"/>
    /// </summary>
    internal class FrameTransfer : SwsTransfer
    {
        private readonly Dx11Context hwctx;
        private Texture2D hwCache;
        private Texture2D swCache;
        private Texture2D yCache;
        private Texture2D uCache;
        private Texture2D vCache;
        private ComPtr<ID3D11Device1> Device => hwctx.Device;
        private ComPtr<ID3D11DeviceContext1> Context => hwctx.Context;

        public FrameTransfer(Dx11Context hwctx)
        {
            this.hwctx = hwctx;
        }

        public bool GetTexture(VideoFrame frame, ref FrameTextureDesc desc)
        {
            if (frame.IsHwFrame)
            {
                if (frame.format == (int)FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_D3D11)
                    return GetTextureD3D11(frame, ref desc);
            }
            if (GetTextureYuv(frame, ref desc))
                return true;
            if (GetTextureSoftware(frame, ref desc))
                return true;
            return false;
        }

        // 获取D3D11VA解码的视频帧
        private bool GetTextureD3D11(VideoFrame frame, ref FrameTextureDesc desc)
        {
            ComPtr<ID3D11Texture2D> avtexture;
            uint avindex;
            D3d11GetFrameTexture(frame, out avtexture, out avindex);
            Texture2DDesc avdesc = default;
            avtexture.GetDesc(ref avdesc);

            if (!Dx11RendererBase.IsInputFormatSupported(avdesc.Format))
                return false;

            desc.type = TextureType.YUVHW;
            desc.srcRect = new Vector4(0, 0, frame.width * 1.0f / avdesc.Width, frame.height * 1.0f / avdesc.Height);
            // 在同一个设备上就直接用
            if (frame.hwctx == hwctx)
            {
                desc.index1 = avindex;
                desc.tex1 = new Texture2D(avtexture, avdesc);
                return true;
            }

            // 在不同设备上需要共享或拷贝
            uint support = 0;
            if (Device.CheckFormatSupport(avdesc.Format, ref support) != 0 ||
                (support & (uint)FormatSupport.Texture2D) == 0)
                return false;

            // 共享且可以直接用
            if ((avdesc.MiscFlags & (uint)ResourceMiscFlag.Shared) != 0 &&
                (avdesc.BindFlags & (uint)BindFlag.ShaderResource) != 0)
            {
                D3d11OpenSharedTexture(avtexture, Device, out var texture);
                desc.index1 = avindex;
                desc.tex1 = new Texture2D(texture, avdesc);
                avtexture.Release();
                return true;
            }

            // 不能直接用的话，就要拷贝出来
            D3d11GetCachedTexture(ref hwCache, avdesc.Width, avdesc.Height, avdesc.Format, false);
            CopyTextureToHwCache(avtexture, avindex);
            avtexture.Release();
            hwCache.texture.AddRef();
            desc.tex1 = hwCache;
            desc.index1 = 0;
            return true;
        }

        private void CopyTextureToHwCache(ComPtr<ID3D11Texture2D> avtexture, uint index)
        {
            ComPtr<ID3D11Device1> avDevice = default;
            ComPtr<ID3D11Texture2D> sharedAvTexture = default;
            ComPtr<ID3D11DeviceContext> avContext = default;
            try
            {
                avtexture.GetDevice(ref avDevice);
                D3d11OpenSharedTexture(hwCache.texture, avDevice, out sharedAvTexture);
                avDevice.GetImmediateContext(ref avContext);
                avContext.CopySubresourceRegion(sharedAvTexture, 0, 0, 0, 0, avtexture, index, ref Unsafe.NullRef<Box>());
                avContext.Flush();
            }
            finally
            {
                avContext.Dispose();
                sharedAvTexture.Dispose();
                avDevice.Dispose();
            }
        }

        // 获取分离的Y、U、V视频帧
        private unsafe bool GetTextureYuv(VideoFrame frame, ref FrameTextureDesc desc)
        {
            var pix_fmt = (FFmpeg.AutoGen.AVPixelFormat)frame.format;
            int uvWidth;
            int uvHeight;
            FFmpeg.AutoGen.AVPixFmtDescriptor* pixDesc;
            // TODO: 支持更多yuv格式
            switch (pix_fmt)
            {
                case FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV420P:
                    pixDesc = FFmpeg.AutoGen.ffmpeg.av_pix_fmt_desc_get((FFmpeg.AutoGen.AVPixelFormat)frame.format);
                    uvWidth = frame.width >> pixDesc->log2_chroma_w;
                    uvHeight = frame.height >> pixDesc->log2_chroma_h;
                    break;
                default:
                    return false;
            }
            int ylength = frame.width * frame.height;
            int uvlength = uvWidth * uvHeight;

            D3d11GetCachedTexture(ref yCache, (uint)frame.width, (uint)frame.height, Format.FormatR8Unorm, false);
            Context.UpdateSubresource(yCache.texture, 0, null, frame.frame->data[0], (uint)frame.width, (uint)frame.height);

            D3d11GetCachedTexture(ref uCache, (uint)uvWidth, (uint)uvHeight, Format.FormatR8Unorm, false);
            Context.UpdateSubresource(uCache.texture, 0, null, frame.frame->data[1], (uint)uvWidth, (uint)uvHeight);

            D3d11GetCachedTexture(ref vCache, (uint)uvWidth, (uint)uvHeight, Format.FormatR8Unorm, false);
            Context.UpdateSubresource(vCache.texture, 0, null, frame.frame->data[2], (uint)uvWidth, (uint)uvHeight);

            desc.type = TextureType.YUVSW;
            desc.tex1 = yCache;
            desc.tex2 = uCache;
            desc.tex3 = vCache;
            desc.srcRect = new Vector4(0, 0, 1, 1);
            yCache.texture.AddRef();
            uCache.texture.AddRef();
            vCache.texture.AddRef();
            return true;
        }

        // 通过sws_cale获取视频帧
        private unsafe bool GetTextureSoftware(VideoFrame frame, ref FrameTextureDesc desc)
        {
            // TODO: 向texture添加填充以提高性能
            D3d11GetCachedTexture(ref swCache, (uint)frame.width, (uint)frame.height, Format.FormatR8G8B8A8Unorm, true);
            MappedSubresource mappedResource = default;
            HResult hr = Context.Map(swCache.texture, 0, Map.WriteDiscard, 0, ref mappedResource);
            if (hr.IsSuccess)
            {
                bool ret = SwsScale(frame.frame, frame.width, frame.height, FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_RGBA, mappedResource.PData, (int)mappedResource.RowPitch);
                Context.Unmap(swCache.texture, 0);
                if (ret)
                {
                    desc.index1 = 0;
                    desc.type = TextureType.RGBA;
                    desc.tex1 = swCache;
                    desc.srcRect = new Vector4(0, 0, 1, 1);
                    swCache.texture.AddRef();
                }
                return ret;
            }
            return false;
        }

        private void D3d11GetCachedTexture(ref Texture2D cache, uint width, uint height, Format format, bool dynamic)
        {
            if (!cache.texture.IsEmpty())
            {
                Texture2DDesc cacheDesc = cache.desc;
                // 不需要检查Usage，因为cache是我们指定的
                if (cacheDesc.Width == width && cacheDesc.Height == height &&
                    cacheDesc.Format == format)
                    return;
            }
            cache.Dispose();
            cache.desc = new Texture2DDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDesc = new SampleDesc(1, 0),
                Usage = dynamic ? Usage.Dynamic : Usage.Default,
                BindFlags = (uint)BindFlag.ShaderResource,
                CPUAccessFlags = dynamic ? (uint)CpuAccessFlag.Write : 0,
                MiscFlags = dynamic ? 0 : (uint)ResourceMiscFlag.Shared,
            };
            Device.CreateTexture2D(ref cache.desc, ref Unsafe.NullRef<SubresourceData>(), ref cache.texture);
        }

        private unsafe static void D3d11GetFrameTexture(VideoFrame frame, out ComPtr<ID3D11Texture2D> texture, out uint index)
        {
            texture = new ComPtr<ID3D11Texture2D>((ID3D11Texture2D*)frame.frame->data[0]);
            index = (uint)frame.frame->data[1];
        }

        private unsafe static bool D3d11OpenSharedTexture(ComPtr<ID3D11Texture2D> src, ComPtr<ID3D11Device1> device, out ComPtr<ID3D11Texture2D> shared)
        {
            ComPtr<IDXGIResource> dxgiResource = src.QueryInterface<IDXGIResource>();
            void* sharedHandle = null;
            HResult hr = dxgiResource.GetSharedHandle(ref sharedHandle);
            dxgiResource.Release();
            if (hr.IsFailure)
            {
                shared = default;
                return false;
            }
            hr = device.OpenSharedResource(sharedHandle, out shared);
            if (hr.IsFailure)
            {
                shared = default;
                return false;
            }
            return true;
        }
    }
}
