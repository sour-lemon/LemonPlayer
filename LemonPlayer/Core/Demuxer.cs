using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Core
{
    internal unsafe class Demuxer
    {
        readonly FFMediaPlayer vs;
        readonly GCHandle handle;
        readonly AVIOInterruptCB_callback interrupt_cb;
        internal readonly PacketQueue videoq;
        internal readonly PacketQueue subtitleq;
        internal readonly PacketQueue audioq;

        internal string filename { get; private set; }
        AVInputFormat* iformat;
        Thread read_tid;
        int abort_request;
        bool last_paused;
        bool queue_attachments_req;
        bool seek_req;
        long seek_pos;
        internal int read_pause_return;

        internal AVFormatContext* ic;
        bool realtime;
        // 帧的最大持续时间，超过这个时间就认为帧不连续了
        internal double max_frame_duration;

        //// 从这个时间开始播放
        //long start_time = AV_NOPTS_VALUE;
        //// 只播放指定长度的内容
        //long duration = AV_NOPTS_VALUE;
        // 生成丢失的pts，即使需要解析未来的帧
        bool genpts = false;
        // 播放完毕后是否自动退出
        bool autoexit = false;
        // 循环播放的数量
        internal int loop = 1;
        // 通过预读取来分析流信息，需要消耗一定时间
        bool find_stream_info = true;

        int video_stream, audio_stream, subtitle_stream;
        int last_video_stream, last_audio_stream, last_subtitle_stream;
        internal AVStream* video_st, audio_st, subtitle_st;
        internal SDL_cond continue_read_thread;

        public Demuxer(FFMediaPlayer player)
        {
            vs = player;
            handle = GCHandle.Alloc(this);
            interrupt_cb = decode_interrupt_cb;

            videoq = new PacketQueue();
            audioq = new PacketQueue();
            subtitleq = new PacketQueue();
        }

        internal void stream_seek(long pos)
        {
            if (!seek_req)
            {
                seek_pos = pos;
                seek_req = true;
                SDL_CondSignal(continue_read_thread);
            }
        }

        internal bool stream_open(string filename, AVInputFormat* iformat)
        {
            abort_request = 0;
            last_video_stream = video_stream = -1;
            last_audio_stream = audio_stream = -1;
            last_subtitle_stream = subtitle_stream = -1;
            queue_attachments_req = true;
            this.filename = filename;
            this.iformat = iformat;
            AVFormatContext* ic = null;
            AVDictionary* format_opts = null;
            int err, i, ret;
            Span<int> st_index = stackalloc int[AVMEDIA_TYPE_NB];
            AVDictionaryEntry* t;
            if (string.IsNullOrWhiteSpace(filename))
                goto fail;
            bool scan_all_pmts_set = false;
            st_index.Fill(-1);

            if (videoq.packet_queue_init() < 0 ||
                audioq.packet_queue_init() < 0 ||
                subtitleq.packet_queue_init() < 0)
                goto fail;
            continue_read_thread = SDL_CreateCond();

            ic = avformat_alloc_context();
            if (ic == null)
            {
                av_log(null, AV_LOG_FATAL, "Could not allocate context.\n");
                ret = AVERROR(ENOMEM);
                goto fail;
            }
            ic->interrupt_callback.callback = interrupt_cb;
            ic->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(handle);
            if (av_dict_get(format_opts, "scan_all_pmts", null, AV_DICT_MATCH_CASE) == null)
            {
                av_dict_set(&format_opts, "scan_all_pmts", "1", AV_DICT_DONT_OVERWRITE);
                scan_all_pmts_set = true;
            }
            err = avformat_open_input(&ic, filename, iformat, &format_opts);
            if (err < 0)
            {
                av_log(null, AV_LOG_ERROR, $"{filename}: {av_err2str(err)}\n");
                ret = -1;
                goto fail;
            }
            if (scan_all_pmts_set)
                av_dict_set(&format_opts, "scan_all_pmts", null, AV_DICT_MATCH_CASE);

            if ((t = av_dict_get(format_opts, "", null, AV_DICT_IGNORE_SUFFIX)) != null)
            {
                av_log(null, AV_LOG_ERROR, $"Option {tostr(t->key)} not found.\n");
                ret = AVERROR_OPTION_NOT_FOUND;
                goto fail;
            }
            this.ic = ic;

            if (genpts)
                ic->flags |= AVFMT_FLAG_GENPTS;

            av_format_inject_global_side_data(ic);

            if (find_stream_info)
            {
                err = avformat_find_stream_info(ic, null);
                if (err < 0)
                {
                    av_log(null, AV_LOG_WARNING, $"{filename}: could not find codec parameters\n");
                    ret = -1;
                    goto fail;
                }
            }

            if (ic->pb != null)
                ic->pb->eof_reached = 0; // FIXME hack, ffplay maybe should not use avio_feof() to test for the end

            max_frame_duration = (ic->iformat->flags & AVFMT_TS_DISCONT) != 0 ? 10.0 : 3600.0;

            ///* if seeking requested, we execute it */
            //if (start_time != AV_NOPTS_VALUE)
            //{
            //    long timestamp;

            //    timestamp = start_time;
            //    /* add the stream start time */
            //    if (ic->start_time != AV_NOPTS_VALUE)
            //        timestamp += ic->start_time;
            //    ret = avformat_seek_file(ic, -1, long.MinValue, timestamp, long.MaxValue, 0);
            //    if (ret < 0)
            //        av_log(null, AV_LOG_WARNING, $"{filename}: could not seek to position {(double)timestamp / AV_TIME_BASE:0.000}\n");
            //}

            realtime = is_realtime(ic);

            if (vs.show_status != 0)
                av_dump_format(ic, 0, filename, 0);

            for (i = 0; i < ic->nb_streams; i++)
            {
                AVStream* st = ic->streams[i];
                AVMediaType type = st->codecpar->codec_type;
                st->discard = AVDiscard.AVDISCARD_ALL;
            }

            if (!vs.video_disable_internal)
                st_index[AVMEDIA_TYPE_VIDEO] =
                    av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO,
                                        st_index[AVMEDIA_TYPE_VIDEO], -1, null, 0);
            if (!vs.audio_disable_internal)
                st_index[AVMEDIA_TYPE_AUDIO] =
                    av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO,
                                        st_index[AVMEDIA_TYPE_AUDIO],
                                        st_index[AVMEDIA_TYPE_VIDEO],
                                        null, 0);
            if (!vs.video_disable_internal && !vs.subtitle_disable_internal)
                st_index[AVMEDIA_TYPE_SUBTITLE] =
                    av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                                        st_index[AVMEDIA_TYPE_SUBTITLE],
                                        st_index[AVMEDIA_TYPE_AUDIO] >= 0 ?
                                         st_index[AVMEDIA_TYPE_AUDIO] :
                                         st_index[AVMEDIA_TYPE_VIDEO],
                                        null, 0);

            if (st_index[AVMEDIA_TYPE_VIDEO] >= 0)
            {
                AVStream* st = ic->streams[st_index[AVMEDIA_TYPE_VIDEO]];
                AVCodecParameters* codecpar = st->codecpar;
                AVRational sar = av_guess_sample_aspect_ratio(ic, st, null);
            }

            video_stream = last_video_stream = st_index[AVMEDIA_TYPE_VIDEO];
            audio_stream = last_audio_stream = st_index[AVMEDIA_TYPE_AUDIO];
            subtitle_stream = last_subtitle_stream = st_index[AVMEDIA_TYPE_SUBTITLE];

            if (video_stream < 0 && audio_stream < 0)
            {
                av_log(null, AV_LOG_FATAL, $"Failed to open file '{filename}' or configure filtergraph\n");
                ret = -1;
                goto fail;
            }

            if (video_stream > -1)
            {
                videoq.packet_queue_start();
                video_st = ic->streams[video_stream];
            }
            if (audio_stream > -1)
            {
                audioq.packet_queue_start();
                audio_st = ic->streams[audio_stream];
            }
            if (subtitle_stream > -1)
            {
                subtitleq.packet_queue_start();
                subtitle_st = ic->streams[subtitle_stream];
            }
            return true;
        fail:
            if (ic != null && this.ic == null)
                avformat_close_input(&ic);
            return false;
        }

        internal void Start()
        {
            var thread = new Thread(read_thread);
            thread.IsBackground = true;
            thread.Start();
            read_tid = thread;
        }

        internal void Close()
        {
            /* XXX: use a special url_shutdown call to abort parse cleanly */
            abort_request = 1;
            read_tid.Join();
            for (int i = 0; i < ic->nb_streams; i++)
            {
                ic->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }
            audio_st = null;
            audio_stream = -1;
            video_st = null;
            video_stream = -1;
            subtitle_st = null;
            subtitle_stream = -1;
            fixed (AVFormatContext** ptr = &ic)
                avformat_close_input(ptr);
            videoq.packet_queue_destroy();
            audioq.packet_queue_destroy();
            subtitleq.packet_queue_destroy();
        }

        void read_thread()
        {
            int ret;
            bool eof = false;
            bool all_eof = false;
            AVPacket* pkt;
            long stream_start_time;
            SDL_mutex wait_mutex = SDL_CreateMutex();
            long pkt_ts;

            pkt = av_packet_alloc();
            if (pkt == null)
            {
                av_log(null, AV_LOG_FATAL, "Could not allocate packet.\n");
                ret = AVERROR(ENOMEM);
                goto fail;
            }

            while (true)
            {
                if (abort_request != 0)
                    break;
                if (vs.paused != last_paused)
                {
                    last_paused = vs.paused;
                    if (vs.paused)
                        read_pause_return = av_read_pause(ic);
                    else
                        av_read_play(ic);
                }
                if (vs.paused &&
                    (!strcmp(ic->iformat->name, "rtsp") ||
                    (ic->pb != null && (filename?.StartsWith("mmsh:") == false))))
                {
                    /* wait 10 ms to avoid trying to get another packet */
                    /* XXX: horrible */
                    Thread.Sleep(10);
                    continue;
                }
                if (seek_req)
                {
                    ret = avformat_seek_file(ic, -1, long.MinValue, seek_pos, long.MaxValue, 0);
                    if (ret < 0)
                    {
                        av_log(null, AV_LOG_ERROR, $"{tostr(ic->url)}: error while seeking\n");
                    }
                    else
                    {
                        if (audio_stream >= 0)
                            audioq.packet_queue_flush();
                        if (subtitle_stream >= 0)
                            subtitleq.packet_queue_flush();
                        if (video_stream >= 0)
                            videoq.packet_queue_flush();
                    }
                    seek_req = false;
                    queue_attachments_req = true;
                    eof = false;
                    all_eof = false;
                    if (vs.paused)
                        vs.step_to_next_frame();
                }
                // 如果进行了跳转并清理了所有内容，需要重新读取专辑封面
                if (queue_attachments_req)
                {
                    if (video_st != null && (video_st->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0)
                    {
                        if ((ret = av_packet_ref(pkt, &video_st->attached_pic)) < 0)
                            goto fail;
                        videoq.packet_queue_put(pkt);
                        videoq.packet_queue_put_nullpacket(pkt, video_stream);
                    }
                    queue_attachments_req = false;
                }

                /* if the queue are full, no need to read more */
                if (!realtime &&
                    (audioq.size + videoq.size + subtitleq.size > MAX_QUEUE_SIZE ||
                    stream_has_enough_packets(audio_st, audio_stream, audioq) &&
                    stream_has_enough_packets(video_st, video_stream, videoq) &&
                    stream_has_enough_packets(subtitle_st, subtitle_stream, subtitleq)))
                {
                    /* wait 10 ms */
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                if (!vs.paused &&
                    (audio_st == null || vs.auddec.finished == audioq.serial && vs.sampq.frame_queue_nb_remaining() == 0)
                    &&
                    (video_st == null || vs.viddec.finished == videoq.serial && vs.pictq.frame_queue_nb_remaining() == 0))
                {
                    if (loop != 1 && (loop == 0 || --loop != 0))
                    {
                        //stream_seek(start_time != AV_NOPTS_VALUE ? start_time : 0);
                        stream_seek(0);
                    }
                    else if (autoexit)
                    {
                        ret = AVERROR_EOF;
                        vs.RaiseMediaEnded();
                        goto fail;
                    }
                    else if (!all_eof)
                    {
                        all_eof = true;
                        vs.RaiseMediaEnded();
                    }
                }
                ret = av_read_frame(ic, pkt);
                if (ret < 0)
                {
                    if ((ret == AVERROR_EOF || avio_feof(ic->pb) != 0) && !eof)
                    {
                        if (video_stream >= 0)
                            videoq.packet_queue_put_nullpacket(pkt, video_stream);
                        if (audio_stream >= 0)
                            audioq.packet_queue_put_nullpacket(pkt, audio_stream);
                        if (subtitle_stream >= 0)
                            subtitleq.packet_queue_put_nullpacket(pkt, subtitle_stream);
                        eof = true;
                    }
                    if (ic->pb != null && ic->pb->error != 0)
                    {
                        if (autoexit)
                            goto fail;
                        else
                            break;
                    }
                    SDL_LockMutex(wait_mutex);
                    SDL_CondWaitTimeout(continue_read_thread, wait_mutex, 10);
                    SDL_UnlockMutex(wait_mutex);
                    continue;
                }
                else
                {
                    eof = false;
                    all_eof = false;
                }
                /* check if packet is in play range specified by user, then queue, otherwise discard */
                stream_start_time = ic->streams[pkt->stream_index]->start_time;
                pkt_ts = pkt->pts == AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
                if (pkt->stream_index == audio_stream)
                {
                    audioq.packet_queue_put(pkt);
                }
                else if (pkt->stream_index == video_stream
                    && (video_st->disposition & AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    videoq.packet_queue_put(pkt);
                }
                else if (pkt->stream_index == subtitle_stream)
                {
                    subtitleq.packet_queue_put(pkt);
                }
                else
                {
                    av_packet_unref(pkt);
                }
            }

            ret = 0;
        fail:
            av_packet_free(&pkt);
        }

        ~Demuxer()
        {
            handle.Free();
            Close();
        }

        static int decode_interrupt_cb(void* ctx)
        {
            var handle = (GCHandle)(IntPtr)ctx;
            var vs = (Demuxer)handle.Target;
            return vs.abort_request;
        }

        static bool is_realtime(AVFormatContext* s)
        {
            if (strcmp(s->iformat->name, "rtp")
               || strcmp(s->iformat->name, "rtsp")
               || strcmp(s->iformat->name, "sdp")
            )
                return true;

            if (s->pb != null &&
                (strncmp(s->url, "rtp:", 4) || strncmp(s->url, "udp:", 4)))
                return true;
            return false;
        }

        static bool stream_has_enough_packets(AVStream* st, int stream_id, PacketQueue queue)
        {
            return stream_id < 0 ||
                   queue.abort_request ||
                   (st->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0 ||
                   queue.nb_packets > MIN_FRAMES && (queue.duration == 0 || av_q2d(st->time_base) * queue.duration > 1.0);
        }
    }
}
