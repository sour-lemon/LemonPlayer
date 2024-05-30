using FFmpeg.AutoGen;
using LemonPlayer.Core;
using System;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Decoder
{
    internal unsafe class AudioDecoder : DecoderBase<AudioFrame>
    {
        internal readonly FrameQueue<AudioFrame> sampq;

        public AudioDecoder(FFMediaPlayer player) : base(player, player.audioq)
        {
            sampq = new FrameQueue<AudioFrame>(player.audioq, SAMPLE_QUEUE_SIZE, 1);
        }

        void audio_thread()
        {
            AVFrame* frame = av_frame_alloc();
            Frame af;
            int got_frame = 0;
            AVRational tb;
            int ret = 0;

            if (frame == null)
                return;
            //return AVERROR(ENOMEM);

            do
            {
                if ((got_frame = decoder_decode_frame(frame, null)) < 0)
                    goto the_end;

                if (got_frame != 0)
                {
                    tb = new AVRational() { num = 1, den = frame->sample_rate };

                    af = sampq.frame_queue_peek_writable();
                    if (af == null)
                        goto the_end;

                    af.pts = (frame->pts == AV_NOPTS_VALUE) ? double.NaN : frame->pts * av_q2d(tb);
                    af.pos = frame->pkt_pos;
                    af.serial = pkt_serial;
                    af.duration = av_q2d(new AVRational() { num = frame->nb_samples, den = frame->sample_rate });

                    av_frame_move_ref(af.frame, frame);
                    sampq.frame_queue_push();
                }
            } while (ret >= 0 || ret == AVERROR(EAGAIN) || ret == AVERROR_EOF);
        the_end:
            av_frame_free(&frame);
            return;
            //return ret;
        }

        protected override unsafe void SetCodecContext(AVCodecContext* avctx, AVDictionary** opts)
        {
            base.SetCodecContext(avctx, opts);
            av_dict_set(opts, "threads", "auto", 0);
        }

        protected override int StreamComponentOpen(AVStream* stream, ref int ret)
        {
            AVFormatContext* ic = vs.ic;
            AVChannelLayout ch_layout = default;

            int sample_rate = avctx->sample_rate;
            ret = av_channel_layout_copy(&ch_layout, &avctx->ch_layout);
            if (ret < 0)
                goto fail;

            // TODO: 在解码器初始化完成之后，再设置音频渲染器
            // 对音频渲染器的设置，不会反过来影响解码器，没必要在这里进行
            /* prepare audio output */
            if ((ret = vs.audio_renderer.audio_open(vs, &ch_layout, sample_rate)) < 0)
                goto fail;

            if ((ret = decoder_init(avctx)) < 0)
                goto fail;
            if ((ic->iformat->flags & (AVFMT_NOBINSEARCH | AVFMT_NOGENSEARCH | AVFMT_NO_BYTE_SEEK)) != 0 && ic->iformat->read_seek.Pointer == IntPtr.Zero)
            {
                start_pts = stream->start_time;
                start_pts_tb = stream->time_base;
            }
            if ((ret = decoder_start(audio_thread)) < 0)
                goto fail;
            av_channel_layout_uninit(&ch_layout);
            return 0;

        fail:
            av_channel_layout_uninit(&ch_layout);
            return 1;
        }
    }
}
