using FFmpeg.AutoGen;
using System;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Renderer
{
    public unsafe abstract class AudioRendererBase : IDisposable
    {
        SwrContext* swr_ctx;
        long audio_callback_time;

        double audio_clock;
        int audio_clock_serial;
        double audio_diff_cum; /* used for AV difference average computation */
        double audio_diff_avg_coef;
        double audio_diff_threshold;
        int audio_diff_avg_count;
        int audio_hw_buf_size;
        byte* audio_buf;
        byte* audio_buf1;
        int audio_buf_size; /* in bytes */
        uint audio_buf1_size;
        int audio_buf_index; /* in bytes */
        int audio_write_buf_size;
        bool muted;
        AudioParams audio_src;
        AudioParams audio_tgt;

        private float volume = 1.0f;
        /// <summary>
        /// 设置音量，可以大于1，但小于0的值将被忽略
        /// </summary>
        /// <remarks>过大的值可能导致未知的问题</remarks>
        public float Volume
        {
            get => volume;
            set => volume = Math.Max(value, 0);
        }
        public bool Muted { get => muted; set => muted = value; }

        /* return the wanted number of samples to get better sync if sync_type is video or external master clock */
        int synchronize_audio(FFMediaPlayer vs, int nb_samples)
        {
            int wanted_nb_samples = nb_samples;

            /* if not master, then we try to remove or add samples to correct the clock */
            if (vs.get_master_sync_type() != AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER)
            {
                double diff, avg_diff;
                int min_nb_samples, max_nb_samples;

                diff = vs.audclk.get_clock() - vs.get_master_clock();

                if (!isnan(diff) && fabs(diff) < AV_NOSYNC_THRESHOLD)
                {
                    audio_diff_cum = diff + audio_diff_avg_coef * audio_diff_cum;
                    if (audio_diff_avg_count < AUDIO_DIFF_AVG_NB)
                    {
                        /* not enough measures to have a correct estimate */
                        audio_diff_avg_count++;
                    }
                    else
                    {
                        /* estimate the A-V difference */
                        avg_diff = audio_diff_cum * (1.0 - audio_diff_avg_coef);

                        if (fabs(avg_diff) >= audio_diff_threshold)
                        {
                            wanted_nb_samples = nb_samples + (int)(diff * audio_src.freq);
                            min_nb_samples = ((nb_samples * (100 - SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            max_nb_samples = ((nb_samples * (100 + SAMPLE_CORRECTION_PERCENT_MAX) / 100));
                            wanted_nb_samples = av_clip(wanted_nb_samples, min_nb_samples, max_nb_samples);
                        }
                        ffmpeg.av_log(null, AV_LOG_TRACE, $"diff={diff} adiff={avg_diff} sample_diff={wanted_nb_samples - nb_samples} apts={audio_clock:0.000} {audio_diff_threshold}\n");
                    }
                }
                else
                {
                    /* too big difference : may be initial PTS errors, so
                       reset A-V filter */
                    audio_diff_avg_count = 0;
                    audio_diff_cum = 0;
                }
            }

            return wanted_nb_samples;
        }

        /**
         * Decode one audio frame and return its uncompressed size.
         *
         * The processed audio frame is decoded, converted if required, and
         * stored in vs.audio_buf, with size in bytes given by the return
         * value.
         */
        int audio_decode_frame(FFMediaPlayer vs)
        {
            int data_size, resampled_data_size;
            double audio_clock0;
            int wanted_nb_samples;
            Frame af;

            if (vs.paused)
                return -1;

            do
            {
                while (vs.sampq.frame_queue_nb_remaining() == 0)
                {
                    if ((av_gettime_relative() - audio_callback_time) > 1000000L * audio_hw_buf_size / audio_tgt.bytes_per_sec / 2)
                        return -1;
                    av_usleep(1000);
                }
                af = vs.sampq.frame_queue_peek_readable();
                if (af == null)
                    return -1;
                vs.sampq.frame_queue_next();
            } while (af.serial != vs.audioq.serial);

            data_size = av_samples_get_buffer_size(null, af.frame->ch_layout.nb_channels,
                                                   af.frame->nb_samples,
                                                   (AVSampleFormat)af.frame->format, 1);

            wanted_nb_samples = synchronize_audio(vs, af.frame->nb_samples);

            fixed (AudioParams* audio_src = &this.audio_src)
            {
                if (af.frame->format != (int)this.audio_src.fmt ||
                    av_channel_layout_compare(&af.frame->ch_layout, &audio_src->ch_layout) != 0 ||
                    af.frame->sample_rate != this.audio_src.freq ||
                    (wanted_nb_samples != af.frame->nb_samples && swr_ctx == null))
                {
                    fixed (SwrContext** swr_ctx = &this.swr_ctx)
                    {
                        swr_free(swr_ctx);
                        fixed (AudioParams* audio_tgt = &this.audio_tgt)
                            swr_alloc_set_opts2(swr_ctx,
                                                &audio_tgt->ch_layout, audio_tgt->fmt, audio_tgt->freq,
                                                &af.frame->ch_layout, (AVSampleFormat)af.frame->format, af.frame->sample_rate,
                                                0, null);
                        if (swr_ctx == null || swr_init(this.swr_ctx) < 0)
                        {
                            ffmpeg.av_log(null, AV_LOG_ERROR, $"Cannot create sample rate converter for conversion of {af.frame->sample_rate} Hz {av_get_sample_fmt_name((AVSampleFormat)af.frame->format)} {af.frame->ch_layout.nb_channels} channels to {audio_tgt.freq} Hz {av_get_sample_fmt_name(audio_tgt.fmt)} {audio_tgt.ch_layout.nb_channels} channels!\n");
                            swr_free(swr_ctx);
                            return -1;
                        }
                        if (av_channel_layout_copy(&audio_src->ch_layout, &af.frame->ch_layout) < 0)
                            return -1;
                        this.audio_src.freq = af.frame->sample_rate;
                        this.audio_src.fmt = (AVSampleFormat)af.frame->format;
                    }
                }
            }

            if (swr_ctx != null)
            {
                byte** inbuf = af.frame->extended_data;
                if (volume != 1)
                {
                    // See: AVFrame::extended_data
                    // ffmpeg解码的数据是AV_SAMPLE_FMT_FLTP
                    int length = af.frame->linesize[0] / 4;
                    for (int ch = 0; ch < af.frame->ch_layout.nb_channels; ch++)
                    {
                        float* inbuf_float = (float*)inbuf[ch];
                        for (int i = 0; i < length; i++)
                            inbuf_float[i] *= volume;
                    }
                }
                int out_count = wanted_nb_samples * audio_tgt.freq / af.frame->sample_rate + 256;
                int out_size = av_samples_get_buffer_size(null, audio_tgt.ch_layout.nb_channels, out_count, audio_tgt.fmt, 0);
                int len2;
                if (out_size < 0)
                {
                    ffmpeg.av_log(null, AV_LOG_ERROR, "av_samples_get_buffer_size() failed\n");
                    return -1;
                }
                if (wanted_nb_samples != af.frame->nb_samples)
                {
                    if (swr_set_compensation(swr_ctx, (wanted_nb_samples - af.frame->nb_samples) * audio_tgt.freq / af.frame->sample_rate,
                                                wanted_nb_samples * audio_tgt.freq / af.frame->sample_rate) < 0)
                    {
                        ffmpeg.av_log(null, AV_LOG_ERROR, "swr_set_compensation() failed\n");
                        return -1;
                    }
                }
                fixed (byte** outbuf = &audio_buf1)
                {
                    fixed (uint* audio_buf1_size = &this.audio_buf1_size)
                        av_fast_malloc(outbuf, audio_buf1_size, (ulong)out_size);
                    if (audio_buf1 == null)
                        return AVERROR(ENOMEM);
                    len2 = swr_convert(swr_ctx, outbuf, out_count, inbuf, af.frame->nb_samples);
                }
                if (len2 < 0)
                {
                    ffmpeg.av_log(null, AV_LOG_ERROR, "swr_convert() failed\n");
                    return -1;
                }
                if (len2 == out_count)
                {
                    ffmpeg.av_log(null, AV_LOG_WARNING, "audio buffer is probably too small\n");
                    if (swr_init(swr_ctx) < 0)
                        fixed (SwrContext** swr_ctx = &this.swr_ctx)
                            swr_free(swr_ctx);
                }
                audio_buf = audio_buf1;
                resampled_data_size = len2 * audio_tgt.ch_layout.nb_channels * av_get_bytes_per_sample(audio_tgt.fmt);
            }
            else
            {
                audio_buf = af.frame->data[0];
                resampled_data_size = data_size;
            }

            audio_clock0 = audio_clock;
            /* update the audio clock with the pts */
            if (!isnan(af.pts))
                audio_clock = af.pts + (double)af.frame->nb_samples / af.frame->sample_rate;
            else
                audio_clock = double.NaN;
            audio_clock_serial = af.serial;
            return resampled_data_size;
        }

        /* prepare a new audio buffer */
        protected int sdl_audio_callback(FFMediaPlayer opaque, byte* stream, int len)
        {
            FFMediaPlayer vs = opaque;
            int audio_size, len1;
            int total = 0;

            audio_callback_time = av_gettime_relative();

            while (len > 0)
            {
                if (audio_buf_index >= audio_buf_size)
                {
                    audio_size = audio_decode_frame(vs);
                    if (audio_size < 0)
                    {
                        /* if error, just output silence */
                        audio_buf = null;
                        // SDL_AUDIO_MIN_BUFFER_SIZE
                        audio_buf_size = 512 / audio_tgt.frame_size * audio_tgt.frame_size;
                    }
                    else
                    {
                        audio_buf_size = audio_size;
                    }
                    audio_buf_index = 0;
                }
                len1 = audio_buf_size - audio_buf_index;
                if (len1 > len)
                    len1 = len;
                if (!muted && audio_buf != null && volume > 0.0000001f)
                {
                    Buffer.MemoryCopy(audio_buf + audio_buf_index, stream, len1, len1);
                }
                else
                {
                    new Span<byte>(stream, len1).Fill(0);
                }
                len -= len1;
                stream += len1;
                total += len1;
                audio_buf_index += len1;
            }
            audio_write_buf_size = audio_buf_size - audio_buf_index;
            /* Let's assume the audio driver that is used by SDL has two periods. */
            if (!isnan(audio_clock))
            {
                vs.audclk.set_clock_at(audio_clock - (double)(2 * audio_hw_buf_size + audio_write_buf_size) / audio_tgt.bytes_per_sec, audio_clock_serial, audio_callback_time / 1000000.0);
            }
            return total;
        }

        internal int audio_open(FFMediaPlayer vs, AVChannelLayout* wanted_channel_layout, int wanted_sample_rate)
        {
            fixed (AudioParams* audio_tgt = &this.audio_tgt)
                audio_hw_buf_size = OpenAudioDevice(vs, wanted_channel_layout, wanted_sample_rate, audio_tgt);

            audio_src = audio_tgt;
            audio_buf_size = 0;
            audio_buf_index = 0;

            /* init averaging filter */
            audio_diff_avg_coef = Math.Exp(Math.Log(0.01) / AUDIO_DIFF_AVG_NB);
            audio_diff_avg_count = 0;
            /* since we do not have a precise anough audio FIFO fullness,
               we correct audio sync only if larger than this threshold */
            audio_diff_threshold = (double)(audio_hw_buf_size) / audio_tgt.bytes_per_sec;
            return audio_hw_buf_size;
        }

        protected abstract int OpenAudioDevice(FFMediaPlayer opaque, AVChannelLayout* wanted_channel_layout, int wanted_sample_rate, AudioParams* audio_hw_params);

        protected virtual void av_log(int level, string msg)
        {
            ffmpeg.av_log(null, level, msg);
        }

        public abstract void Start();

        public abstract void Stop();

        public void Dispose()
        {
            fixed (byte** ptr = &audio_buf1)
                av_freep(ptr);
            audio_buf1_size = 0;
            audio_buf = null;
            fixed (SwrContext** ptr = &swr_ctx)
                swr_free(ptr);
        }
    }
}
