using FFmpeg.AutoGen;
using LemonPlayer.Core;
using System;
using System.Runtime.InteropServices;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer
{
    internal unsafe class VideoDecoder : DecoderBase<VideoFrame>
    {
        readonly GCHandle handle;
        readonly IDeviceContext hwctx;
        readonly AVCodecContext_get_format get_format_callback;
        internal readonly FrameQueue<VideoFrame> pictq;

        public VideoDecoder(FFMediaPlayer player, IDeviceContext hwctx) : base(player, player.videoq)
        {
            this.hwctx = hwctx;
            handle = GCHandle.Alloc(this);
            get_format_callback = GetFormatCallback;
            pictq = new FrameQueue<VideoFrame>(player.videoq, VIDEO_PICTURE_QUEUE_SIZE, 1);
        }

        int queue_picture(AVFrame* src_frame, double pts, double duration, long pos, int serial)
        {
            VideoFrame vp = pictq.frame_queue_peek_writable();
            if (vp == null)
                return -1;

            vp.sar = src_frame->sample_aspect_ratio;
            vp.uploaded = false;

            vp.width = src_frame->width;
            vp.height = src_frame->height;
            vp.format = src_frame->format;

            vp.pts = pts;
            vp.duration = duration;
            vp.pos = pos;
            vp.serial = serial;

            vp.hwctx = hwctx;

            av_frame_move_ref(vp.frame, src_frame);
            pictq.frame_queue_push();

            return 0;
        }

        int get_video_frame(AVFrame* frame)
        {
            int got_picture;

            if ((got_picture = decoder_decode_frame(frame, null)) < 0)
                return -1;

            if (got_picture != 0)
            {
                double dpts = double.NaN;

                if (frame->pts != AV_NOPTS_VALUE)
                    dpts = av_q2d(stream->time_base) * frame->pts;

                frame->sample_aspect_ratio = av_guess_sample_aspect_ratio(vs.ic, stream, frame);

                if (vs.FrameDrop == FrameDrop.Yes || (vs.FrameDrop != FrameDrop.No && vs.get_master_sync_type() != AV_SYNC_TYPE.AV_SYNC_VIDEO_MASTER))
                {
                    if (frame->pts != AV_NOPTS_VALUE)
                    {
                        double diff = dpts - vs.get_master_clock();
                        if (!isnan(diff) && fabs(diff) < AV_NOSYNC_THRESHOLD &&
                            diff < 0 &&
                            pkt_serial == vs.vidclk.serial &&
                            vs.videoq.nb_packets != 0)
                        {
                            vs.frame_drops_early++;
                            av_frame_unref(frame);
                            got_picture = 0;
                        }
                    }
                }
            }

            return got_picture;
        }

        void video_thread()
        {
            AVFrame* frame = av_frame_alloc();
            double pts;
            double duration;
            int ret;
            AVRational tb = stream->time_base;
            AVRational frame_rate = av_guess_frame_rate(vs.ic, stream, null);

            if (frame == null)
                return;

            for (; ; )
            {
                ret = get_video_frame(frame);
                if (ret < 0)
                    goto the_end;
                if (ret == 0)
                    continue;

                duration = (frame_rate.num != 0 && frame_rate.den != 0 ? av_q2d(new AVRational { num = frame_rate.den, den = frame_rate.num }) : 0);
                pts = (frame->pts == AV_NOPTS_VALUE) ? double.NaN : frame->pts * av_q2d(tb);
                ret = queue_picture(frame, pts, duration, frame->pkt_pos, pkt_serial);
                av_frame_unref(frame);

                if (ret < 0)
                    goto the_end;
            }
        the_end:
            av_frame_free(&frame);
            return;
            //return 0;
        }

        protected override unsafe void SetCodecContext(AVCodecContext* avctx, AVDictionary** opts)
        {
            base.SetCodecContext(avctx, opts);
            if (fallbackToSw)
            {
                av_dict_set(opts, "threads", "auto", 0);
            }
            else if (hwctx != null && hwctx.ApplyDeviceContext(avctx))
            {
                avctx->hwaccel_flags |= AV_HWACCEL_FLAG_ALLOW_HIGH_DEPTH | AV_HWACCEL_FLAG_IGNORE_LEVEL | AV_HWACCEL_FLAG_ALLOW_PROFILE_MISMATCH;
                avctx->get_format = get_format_callback;
                avctx->opaque = (void*)GCHandle.ToIntPtr(handle);
            }
            else
            {
                // 要使用硬件加速解码的话不能设置这个标志，否则get_format会变成异步调用
                // 导致ffmpeg不使用我们设置的hw_device_ctx
                av_dict_set(opts, "threads", "auto", 0);
            }
        }

        protected override int StreamComponentOpen(AVStream* stream, ref int ret)
        {
            if ((ret = decoder_init(avctx)) < 0)
                return 1;
            if ((ret = decoder_start(video_thread)) < 0)
                return 0;
            return 0;
        }

        // unity中要求回调必须是静态方法
        private static AVPixelFormat GetFormatCallback(AVCodecContext* s, AVPixelFormat* fmt)
        {
            var vs = (VideoDecoder)GCHandle.FromIntPtr((IntPtr)s->opaque).Target;
            return vs.GetFormatCallbackCore(s, fmt);
        }

        private AVPixelFormat GetFormatCallbackCore(AVCodecContext* s, AVPixelFormat* fmt)
        {
            // hwctx 不会为null，否则就不会有回调了
            var tfmt = hwctx.HWPixelFormat;
            if (avctx->hw_device_ctx != null)
            {
                while (*fmt != AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    var p = *fmt;
                    if (*fmt == tfmt)
                    {
                        //Console.WriteLine($"video decode pixel format: {tfmt}");
                        if (avctx->hw_frames_ctx != null)
                            av_buffer_unref(&avctx->hw_frames_ctx);
                        if (hwctx.ApplyFrameContext(avctx))
                            return tfmt;
                    }
                    fmt++;
                }
            }
            if (avctx->hw_frames_ctx != null)
                av_buffer_unref(&avctx->hw_frames_ctx);
            if (avctx->hw_device_ctx != null)
                av_buffer_unref(&avctx->hw_device_ctx);
            var format = avcodec_default_get_format(avctx, fmt);
            //Console.WriteLine($"{Environment.NewLine}video decode pixel format: {format}");
            fallbackToSw = true;
            return format;
        }

        ~VideoDecoder()
        {
            handle.Free();
        }
    }
}
