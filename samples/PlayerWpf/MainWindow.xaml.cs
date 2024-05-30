using LemonPlayer;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace PlayerWpf
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            //FFmpeg.AutoGen.ffmpeg.avdevice_register_all();
            //FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_QUIET);
            InitializeComponent();
        }

        private void Player_StateChanged(object sender, StateChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.New)
                {
                    case PlayerState.Playing:
                        BtnPlayPause.Content = "Pause";
                        break;
                    case PlayerState.Paused:
                        BtnPlayPause.Content = "Play";
                        break;
                    case PlayerState.Stoped:
                        BtnPlayPause.Content = "Play";
                        BtnPlayPause.IsEnabled = true;
                        break;
                    case PlayerState.Ended:
                        BtnPlayPause.IsEnabled = false;
                        break;
                    case PlayerState.Closed:
                        BtnPlayPause.Content = "Play";
                        BtnPlayPause.IsEnabled = false;
                        break;
                    default:
                        BtnPlayPause.IsEnabled = false;
                        break;
                }
            });
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog();
            ofd.Filter = "视频|*.mp4;*.mov;*.avi;*.mkv;*.ts|音频|*.mp3;*.wav|图片|*.jpg;*.png";
            ofd.Multiselect = false;
            bool? result = ofd.ShowDialog();
            if (result == true)
            {
                var path = ofd.FileName;
                Player.Open(path);
                Player.Play();
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (BtnPlayPause.Content.ToString() == "Play")
            {
                Player.Play();
            }
            else
            {
                Player.Pause();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Player.Close();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Player != null)
                Player.Volume = (float)e.NewValue;
        }
    }
}