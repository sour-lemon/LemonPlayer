using LemonPlayer;
using LemonPlayer.Windows;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D9;
using Silk.NET.DXGI;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace PlayerWpf
{
    internal class WpfPlayer2 : WpfPlayerBase
    {
        private readonly D3DImage bitmap = new D3DImage();
        private Dx11Context hwctx;
        private RendererBridge videoRenderer;

        public WpfPlayer2()
        {
            Host.Source = bitmap;
        }

        protected override FFMediaPlayer CreatePlayer()
        {
            DirectxHelper.D3D11CreateDevice(out var device, out var context);
            hwctx = new Dx11Context(device, context);
            device.Release();
            context.Release();
            videoRenderer = new RendererBridge(hwctx, bitmap);
            return new FFMediaPlayer(AudioRenderer, videoRenderer, hwctx);
        }

        protected override void DoRenderer()
        {
            if (videoRenderer.stoped)
                return;
            videoRenderer.DoRenderer(NativeSize);
        }

        class RendererBridge : Dx11RendererBase
        {
            private readonly Dx11Context hwctx;
            private readonly D3DImage bitmap;
            private readonly ComPtr<IDirect3DDevice9Ex> d3d9Device;
            private FFMediaPlayer player;
            private bool updateBitmap = false;
            internal bool stoped = true;

            private Texture2DDesc desc;
            private ComPtr<ID3D11Texture2D> renderTarget;
            private ComPtr<ID3D11RenderTargetView> renderTargetView;
            private ComPtr<IDirect3DSurface9> surface;

            public RendererBridge(Dx11Context hwctx, D3DImage bitmap) : base(hwctx)
            {
                this.hwctx = hwctx;
                this.bitmap = bitmap;
                d3d9Device = D3D9Helper.CreateDx9Device();
                desc = new Texture2DDesc
                {
                    Width = 4,
                    Height = 4,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Silk.NET.DXGI.Format.FormatB8G8R8A8Unorm, // WPF DX9支持
                    SampleDesc = new SampleDesc(1, 0),
                    Usage = Usage.Default,
                    BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.RenderTarget),
                    CPUAccessFlags = (uint)CpuAccessFlag.None,
                    MiscFlags = (uint)ResourceMiscFlag.Shared,
                };
            }

            public void DoRenderer(SizeInt size)
            {
                if (desc.Width != size.Width || desc.Height != size.Height || renderTarget.IsEmpty())
                {
                    renderTarget.Dispose();
                    renderTargetView.Dispose();
                    surface.Dispose();
                    desc.Width = (uint)size.Width;
                    desc.Height = (uint)size.Height;
                    hwctx.Device.CreateTexture2D(ref desc, ref Unsafe.NullRef<SubresourceData>(), ref renderTarget);
                    hwctx.Device.CreateRenderTargetView(renderTarget, ref Unsafe.NullRef<RenderTargetViewDesc>(), ref renderTargetView);
                    surface = D3D9Helper.OpenSharedResource(d3d9Device, renderTarget);
                    updateBitmap = true;
                }
                var time = 0.01;
                video_refresh(player, ref time);
            }

            public override void Start(FFMediaPlayer player)
            {
                this.player = player;
                stoped = false;
            }

            public override void Stop()
            {
                stoped = true;
            }

            protected override void upload_texture(VideoFrame frame)
            {
                upload_texture(frame, renderTargetView, new Viewport(0, 0, desc.Width, desc.Height, 0, 1));
                hwctx.Context.Flush();
                bitmap.Lock();
                unsafe
                {
                    if (updateBitmap)
                        bitmap.SetBackBuffer(D3DResourceType.IDirect3DSurface9, (IntPtr)surface.Handle);
                }
                bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)desc.Width, (int)desc.Height));
                bitmap.Unlock();
            }
        }
    }
}
