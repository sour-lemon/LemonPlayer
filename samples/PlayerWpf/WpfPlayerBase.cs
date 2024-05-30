using LemonPlayer;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayerWpf
{
    struct SizeInt
    {
        public int Width;
        public int Height;
    }
    internal abstract class WpfPlayerBase : FrameworkElement
    {
        private Window hostWindow;
        private DpiScale dpi = new DpiScale(1, 1);

        protected Image Host { get; private set; }
        protected SizeInt NativeSize { get; private set; }
        protected NaudioRenderer AudioRenderer { get; private set; }
        protected FFMediaPlayer Player { get; }

        public float Volume { get => AudioRenderer.Volume; set => AudioRenderer.Volume = value; }

        public event EventHandler<StateChangedEventArgs> StateChanged;

        public WpfPlayerBase()
        {
            Host = new Image()
            {
                UseLayoutRounding = true,
                Stretch = Stretch.Fill,
                Source = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null),
            };
            AddVisualChild(Host);
            Host.SizeChanged += Host_SizeChanged;
            Loaded += WpfPlayer_Loaded;
            Unloaded += WpfPlayer_Unloaded;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            AudioRenderer = new NaudioRenderer();
            Player = CreatePlayer();
            Player.StateChanged += Player_StateChanged;
        }

        private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RefreshNativeSize();
        }

        private void WpfPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            hostWindow = Window.GetWindow(this);
            if (hostWindow == null) return; // 设计器上是这样的
            hostWindow.DpiChanged += HostWindow_DpiChanged;
            dpi = VisualTreeHelper.GetDpi(hostWindow);
            RefreshNativeSize();
        }

        private void WpfPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            if (hostWindow != null)
                hostWindow.DpiChanged -= HostWindow_DpiChanged;
            hostWindow = null;
        }

        private void HostWindow_DpiChanged(object sender, DpiChangedEventArgs e)
        {
            dpi = e.NewDpi;
            RefreshNativeSize();
        }

        private void RefreshNativeSize()
        {
            var renderWidth = (int)(ActualWidth * dpi.DpiScaleX);
            var renderHeight = (int)(ActualHeight * dpi.DpiScaleY);
            NativeSize = new SizeInt
            {
                Width = renderWidth,
                Height = renderHeight
            };
        }

        private void Player_StateChanged(object sender, StateChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => StateChanged?.Invoke(this, e));
        }

        TimeSpan lastTime;
        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            var arg = (RenderingEventArgs)e;
            if (lastTime == arg.RenderingTime)
                return;
            lastTime = arg.RenderingTime;
            if (!Host.IsLoaded || !Host.IsVisible || NativeSize.Width == 0 || NativeSize.Height == 0)
                return;
            DoRenderer();
        }

        public void Open(string filename) => Player.Open(filename);
        public void Play() => Player.Play();
        public void Pause() => Player.Pause();
        public void Close() => Player.Close();

        protected abstract FFMediaPlayer CreatePlayer();

        protected abstract void DoRenderer();

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index)
        {
            if (index == 0) return Host;
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            Host.Measure(availableSize);
            return Host.DesiredSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            Host.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
