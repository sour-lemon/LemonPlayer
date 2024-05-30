using System;
using System.Threading;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Renderer
{
    public unsafe abstract class VideoRendererBase : IDisposable
    {
        bool started;
        Thread video_tid;
        bool force_refresh;
        internal double frame_timer;

        protected abstract void upload_texture(VideoFrame frame);

        void video_image_display(FFMediaPlayer vs)
        {
            VideoFrame vp = vs.pictq.frame_queue_peek_last();

            if (!vp.uploaded)
            {
                upload_texture(vp);
                vp.uploaded = true;
                vp.flip_v = vp.frame->linesize[0] < 0;
            }
        }

        double compute_target_delay(double delay, FFMediaPlayer vs)
        {
            double sync_threshold, diff;

            /* update delay to follow master synchronisation source */
            if (vs.get_master_sync_type() != AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER)
            {
                /* if video is slave, we try to correct big delays by
                   duplicating or deleting a frame */
                diff = vs.vidclk.get_clock() - vs.get_master_clock();

                /* skip or repeat frame. We take into account the
                   delay to compute the threshold. I still don't know
                   if it is the best guess */
                sync_threshold = Math.Max(AV_SYNC_THRESHOLD_MIN, Math.Min(AV_SYNC_THRESHOLD_MAX, delay));
                if (!isnan(diff) && fabs(diff) < vs.max_frame_duration)
                {
                    if (diff <= -sync_threshold)
                        delay = Math.Max(0, delay + diff);
                    else if (diff >= sync_threshold && delay > AV_SYNC_FRAMEDUP_THRESHOLD)
                        delay = delay + diff;
                    else if (diff >= sync_threshold)
                        delay = 2 * delay;
                }
            }

            av_log(null, AV_LOG_TRACE, "video: delay={delay:0.000} A-V={-diff}\n");

            return delay;
        }

        double vp_duration(FFMediaPlayer vs, Frame vp, Frame nextvp)
        {
            if (vp.serial == nextvp.serial)
            {
                double duration = nextvp.pts - vp.pts;
                if (isnan(duration) || duration <= 0 || duration > vs.max_frame_duration)
                    return vp.duration;
                else
                    return duration;
            }
            else
            {
                return 0.0;
            }
        }

        void update_video_pts(FFMediaPlayer vs, double pts, int serial)
        {
            /* update current video pts */
            vs.vidclk.set_clock(pts, serial);
        }

        protected void video_refresh(FFMediaPlayer vs, ref double remaining_time)
        {
            double time;

        retry:
            if (vs.pictq.frame_queue_nb_remaining() == 0)
            {
                // nothing to do, no picture to display in the queue
            }
            else
            {
                double last_duration, duration, delay;
                Frame vp;
                Frame lastvp;

                /* dequeue the picture */
                lastvp = vs.pictq.frame_queue_peek_last();
                vp = vs.pictq.frame_queue_peek();

                if (vp.serial != vs.videoq.serial)
                {
                    vs.pictq.frame_queue_next();
                    goto retry;
                }

                if (lastvp.serial != vp.serial)
                    frame_timer = av_gettime_relative() / 1000000.0;

                if (vs.paused)
                    goto display;

                /* compute nominal last_duration */
                last_duration = vp_duration(vs, lastvp, vp);
                delay = compute_target_delay(last_duration, vs);

                time = av_gettime_relative() / 1000000.0;
                if (time < frame_timer + delay)
                {
                    remaining_time = Math.Min(frame_timer + delay - time, remaining_time);
                    goto display;
                }

                frame_timer += delay;
                if (delay > 0 && time - frame_timer > AV_SYNC_THRESHOLD_MAX)
                    frame_timer = time;

                SDL_LockMutex(vs.pictq.mutex);
                if (!isnan(vp.pts))
                    update_video_pts(vs, vp.pts, vp.serial);
                SDL_UnlockMutex(vs.pictq.mutex);

                if (vs.pictq.frame_queue_nb_remaining() > 1)
                {
                    Frame nextvp = vs.pictq.frame_queue_peek_next();
                    duration = vp_duration(vs, vp, nextvp);
                    if (!vs.step && (vs.FrameDrop == FrameDrop.Yes || (vs.FrameDrop != FrameDrop.No && vs.get_master_sync_type() != AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER)) && time > frame_timer + duration)
                    {
                        vs.frame_drops_late++;
                        vs.pictq.frame_queue_next();
                        goto retry;
                    }
                }

                vs.pictq.frame_queue_next();
                force_refresh = true;

                if (vs.step && !vs.paused)
                    vs.stream_toggle_pause();
            }
        display:
            /* display picture */
            if (force_refresh && vs.pictq.rindex_shown != 0)
                video_image_display(vs);
            force_refresh = false;
        }

        void refresh_loop_wait_event(object opaque)
        {
            FFMediaPlayer vs = (FFMediaPlayer)opaque;
            double remaining_time = 0.0;
            while (started)
            {
                if (remaining_time > 0.0)
                    av_usleep((uint)(remaining_time * 1000000.0));
                remaining_time = REFRESH_RATE;
                if (!vs.paused || force_refresh)
                    video_refresh(vs, ref remaining_time);
            }
        }

        /// <summary>
        /// 启动一个新线程来执行渲染工作，如果不想使用内置的渲染线程，可以重写此方法
        /// </summary>
        public virtual void Start(FFMediaPlayer player)
        {
            if (!started)
            {
                started = true;
                video_tid = new Thread(refresh_loop_wait_event);
                video_tid.IsBackground = true;
                video_tid.Start(player);
            }
        }

        public virtual void Stop()
        {
            if (started)
            {
                started = false;
                video_tid?.Join();
                video_tid = null;
            }
        }

        public virtual void Dispose()
        {
            Stop();
        }
    }
}
