using LemonPlayer;
using LemonPlayer.Windows;

namespace PlayerConsole
{
    internal class Program
    {
        static Dx11Context hwctx;
        static WindowRenderer videoRenderer;
        static NaudioRenderer audioRenderer;
        static FFMediaPlayer player;

        static void Main(string[] args)
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            //FFmpeg.AutoGen.ffmpeg.avdevice_register_all();
            FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_QUIET);
            // 要AOT发布的话，得加上这一句
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();

            Task.Factory.StartNew(() =>
            {
                DirectxHelper.D3D11CreateDevice(out var d3d11Device, out var d3d11Context);
                hwctx = new Dx11Context(d3d11Device, d3d11Context);
                videoRenderer = new WindowRenderer(hwctx, "视频窗口", 800, 450);
                audioRenderer = new NaudioRenderer();
                player = new FFMediaPlayer(audioRenderer, videoRenderer, hwctx);
                player.Loop = true;
                videoRenderer.Show();
            }, TaskCreationOptions.LongRunning);

            string[] exts = { ".mp4", ".mov", ".avi" };
            Console.WriteLine("*.mp4|*.mov|*.avi|");
            while (true)
            {
                var line = Console.ReadLine();
                if (line == "s" || line == "exit")
                {
                    player.Close();
                    videoRenderer.Close();
                    break;
                }
                else
                {
                    line = line.Trim('"');
                    if (File.Exists(line))
                    {
                        var ext = Path.GetExtension(line).ToLower();
                        if (exts.Contains(ext))
                        {
                            player.Open(line);
                            player.Play();
                        }
                    }
                }
            }
        }
    }
}
