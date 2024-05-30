using FFmpeg.AutoGen;
using System;

namespace LemonPlayer
{
    public unsafe interface IDeviceContext
    {
        AVHWDeviceType DeviceType { get; }
        AVPixelFormat HWPixelFormat { get; }
        bool ApplyDeviceContext(AVCodecContext* avctx);
        bool ApplyFrameContext(AVCodecContext* avctx);
    }

    public enum PlayerState
    {
        Playing,
        Paused,
        Stoped,
        Ended,
        Closed
    }

    public struct AudioParams
    {
        public int freq;
        public AVChannelLayout ch_layout;
        public AVSampleFormat fmt;
        public int frame_size;
        public int bytes_per_sec;
    }

    enum AV_SYNC_TYPE
    {
        AV_SYNC_AUDIO_MASTER, /* default choice */
        AV_SYNC_VIDEO_MASTER,
        AV_SYNC_EXTERNAL_CLOCK, /* synchronize to an external clock */
    };

    /// <summary>
    /// 当视频画面出现延迟时是否丢掉视频帧以追赶进度
    /// </summary>
    /// <remarks>只会丢弃非关键帧；只有在解码之后才能判断是否需要丢掉</remarks>
    public enum FrameDrop
    {
        /// <summary>
        /// 一帧也不丢
        /// </summary>
        No,
        /// <summary>
        /// 该丢就丢
        /// </summary>
        Yes,
        /// <summary>
        /// 当有音频时就可以丢
        /// </summary>
        Auto
    }

    public class StateChangedEventArgs : EventArgs
    {
        internal StateChangedEventArgs(PlayerState @new, PlayerState old)
        {
            New = @new;
            Old = old;
        }

        public PlayerState New { get; private set; }
        public PlayerState Old { get; private set; }
    }

    //unsafe void stream_cycle_channel(int codec_type)
    //{
    //    int start_index, stream_index;
    //    int old_index;
    //    AVStream* st;
    //    AVProgram* p = null;
    //    uint nb_streams = ic->nb_streams;

    //    if (codec_type == AVMEDIA_TYPE_VIDEO)
    //    {
    //        start_index = last_video_stream;
    //        old_index = video_stream;
    //    }
    //    else if (codec_type == AVMEDIA_TYPE_AUDIO)
    //    {
    //        start_index = last_audio_stream;
    //        old_index = audio_stream;
    //    }
    //    else
    //    {
    //        start_index = last_subtitle_stream;
    //        old_index = subtitle_stream;
    //    }
    //    stream_index = start_index;

    //    if (codec_type != AVMEDIA_TYPE_VIDEO && video_stream != -1)
    //    {
    //        p = av_find_program_from_stream(ic, null, video_stream);
    //        if (p != null)
    //        {
    //            nb_streams = p->nb_stream_indexes;
    //            for (start_index = 0; start_index < nb_streams; start_index++)
    //                if (p->stream_index[start_index] == stream_index)
    //                    break;
    //            if (start_index == nb_streams)
    //                start_index = -1;
    //            stream_index = start_index;
    //        }
    //    }

    //    for (; ; )
    //    {
    //        if (++stream_index >= nb_streams)
    //        {
    //            if (codec_type == AVMEDIA_TYPE_SUBTITLE)
    //            {
    //                stream_index = -1;
    //                last_subtitle_stream = -1;
    //                goto the_end;
    //            }
    //            if (start_index == -1)
    //                return;
    //            stream_index = 0;
    //        }
    //        if (stream_index == start_index)
    //            return;
    //        st = ic->streams[p != null ? p->stream_index[stream_index] : stream_index];
    //        if (st->codecpar->codec_type == (AVMediaType)codec_type)
    //        {
    //            /* check that parameters are OK */
    //            switch (codec_type)
    //            {
    //                case AVMEDIA_TYPE_AUDIO:
    //                    if (st->codecpar->sample_rate != 0 &&
    //                        st->codecpar->ch_layout.nb_channels != 0)
    //                        goto the_end;
    //                    break;
    //                case AVMEDIA_TYPE_VIDEO:
    //                case AVMEDIA_TYPE_SUBTITLE:
    //                    goto the_end;
    //                default:
    //                    break;
    //            }
    //        }
    //    }
    //the_end:
    //    if (p != null && stream_index != -1)
    //        stream_index = (int)p->stream_index[stream_index];
    //    av_log(null, AV_LOG_INFO, $"Switch {av_get_media_type_string((AVMediaType)codec_type)} stream from #{old_index} to #{stream_index}\n");

    //    stream_component_close(old_index);
    //    switch (ic->streams[stream_index]->codecpar->codec_type)
    //    {
    //        case AVMediaType.AVMEDIA_TYPE_VIDEO:
    //            viddec.stream_component_open(this, stream_index);
    //            break;
    //        case AVMediaType.AVMEDIA_TYPE_AUDIO:
    //            auddec.stream_component_open(this, stream_index);
    //            break;
    //        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
    //            subdec.stream_component_open(this, stream_index);
    //            break;
    //        default:
    //            break;
    //    }
    //}

    //void seek_chapter(int incr)
    //{
    //    long pos = (int)(get_master_clock() * AV_TIME_BASE);
    //    int i;

    //    if (ic->nb_chapters == 0)
    //        return;

    //    /* find the current chapter */
    //    for (i = 0; i < ic->nb_chapters; i++)
    //    {
    //        AVChapter* ch = ic->chapters[i];
    //        if (av_compare_ts(pos, av_get_time_base_q(), ch->start, ch->time_base) < 0)
    //        {
    //            i--;
    //            break;
    //        }
    //    }

    //    i += incr;
    //    i = Math.Max(i, 0);
    //    if (i >= ic->nb_chapters)
    //        return;

    //    av_log(null, AV_LOG_VERBOSE, $"Seeking to chapter {i}.\n");
    //    stream_seek(av_rescale_q(ic->chapters[i]->start, ic->chapters[i]->time_base, av_get_time_base_q()));
    //}
}
