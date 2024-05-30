using LemonPlayer;
using LemonPlayer.Renderer;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayerWpf
{
    internal class WpfPlayer : WpfPlayerBase
    {
        private WriteableBitmap bitmap;
        private RendererBridge videoRenderer;

        public WpfPlayer()
        {
            bitmap = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null);
        }

        protected override FFMediaPlayer CreatePlayer()
        {
            videoRenderer = new RendererBridge();
            return new FFMediaPlayer(AudioRenderer, videoRenderer, null);
        }

        protected override void DoRenderer()
        {
            if (videoRenderer.stoped)
                return;
            var size = NativeSize;
            if (bitmap.PixelWidth != size.Width || bitmap.PixelHeight != size.Height)
            {
                bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
                Host.Source = bitmap;
            }
            videoRenderer.DoRenderer(bitmap);
        }

        class RendererBridge : VideoRendererBase
        {
            private readonly SwsTransfer swsTransfer;
            private FFMediaPlayer player;
            private WriteableBitmap bitmap;
            internal bool stoped = true;

            public RendererBridge()
            {
                swsTransfer = new SwsTransfer();
            }

            public void DoRenderer(WriteableBitmap bitmap)
            {
                this.bitmap = bitmap;
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

            protected unsafe override void upload_texture(VideoFrame frame)
            {
                bool ret = swsTransfer.SwsScale(frame.frame, bitmap.PixelWidth, bitmap.PixelHeight, FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_BGRA, (void*)bitmap.BackBuffer, bitmap.BackBufferStride);
                if (ret)
                {
                    bitmap.Lock();
                    bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                    bitmap.Unlock();
                }
            }
        }
    }
}
