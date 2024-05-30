using FFmpeg.AutoGen;
using LemonPlayer.Core;
using LemonPlayer.Decoder;
using LemonPlayer.Renderer;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer
{
    public unsafe class FFMediaPlayer
    {
        internal readonly VideoRendererBase video_renderer;
        internal readonly AudioRendererBase audio_renderer;

        readonly Demuxer demuxer;
        internal readonly VideoDecoder viddec;
        internal readonly AudioDecoder auddec;
        internal readonly SubtitleDecoder subdec;
        internal readonly Clock audclk;
        internal readonly Clock vidclk;

        readonly ConcurrentQueue<PlayerEvent> eventq;
        readonly Thread vs_tid;
        AV_SYNC_TYPE av_sync_type = AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER;
        PlayerState state = PlayerState.Closed;
        long last_status_time;

        #region Internal Reference
        internal FrameQueue<VideoFrame> pictq => viddec.pictq;
        internal FrameQueue<AudioFrame> sampq => auddec.sampq;
        internal FrameQueue<SubtitleFrame> subpq => subdec.subpq;

        internal AVFormatContext* ic => demuxer.ic;
        internal SDL_cond continue_read_thread => demuxer.continue_read_thread;
        internal PacketQueue videoq => demuxer.videoq;
        internal PacketQueue audioq => demuxer.audioq;
        internal PacketQueue subtitleq => demuxer.subtitleq;
        internal double max_frame_duration => demuxer.max_frame_duration;
        #endregion

        internal bool paused { get; private set; }
        internal bool step { get; private set; }
        internal int frame_drops_early; //decoder丢帧的数量
        internal int frame_drops_late; //renderer丢帧的数量
        public FrameDrop FrameDrop { get; set; } = FrameDrop.Auto;
        public int FrameDropCount => frame_drops_early + frame_drops_late;
        public bool Loop
        {
            get => demuxer.loop > 1;
            set => demuxer.loop = value ? int.MaxValue : 1;
        }
        // 用户主动禁用音频
        private bool audio_disable_config;
        public bool AudioDisable
        {
            get => audio_disable_config;
            set => audio_disable_config = value;
        }

        internal bool audio_disable_internal { get => audio_renderer == null || audio_disable_config; }
        internal bool video_disable_internal => video_renderer == null;
        internal bool subtitle_disable_internal { get; private set; } = true;
        /// <summary>
        /// 是否在控制台显示一些播放信息，0：不显示，1：显示，-1：部分显示
        /// </summary>
        public int show_status { get; private set; } = -1;

        public string Source => demuxer.filename;
        public double Position => get_master_clock();
        public PlayerState State
        {
            get => state;
            private set
            {
                if (state != value)
                {
                    var old = state;
                    state = value;
                    StateChanged?.Invoke(this, new StateChangedEventArgs(value, old));
                }
            }
        }

        public event EventHandler<StateChangedEventArgs> StateChanged;

        public FFMediaPlayer(AudioRendererBase audioRenderer, VideoRendererBase videoRenderer, IDeviceContext hwctx)
        {
            if (audioRenderer == null && videoRenderer == null)
                throw new Exception("必须至少有一个渲染器");
            audio_renderer = audioRenderer;
            video_renderer = videoRenderer;

            demuxer = new Demuxer(this);
            vidclk = new Clock(demuxer.videoq);
            audclk = new Clock(demuxer.audioq);
            viddec = new VideoDecoder(this, hwctx);
            auddec = new AudioDecoder(this);
            subdec = new SubtitleDecoder(this);

            eventq = new ConcurrentQueue<PlayerEvent>();
            vs_tid = new Thread(event_loop);
            vs_tid.IsBackground = true;
            vs_tid.Start();
        }

        public void Open(string filename)
        {
            var e = PlayerEvent.Open(filename);
            eventq.Enqueue(e);
        }

        public void Play()
        {
            eventq.Enqueue(PlayerEvent.Play());
        }

        public void Pause()
        {
            eventq.Enqueue(PlayerEvent.Pause());
        }

        public void Stop()
        {
            eventq.Enqueue(PlayerEvent.Stop());
        }

        public void Close()
        {
            eventq.Enqueue(PlayerEvent.Close());
        }

        void event_loop()
        {
            while (true)
            {
                if (eventq.TryDequeue(out PlayerEvent e))
                {
                    switch (e.Action)
                    {
                        case EventAction.Open:
                            if (state != PlayerState.Closed)
                                stream_close();
                            stream_open(e.filename, e.iformat);
                            break;
                        case EventAction.Stop:
                            if (state == PlayerState.Closed || state == PlayerState.Stoped)
                                break;
                            demuxer.stream_seek(0);
                            if (state != PlayerState.Paused)
                                toggle_pause(false);
                            State = PlayerState.Stoped;
                            break;
                        case EventAction.Play:
                            if (state == PlayerState.Paused)
                            {
                                toggle_pause(true);
                            }
                            else if (state == PlayerState.Stoped)
                            {
                                if (paused)
                                    toggle_pause(false);
                                if (demuxer.audio_st != null)
                                    audio_renderer?.Start();
                                if (demuxer.video_st != null)
                                    video_renderer?.Start(this);
                                State = PlayerState.Playing;
                            }
                            break;
                        case EventAction.Pause:
                            if (state == PlayerState.Playing)
                                toggle_pause(true);
                            break;
                        case EventAction.Ended:
                            State = PlayerState.Ended;
                            break;
                        case EventAction.Close:
                            if (state != PlayerState.Closed)
                                stream_close();
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
                ShowStatus();
            }
        }

        void stream_open(string filename, AVInputFormat* iformat)
        {
            if (!demuxer.stream_open(filename, iformat))
                goto fail;

            /* start video display */
            if (pictq.frame_queue_init() < 0)
                goto fail;
            if (subpq.frame_queue_init() < 0)
                goto fail;
            if (sampq.frame_queue_init() < 0)
                goto fail;

            vidclk.init_clock();
            audclk.init_clock();

            /* open the streams */
            if (demuxer.video_st != null)
                viddec.stream_component_open(demuxer.video_st);
            if (demuxer.audio_st != null)
                auddec.stream_component_open(demuxer.audio_st);
            if (demuxer.subtitle_st != null)
                subdec.stream_component_open(demuxer.subtitle_st);

            if (demuxer.audio_st != null)
                av_sync_type = AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER;
            else
                av_sync_type = AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER;

            demuxer.Start();
            State = PlayerState.Stoped;
            return;

        fail:
            stream_close();
            return;
        }

        void toggle_pause(bool raiseEvent)
        {
            stream_toggle_pause();
            step = false;
            if (raiseEvent)
                State = paused ? PlayerState.Paused : PlayerState.Playing;
        }

        void stream_close()
        {
            audio_renderer?.Stop();
            video_renderer?.Stop();

            /* close each stream */
            if (demuxer.audio_st != null)
            {
                auddec.decoder_abort(sampq);
                auddec.decoder_destroy();
            }
            if (demuxer.video_st != null)
            {
                viddec.decoder_abort(pictq);
                viddec.decoder_destroy();
            }
            if (demuxer.subtitle_st != null)
            {
                subdec.decoder_abort(subpq);
                subdec.decoder_destroy();
            }

            demuxer.Close();

            /* free all pictures */
            pictq.frame_queue_destory();
            sampq.frame_queue_destory();
            subpq.frame_queue_destory();
            SDL_DestroyCond(continue_read_thread);
            State = PlayerState.Closed;
        }

        /* pause or resume the video */
        internal void stream_toggle_pause()
        {
            if (paused)
            {
                if (video_renderer != null)
                    video_renderer.frame_timer += av_gettime_relative() / 1000000.0 - vidclk.last_updated;
                if (demuxer.read_pause_return != AVERROR(ENOSYS))
                    vidclk.paused = false;
                vidclk.set_clock(vidclk.get_clock(), vidclk.serial);
            }
            paused = audclk.paused = vidclk.paused = !paused;
        }

        internal void step_to_next_frame()
        {
            /* if the stream is paused unpause it, then step */
            if (paused)
                stream_toggle_pause();
            step = true;
        }

        internal void RaiseMediaEnded()
        {
            eventq.Enqueue(PlayerEvent.Ended());
        }

        internal unsafe AV_SYNC_TYPE get_master_sync_type()
        {
            if (av_sync_type == AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER)
            {
                if (demuxer.video_st != null)
                    return AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER;
                else
                    return AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER;
            }
            else if (av_sync_type == AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER)
            {
                if (demuxer.audio_st != null)
                    return AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER;
                else
                    return AV_SYNC_TYPE.AV_SYNC_EXTERNAL_CLOCK;
            }
            else
            {
                return AV_SYNC_TYPE.AV_SYNC_EXTERNAL_CLOCK;
            }
        }

        /* get the current master clock value */
        internal double get_master_clock()
        {
            switch (get_master_sync_type())
            {
                case AV_SYNC_TYPE.AV_SYNC_AUDIO_MASTER:
                    return audclk.get_clock();
                case AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER:
                    return vidclk.get_clock();
                default:
                    throw new Exception();
            }
        }

        void ShowStatus()
        {
            if (show_status == 0 || state != PlayerState.Playing) return;
            long cur_time = av_gettime_relative();
            if (last_status_time == 0 || (cur_time - last_status_time) >= 50000)
            {
                int aqsize = 0;
                int vqsize = 0;
                int sqsize = 0;
                double av_diff = 0;
                string tip;

                if (demuxer.audio_st != null)
                    aqsize = audioq.size;
                if (demuxer.video_st != null)
                    vqsize = videoq.size;
                if (demuxer.subtitle_st != null)
                    sqsize = subtitleq.size;

                if (demuxer.audio_st != null && demuxer.video_st != null)
                {
                    av_diff = audclk.get_clock() - vidclk.get_clock();
                    tip = "A-V";
                }
                else if (demuxer.video_st != null)
                {
                    av_diff = get_master_clock() - vidclk.get_clock();
                    tip = "M-V";
                }
                else if (demuxer.audio_st != null)
                {
                    av_diff = get_master_clock() - audclk.get_clock();
                    tip = "M-A";
                }
                else
                {
                    tip = "   ";
                }

                string drop;
                if (video_disable_internal || demuxer.video_st == null || FrameDrop == FrameDrop.No ||
                    (FrameDrop == FrameDrop.Auto && av_sync_type == AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER))
                    drop = "NaN";
                else
                    drop = FrameDropCount.ToString();

                long pcnfd = demuxer.video_st != null && viddec.CodecContext != null ? viddec.CodecContext->pts_correction_num_faulty_dts : 0;
                long pcnfp = demuxer.video_st != null && viddec.CodecContext != null ? viddec.CodecContext->pts_correction_num_faulty_pts : 0;
                Console.Write($"\r{get_master_clock(),7:0.00}" +
                              $" {tip}:{av_diff,7:0.000}" +
                              $" fd={frame_drops_early + frame_drops_late,4}" +
                              $" aq={aqsize / 1024,5}KB" +
                              $" vq={vqsize / 1024,5}KB" +
                              $" sq={sqsize,5}B" +
                              $" f={pcnfd}/{pcnfp}" +
                              $" drop={drop}");
                last_status_time = cur_time;
            }
            else
            {

            }
        }

        enum EventAction
        {
            Open,
            Stop,
            Play,
            Pause,
            Ended,
            Close,
        }

        unsafe struct PlayerEvent
        {
            public EventAction Action;
            public string filename;
            internal AVInputFormat* iformat;

            public static PlayerEvent Open(string filename)
            {
                return new PlayerEvent()
                {
                    Action = EventAction.Open,
                    filename = filename,
                };
            }
            public static PlayerEvent Open(string filename, AVInputFormat* iformat)
            {
                return new PlayerEvent()
                {
                    Action = EventAction.Open,
                    filename = filename,
                    iformat = iformat
                };
            }
            public static PlayerEvent? Open(string filename, string iformat)
            {
                var file_iformt = opt_format(iformat);
                if (file_iformt == null)
                    return null;
                return new PlayerEvent()
                {
                    Action = EventAction.Open,
                    filename = filename,
                    iformat = file_iformt,
                };
            }
            public static PlayerEvent Play() => new PlayerEvent() { Action = EventAction.Play };
            public static PlayerEvent Stop() => new PlayerEvent() { Action = EventAction.Stop };
            public static PlayerEvent Pause() => new PlayerEvent() { Action = EventAction.Pause };
            public static PlayerEvent Ended() => new PlayerEvent() { Action = EventAction.Ended };
            public static PlayerEvent Close() => new PlayerEvent() { Action = EventAction.Close };

            static AVInputFormat* opt_format(string iformat)
            {
                var file_iformat = av_find_input_format(iformat);
                if (file_iformat == null)
                {
                    av_log(null, AV_LOG_FATAL, "Unknown input format: {arg}\n");
                    return null;
                }
                return file_iformat;
            }
        }
    }
}
