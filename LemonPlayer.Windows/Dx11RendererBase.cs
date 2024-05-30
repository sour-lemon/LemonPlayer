using LemonPlayer.Renderer;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LemonPlayer.Windows
{
    public abstract class Dx11RendererBase : VideoRendererBase, IDisposable
    {
        enum ColorRange
        {
            Limit,
            Full,
        }
        struct ShaderInfo
        {
            public Vector4 SrcRect;
            public TextureType Type;
            public int ColorDepth;
            public ColorRange ColorRange;
            public FFmpeg.AutoGen.AVColorSpace ColorSpace;

            public bool IsRGB => Type == TextureType.RGB || Type == TextureType.RGBA;
        }
        struct VertexData
        {
            public Vector3 pos;
            public Vector2 uv;

            public VertexData(float x, float y, float z, float u, float v)
            {
                pos = new Vector3(x, y, z);
                uv = new Vector2(u, v);
            }
        }
        const string VertexSource = @"
cbuffer VS_SRC_RECT : b0
{
    float4 src_rect;
};

struct vs_out
{
    float2 uv : TEXCOORD;
    float4 pos : SV_POSITION;
};

vs_out vs_main(float3 pos : POSITION, float2 uv : TEXCOORD) {
    vs_out output;
    output.uv = uv * src_rect.ba + src_rect.rg;
    output.pos = float4(pos, 1.0);
    return output;
}";
        const string PixelSource = @"
{0}
SamplerState splr;

cbuffer PS_MATRIX : b0
{{
    row_major float4x4 colorMatrix;
}};

struct vs_out
{{
    float2 uv : TEXCOORD;
    float4 pos : SV_POSITION;
}};

float4 read_texture(float2 uv) {{
    {1}
}}

float4 ps_main(float2 uv : TEXCOORD) : SV_TARGET {{
    float4 color4 = read_texture(uv);
    color4 = mul(color4, colorMatrix);
    color4 = saturate(color4);
    return color4;
}}";

        private readonly Dx11Context hwctx;
        private readonly FrameTransfer frameTransfer;

        readonly VertexData[] vertices =
        {
            new VertexData(-1,  1, 0, 0, 0),
            new VertexData( 1,  1, 0, 1, 0),
            new VertexData( 1, -1, 0, 1, 1),
            new VertexData(-1, -1, 0, 0, 1),
        };
        readonly int[] indices = { 0, 1, 2, 0, 2, 3 };
        ComPtr<ID3D11Buffer> vertexBuffer;
        ComPtr<ID3D11Buffer> indexBuffer;
        ComPtr<ID3D11VertexShader> vertexShader;
        ComPtr<ID3D11InputLayout> vertexInputLayout;
        ComPtr<ID3D11Buffer> rectBuffer;

        ComPtr<ID3D11SamplerState> psSampler;
        ComPtr<ID3D11PixelShader> pixelShader;
        ComPtr<ID3D11Buffer> matrixBuffer;
        ShaderInfo shaderInfo = new ShaderInfo()
        {
            SrcRect = new Vector4(0, 0, 1, 1),
        };

        ComPtr<ID3D11ShaderResourceView> srv1; // yuv420p-y、nv12-y、rgb
        ComPtr<ID3D11ShaderResourceView> srv2; // yuv420p-u、nv12-uv
        ComPtr<ID3D11ShaderResourceView> srv3; // yuv420p-v

        protected ComPtr<ID3D11Device1> Device => hwctx.Device;
        protected ComPtr<ID3D11DeviceContext1> Context => hwctx.Context;

        public Dx11RendererBase(Dx11Context ctx)
        {
            hwctx = ctx;
            frameTransfer = new FrameTransfer(ctx);
            InitDefaultShaderData();
        }

        private unsafe void InitDefaultShaderData()
        {
            HResult hr;

            // 顶点数据
            var vertexDesc = new BufferDesc()
            {
                ByteWidth = (uint)(sizeof(VertexData) * vertices.Length),
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.VertexBuffer,
            };
            fixed (VertexData* vertexData = vertices)
            {
                SubresourceData vertexSubData = default;
                vertexSubData.PSysMem = vertexData;
                hr = Device.CreateBuffer(ref vertexDesc, ref vertexSubData, ref vertexBuffer);
                hr.ThrowIfFailure();
            }

            // 顶点索引
            var indicesDesc = new BufferDesc()
            {
                ByteWidth = sizeof(float) * (uint)indices.Length,
                BindFlags = (uint)BindFlag.IndexBuffer,
                Usage = Usage.Default,
            };
            fixed (int* indicesData = indices)
            {
                SubresourceData indicesSubData = default;
                indicesSubData.PSysMem = indicesData;
                hr = Device.CreateBuffer(ref vertexDesc, ref indicesSubData, ref indexBuffer);
                hr.ThrowIfFailure();
            }

            ComPtr<ID3D10Blob> vertexCode = default;
            try
            {
                hr = DirectxHelper.CompilerShader(VertexSource, "vs_main", hwctx.VsShaderMode, ref vertexCode, out string error);
                hr.ThrowIfFailure();
                uint size = (uint)vertexCode.GetBufferSize();
                hr = Device.CreateVertexShader(vertexCode.GetBufferPointer(), vertexCode.GetBufferSize(), ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vertexShader);
                hr.ThrowIfFailure();

                byte* position = stackalloc byte[] { (byte)'P', (byte)'O', (byte)'S', (byte)'I', (byte)'T', (byte)'I', (byte)'O', (byte)'N', 0 };
                byte* texcoord = stackalloc byte[] { (byte)'T', (byte)'E', (byte)'X', (byte)'C', (byte)'O', (byte)'O', (byte)'R', (byte)'D', 0 };
                InputElementDesc[] inputElement = new[]
                {
                    new InputElementDesc()
                    {
                        SemanticName = position,
                        SemanticIndex = 0,
                        Format = Format.FormatR32G32B32Float,
                        InputSlot = 0,
                        AlignedByteOffset = 0,
                        InputSlotClass = InputClassification.PerVertexData,
                        InstanceDataStepRate = 0
                    },
                    new InputElementDesc()
                    {
                        SemanticName = texcoord,
                        SemanticIndex = 0,
                        Format = Format.FormatR32G32Float,
                        InputSlot = 0,
                        AlignedByteOffset = 12,
                        InputSlotClass = InputClassification.PerVertexData,
                        InstanceDataStepRate = 0
                    },
                };
                hr = Device.CreateInputLayout(ref inputElement[0], (uint)inputElement.Length, vertexCode.GetBufferPointer(), vertexCode.GetBufferSize(), ref vertexInputLayout);
                hr.ThrowIfFailure();
            }
            finally
            {
                vertexCode.Dispose();
            }

            // 采样器
            var samplerDesc = new SamplerDesc()
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MaxAnisotropy = 16,
                MaxLOD = float.MaxValue,
            };
            hr = Device.CreateSamplerState(ref samplerDesc, ref psSampler);
            hr.ThrowIfFailure();
        }

        private string CreatePixelShaderCode(TextureType type)
        {
            string input, readTexture;
            switch (type)
            {
                case TextureType.RGB:
                    input = @"
Texture2D<float3> tex0 : t0;";
                    readTexture = @"
float3 color = tex0.Sample(splr, uv);
return float4(color, 1);";
                    break;
                case TextureType.RGBA:
                    input = @"
Texture2D<float4> tex0 : t0;";
                    readTexture = @"
return tex0.Sample(splr, uv);";
                    break;
                case TextureType.YUVHW:
                    input = @"
Texture2D<float> tex0 : t0;
Texture2D<float2> tex1 : t1;";
                    readTexture = @"
float colorY = tex0.Sample(splr, uv);
float2 colorUV = tex1.Sample(splr, uv);
return float4(colorY, colorUV, 1);";
                    break;
                case TextureType.YUVSW:
                    input = @"
Texture2D<float> tex0 : t0;
Texture2D<float> tex1 : t1;
Texture2D<float> tex2 : t2;";
                    readTexture = @"
float colorY = tex0.Sample(splr, uv);
float colorU = tex1.Sample(splr, uv);
float colorV = tex2.Sample(splr, uv);
return float4(colorY, colorU, colorV, 1);";
                    break;
                default:
                    return null;
            }
            return string.Format(PixelSource, input, readTexture);
        }

        protected abstract override void upload_texture(VideoFrame frame);

        protected void upload_texture(VideoFrame frame, ComPtr<ID3D11RenderTargetView> renderTarget, Viewport viewport)
        {
            if (renderTarget.IsEmpty())
                return;
            FrameTextureDesc desc = default;
            if (!frameTransfer.GetTexture(frame, ref desc))
                return;
            UpdateShaderData(frame, desc);
            CreateShaderResource(desc);
            Renderer(renderTarget, viewport);
        }

        private unsafe void UpdateShaderData(VideoFrame frame, FrameTextureDesc desc)
        {
            HResult hr;

            // 画面源范围（常量缓冲区）
            if (rectBuffer.IsEmpty() || shaderInfo.SrcRect != desc.srcRect)
            {
                shaderInfo.SrcRect = desc.srcRect;
                var rectDesc = new BufferDesc()
                {
                    ByteWidth = (uint)sizeof(Vector4),
                    BindFlags = (uint)BindFlag.ConstantBuffer,
                    Usage = Usage.Default,
                };
                SubresourceData rectSubData = default;
                rectSubData.PSysMem = &desc.srcRect;
                hr = Device.CreateBuffer(ref rectDesc, ref rectSubData, ref rectBuffer);
                hr.ThrowIfFailure();
            }

            // 像素着色器
            int colorDepth = GetColorDepth(desc.tex1.desc.Format);
            ColorRange colorRange = GetColorRange(frame);
            if (shaderInfo.Type != desc.type ||
                shaderInfo.ColorDepth != colorDepth ||
                shaderInfo.ColorRange != colorRange ||
                pixelShader.IsEmpty())
            {
                string shaderStr = CreatePixelShaderCode(desc.type);
                ComPtr<ID3D10Blob> shaderCode = default;
                try
                {
                    hr = DirectxHelper.CompilerShader(shaderStr, "ps_main", hwctx.PsShaderMode, ref shaderCode, out string error);
                    hr.ThrowIfFailure();
                    hr = Device.CreatePixelShader(shaderCode.GetBufferPointer(), shaderCode.GetBufferSize(), ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref pixelShader);
                    hr.ThrowIfFailure();
                }
                finally
                {
                    shaderCode.Dispose();
                }
            }

            // 颜色转换矩阵（常量缓冲区）
            if (shaderInfo.Type != desc.type ||
                shaderInfo.ColorDepth != colorDepth ||
                shaderInfo.ColorRange != colorRange ||
                shaderInfo.ColorSpace != frame.colorspace ||
                matrixBuffer.IsEmpty())
            {
                var bufferDesc = new BufferDesc()
                {
                    ByteWidth = sizeof(float) * 4 * 4,
                    BindFlags = (uint)BindFlag.ConstantBuffer,
                    Usage = Usage.Default,
                };
                // 没有可靠又简单的手段检测颜色参数，在未标明时，
                // 也许应该默认YUV类都是有限范围，RGB类都是全范围
                // 通过检测AV_PIX_FMT_FLAG_RGB判断YUV或RGB
                Matrix4x4 mat;
                if (desc.type == TextureType.RGB || desc.type == TextureType.RGBA)
                    mat = colorRange == ColorRange.Full ? Matrix4x4.Identity : ColorMatrix.GetMatLimitToFull(colorDepth, false);
                else
                    mat = ColorMatrix.GetColorMatrix(frame, colorDepth);
                Matrix4x4* mp = &mat;
                SubresourceData subresourceData = default;
                subresourceData.PSysMem = mp;
                hr = Device.CreateBuffer(ref bufferDesc, ref subresourceData, ref matrixBuffer);
                hr.ThrowIfFailure();
            }

            shaderInfo.Type = desc.type;
            shaderInfo.ColorDepth = colorDepth;
            shaderInfo.ColorRange = colorRange;
            shaderInfo.ColorSpace = frame.colorspace;
        }

        private unsafe void CreateShaderResource(FrameTextureDesc desc)
        {
            HResult hr;
            // RGBX格式使用单个texture
            // YUVHW(NV12等)使用单个texture，并使用两个特定格式的资源视图来处理
            // YUVSW则分别对Y\U\V使用三个texture
            if (shaderInfo.IsRGB)
            {
                var srvDescRGB = new ShaderResourceViewDesc(desc.tex1.desc.Format, D3DSrvDimension.D3D11SrvDimensionTexture2D, texture2D: new Tex2DSrv(0, 1));
                hr = Device.CreateShaderResourceView(desc.tex1.texture, ref srvDescRGB, ref srv1);
                hr.ThrowIfFailure();
            }
            else if (shaderInfo.Type == TextureType.YUVHW)
            {
                Format format1, format2;
                if (shaderInfo.ColorDepth == 8)
                {
                    format1 = Format.FormatR8Unorm;
                    format2 = Format.FormatR8G8Unorm;
                }
                else
                {
                    // 对于P010跟P016都是用16位处理
                    format1 = Format.FormatR16Unorm;
                    format2 = Format.FormatR16G16Unorm;
                }
                D3DSrvDimension dimension = desc.tex1.desc.ArraySize == 1 ? D3DSrvDimension.D3D11SrvDimensionTexture2D : D3DSrvDimension.D3D11SrvDimensionTexture2Darray;
                var srvArray = new Tex2DArraySrv(0, desc.tex1.desc.MipLevels, desc.index1, 1);
                var srvDescY = new ShaderResourceViewDesc(format1, dimension, texture2DArray: srvArray);
                var srvDescUV = new ShaderResourceViewDesc(format2, dimension, texture2DArray: srvArray);
                hr = Device.CreateShaderResourceView(desc.tex1.texture, ref srvDescY, ref srv1);
                hr.ThrowIfFailure();
                hr = Device.CreateShaderResourceView(desc.tex1.texture, ref srvDescUV, ref srv2);
                hr.ThrowIfFailure();
            }
            else if (shaderInfo.Type == TextureType.YUVSW)
            {
                var srvDesc = new ShaderResourceViewDesc(desc.tex1.desc.Format, D3DSrvDimension.D3D11SrvDimensionTexture2D, texture2D: new Tex2DSrv(0, 1));
                hr = Device.CreateShaderResourceView(desc.tex1.texture, ref srvDesc, ref srv1);
                hr.ThrowIfFailure();
                hr = Device.CreateShaderResourceView(desc.tex2.texture, ref srvDesc, ref srv2);
                hr.ThrowIfFailure();
                hr = Device.CreateShaderResourceView(desc.tex3.texture, ref srvDesc, ref srv3);
                hr.ThrowIfFailure();
            }
        }

        private void Renderer(ComPtr<ID3D11RenderTargetView> renderTarget, Viewport viewport)
        {
            Context.OMSetRenderTargets(1, ref renderTarget, ref Unsafe.NullRef<ID3D11DepthStencilView>());
            Context.RSSetViewports(1, ref viewport);
            Context.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
            Context.IASetInputLayout(vertexInputLayout);
            uint stride = 20; // sizeof(VertexData);
            uint offset = 0;
            Context.IASetVertexBuffers(0, 1, ref vertexBuffer, ref stride, ref offset);
            Context.IASetIndexBuffer(indexBuffer, Format.FormatR32Uint, 0);
            Context.VSSetShader(vertexShader, ref Unsafe.NullRef<ComPtr<ID3D11ClassInstance>>(), 0);
            Context.VSSetConstantBuffers(0, 1, ref rectBuffer);

            Context.PSSetShader(pixelShader, ref Unsafe.NullRef<ComPtr<ID3D11ClassInstance>>(), 0);
            unsafe
            {
                if (shaderInfo.IsRGB)
                {
                    ID3D11ShaderResourceView** srvs = stackalloc[] { srv1.Handle };
                    Context.PSSetShaderResources(0, 1, srvs);
                }
                else if (shaderInfo.Type == TextureType.YUVHW)
                {
                    ID3D11ShaderResourceView** srvs = stackalloc[] { srv1.Handle, srv2.Handle };
                    Context.PSSetShaderResources(0, 2, srvs);
                }
                else
                {
                    ID3D11ShaderResourceView** srvs = stackalloc[] { srv1.Handle, srv2.Handle, srv3.Handle };
                    Context.PSSetShaderResources(0, 3, srvs);
                }
            }
            Context.PSSetConstantBuffers(0, 1, ref matrixBuffer);
            Context.PSSetSamplers(0, 1, ref psSampler);

            Context.DrawIndexed((uint)indices.Length, 0, 0);
            Context.ClearState();
            DisposeResource(ref srv1);
            DisposeResource(ref srv2);
            DisposeResource(ref srv3);
        }

        private static int GetColorDepth(Format format)
        {
            switch (format)
            {
                case Format.FormatNV12:
                case Format.FormatR8G8B8A8Unorm:
                    return 8;
                case Format.FormatP010:
                case Format.FormatR10G10B10A2Unorm:
                    return 10;
                case Format.FormatP016:
                case Format.FormatR16G16B16A16Unorm:
                    return 16;
                case Format.FormatR8Unorm:
                    return 8;
                case Format.FormatR16Unorm:
                    return 16;
                default:
                    throw new NotSupportedException();
            }
        }

        private static ColorRange GetColorRange(VideoFrame frame)
        {
            if (frame.color_range == FFmpeg.AutoGen.AVColorRange.AVCOL_RANGE_JPEG)
                return ColorRange.Full;
            return ColorRange.Limit;
        }

        public static bool IsInputFormatSupported(Format format)
        {
            switch (format)
            {
                case Format.FormatNV12:
                case Format.FormatP010:
                case Format.FormatP016:
                    return true;
                case Format.FormatR8G8B8A8Unorm:
                case Format.FormatR10G10B10A2Unorm:
                case Format.FormatR16G16B16A16Unorm:
                    return true;
                default:
                    return false;
            }
        }

        protected static void DisposeResource<T>(ref ComPtr<T> resource) where T : unmanaged, IComVtbl<T>
        {
            if (!resource.IsEmpty())
                resource.Dispose();
            resource = default;
        }
    }
}
