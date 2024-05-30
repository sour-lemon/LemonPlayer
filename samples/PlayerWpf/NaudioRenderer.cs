using FFmpeg.AutoGen;
using LemonPlayer.Renderer;
using NAudio.Wave;

namespace LemonPlayer
{
    class NaudioRenderer : AudioRendererBase
    {
        readonly WaveOutEvent output;
        readonly MyWaveProvider myProvider;
        FFMediaPlayer opaque;

        WaveFormat Format => output.OutputWaveFormat;

        public NaudioRenderer()
        {
            output = new WaveOutEvent();
            output.DesiredLatency = 100;
            myProvider = new MyWaveProvider(this, new WaveFormat(48000, 16, 2));
            output.Init(myProvider);
        }

        public override void Start()
        {
            output.Play();
        }

        public override void Stop()
        {
            output.Stop();
        }

        protected override unsafe int OpenAudioDevice(FFMediaPlayer opaque, AVChannelLayout* wanted_channel_layout, int wanted_sample_rate, AudioParams* audio_hw_params)
        {
            this.opaque = opaque;
            ffmpeg.av_channel_layout_uninit(wanted_channel_layout);
            ffmpeg.av_channel_layout_default(wanted_channel_layout, Format.Channels);
            if (wanted_channel_layout->order != AVChannelOrder.AV_CHANNEL_ORDER_NATIVE)
            {
                ffmpeg.av_channel_layout_uninit(wanted_channel_layout);
                ffmpeg.av_channel_layout_default(wanted_channel_layout, Format.Channels);
            }
            if (Format.SampleRate <= 0 || Format.Channels <= 0)
            {
                av_log(ffmpeg.AV_LOG_ERROR, "Invalid sample rate or channel count!");
                return -1;
            }
            audio_hw_params->fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            audio_hw_params->freq = Format.SampleRate;
            if (ffmpeg.av_channel_layout_copy(&audio_hw_params->ch_layout, wanted_channel_layout) < 0)
                return -1;
            audio_hw_params->frame_size = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params->ch_layout.nb_channels, 1, audio_hw_params->fmt, 1);
            audio_hw_params->bytes_per_sec = ffmpeg.av_samples_get_buffer_size(null, audio_hw_params->ch_layout.nb_channels, audio_hw_params->freq, audio_hw_params->fmt, 1);
            if (audio_hw_params->bytes_per_sec <= 0 || audio_hw_params->frame_size <= 0)
            {
                av_log(ffmpeg.AV_LOG_ERROR, "av_samples_get_buffer_size failed");
                return -1;
            }
            return Format.ConvertLatencyToByteSize(100);
        }

        class MyWaveProvider : IWaveProvider
        {
            private readonly NaudioRenderer output;
            private readonly WaveFormat outputFormat;

            public MyWaveProvider(NaudioRenderer output, WaveFormat outputFormat)
            {
                this.output = output;
                this.outputFormat = outputFormat;
            }

            public WaveFormat WaveFormat => outputFormat;

            public unsafe int Read(byte[] buffer, int offset, int count)
            {
                fixed (byte* ptr = buffer)
                    return output.sdl_audio_callback(output.opaque, ptr + offset, count);
            }
        }
    }
}
