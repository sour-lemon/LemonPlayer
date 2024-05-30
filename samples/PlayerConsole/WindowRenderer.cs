using LemonPlayer;
using LemonPlayer.Windows;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Runtime.CompilerServices;

namespace PlayerConsole
{
    internal class WindowRenderer : Dx11RendererBase
    {
        private readonly Dx11Context hwctx;
        private readonly IWindow Window;
        private bool loaded;

        ComPtr<IDXGISwapChain1> swapChain;
        SwapChainDesc1 swapChainDesc;

        private FFMediaPlayer player;
        internal bool stoped = true;

        public event Action OnRendering;

        public WindowRenderer(Dx11Context hwctx, string title, int width, int height)
            : base(hwctx)
        {
            this.hwctx = hwctx;
            var option = WindowOptions.Default;
            option.API = GraphicsAPI.None;
            option.Title = title;
            option.Size = new Vector2D<int>(width, height);
            option.WindowBorder = WindowBorder.Hidden;
            option.WindowBorder = WindowBorder.Resizable;
            Window = Silk.NET.Windowing.Window.Create(option);
            Window.Load += Window_Load;
            Window.Render += Window_Render;
        }

        private void Window_Load()
        {
            swapChainDesc = new SwapChainDesc1
            {
                Width = 640,
                Height = 480,
                Format = Format.FormatB8G8R8A8Unorm,
                SampleDesc = new SampleDesc(1, 0),
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard,
            };
            DirectxHelper.DXGIFactory.CreateSwapChainForHwnd(
                hwctx.Device,
                Window.Native!.DXHandle!.Value,
                ref swapChainDesc,
                ref Unsafe.NullRef<SwapChainFullscreenDesc>(),
                ref Unsafe.NullRef<IDXGIOutput>(),
                ref swapChain);
            loaded = true;
        }

        private void Window_Render(double obj)
        {
            OnRendering?.Invoke();
            if (stoped || !loaded) return;
            var time = 0.01;
            video_refresh(player, ref time);
        }

        public void Show()
        {
            Window.Run();
        }

        public void Close()
        {
            loaded = false;
            Window.Close();
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
            var size = Window.Size;
            if (swapChainDesc.Width != size.X || swapChainDesc.Height != size.Y)
            {
                swapChainDesc.Width = (uint)size.X;
                swapChainDesc.Height = (uint)size.Y;
                swapChain.ResizeBuffers(swapChainDesc.BufferCount, swapChainDesc.Width, swapChainDesc.Height, swapChainDesc.Format, 0);
            }

            var framebuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            ComPtr<ID3D11RenderTargetView> renderTargetView = default;
            hwctx.Device.CreateRenderTargetView(framebuffer, ref Unsafe.NullRef<RenderTargetViewDesc>(), ref renderTargetView);
            upload_texture(frame, renderTargetView, new Viewport(0, 0, swapChainDesc.Width, swapChainDesc.Height, 0, 1));
            swapChain.Present(0, 0);

            renderTargetView.Dispose();
            framebuffer.Dispose();
        }
    }
}
