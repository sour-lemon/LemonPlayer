using FFmpeg.AutoGen;
using LemonPlayer.Core;
using System.Threading;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer
{
    unsafe abstract class DecoderBase<T> where T : Frame, new()
    {
        protected readonly FFMediaPlayer vs;
        readonly PacketQueue queue;
        AVPacket* pkt;
        protected AVCodecContext* avctx;
        protected AVStream* stream;
        protected int pkt_serial;
        protected bool fallbackToSw;
        internal int finished { get; private set; }
        bool packet_pending;
        protected long start_pts;
        protected AVRational start_pts_tb;
        long next_pts;
        AVRational next_pts_tb;
        Thread decoder_tid;

        internal AVCodecContext* CodecContext => avctx;

        /* non spec compliant optimizations */
        bool fast = false;
        /* let decoder reorder pts 0=off 1=on -1=auto */
        int decoder_reorder_pts = -1;

        public DecoderBase(FFMediaPlayer player, PacketQueue queue)
        {
            this.vs = player;
            this.queue = queue;
        }

        protected int decoder_init(AVCodecContext* avctx)
        {
            finished = default;
            packet_pending = default;
            start_pts_tb = default;
            next_pts = default;
            next_pts_tb = default;
            decoder_tid = null;
            //memset(d, 0, sizeof(Decoder));
            pkt = av_packet_alloc();
            if (pkt == null)
                return AVERROR(ENOMEM);
            this.avctx = avctx;
            start_pts = AV_NOPTS_VALUE;
            pkt_serial = -1;
            return 0;
        }

        protected int decoder_decode_frame(AVFrame* frame, AVSubtitle* sub)
        {
            int ret = AVERROR(EAGAIN);

            for (; ; )
            {
                if (queue.serial == pkt_serial)
                {
                    do
                    {
                        if (queue.abort_request)
                            return -1;

                        switch (avctx->codec_type)
                        {
                            case AVMediaType.AVMEDIA_TYPE_VIDEO:
                                ret = avcodec_receive_frame(avctx, frame);
                                if (ret >= 0)
                                {
                                    if (decoder_reorder_pts == -1)
                                    {
                                        frame->pts = frame->best_effort_timestamp;
                                    }
                                    else if (decoder_reorder_pts == 0)
                                    {
                                        frame->pts = frame->pkt_dts;
                                    }
                                }
                                break;
                            case AVMediaType.AVMEDIA_TYPE_AUDIO:
                                ret = avcodec_receive_frame(avctx, frame);
                                if (ret >= 0)
                                {
                                    AVRational tb = new AVRational() { num = 1, den = frame->sample_rate };
                                    if (frame->pts != AV_NOPTS_VALUE)
                                        frame->pts = av_rescale_q(frame->pts, avctx->pkt_timebase, tb);
                                    else if (next_pts != AV_NOPTS_VALUE)
                                        frame->pts = av_rescale_q(next_pts, next_pts_tb, tb);
                                    if (frame->pts != AV_NOPTS_VALUE)
                                    {
                                        next_pts = frame->pts + frame->nb_samples;
                                        next_pts_tb = tb;
                                    }
                                }
                                break;
                        }
                        if (ret == AVERROR_EOF)
                        {
                            finished = pkt_serial;
                            avcodec_flush_buffers(avctx);
                            return 0;
                        }
                        if (ret >= 0)
                            return 1;
                    } while (ret != AVERROR(EAGAIN));
                }

                do
                {
                    if (queue.nb_packets == 0)
                        SDL_CondSignal(vs.continue_read_thread);
                    if (packet_pending)
                    {
                        packet_pending = false;
                    }
                    else
                    {
                        int old_serial = pkt_serial;
                        if (queue.packet_queue_get(pkt, true, ref pkt_serial) < 0)
                            return -1;
                        if (old_serial != pkt_serial)
                        {
                            avcodec_flush_buffers(avctx);
                            finished = 0;
                            next_pts = start_pts;
                            next_pts_tb = start_pts_tb;
                        }
                    }
                    if (queue.serial == pkt_serial)
                        break;
                    av_packet_unref(pkt);
                } while (true);

                if (avctx->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                {
                    int got_frame = 0;
                    ret = avcodec_decode_subtitle2(avctx, sub, &got_frame, pkt);
                    if (ret < 0)
                    {
                        ret = AVERROR(EAGAIN);
                    }
                    else
                    {
                        if (got_frame != 0 && pkt->data == null)
                        {
                            packet_pending = true;
                        }
                        ret = got_frame != 0 ? 0 : pkt->data != null ? AVERROR(EAGAIN) : AVERROR_EOF;
                    }
                    av_packet_unref(pkt);
                }
                else
                {
                    sendPkt:
                    if (avcodec_send_packet(avctx, pkt) == AVERROR(EAGAIN))
                    {
                        av_log(avctx, AV_LOG_ERROR, "Receive_frame and send_packet both returned EAGAIN, which is an API violation.\n");
                        packet_pending = true;
                    }
                    else if (fallbackToSw)
                    {
                        fixed (AVCodecContext** ptr = &avctx)
                            avcodec_free_context(ptr);
                        stream_component_open(stream);
                        goto sendPkt;
                    }
                    else
                    {
                        av_packet_unref(pkt);
                    }
                }
            }
        }

        protected int decoder_start(ThreadStart fn)
        {
            var thread = new Thread(fn);
            //thread.Name = thread_name;
            thread.IsBackground = true;
            thread.Start();
            decoder_tid = thread;
            return 0;
        }

        internal void decoder_destroy()
        {
            fixed (AVPacket** ptr = &pkt)
                av_packet_free(ptr);
            fixed (AVCodecContext** ptr = &avctx)
                avcodec_free_context(ptr);
        }

        internal void decoder_abort(FrameQueue<T> fq)
        {
            queue.packet_queue_abort();
            fq.frame_queue_signal();
            decoder_tid.Join();
            decoder_tid = null;
            queue.packet_queue_flush();
        }

        /* open a given stream. Return 0 if OK */
        internal int stream_component_open(AVStream* stream)
        {
            this.stream = stream;
            AVCodecContext* avctx = this.avctx;
            AVCodec* codec;
            AVDictionaryEntry* t = null;
            int ret = 0;

            if (stream == null)
                return -1;

            if (avctx == null)
                avctx = avcodec_alloc_context3(null);
            if (avctx == null)
                return AVERROR(ENOMEM);

            ret = avcodec_parameters_to_context(avctx, stream->codecpar);
            if (ret < 0)
                goto fail;
            avctx->pkt_timebase = stream->time_base;

            codec = avcodec_find_decoder(avctx->codec_id);
            if (codec == null)
            {
                av_log(null, AV_LOG_WARNING, $"No decoder could be found for codec {avcodec_get_name(avctx->codec_id)}\n");
                ret = AVERROR(EINVAL);
                goto fail;
            }

            avctx->codec_id = codec->id;

            if (fast)
                avctx->flags2 |= AV_CODEC_FLAG2_FAST;

            AVDictionary* opts = null;
            SetCodecContext(avctx, &opts);

            //avctx->opaque = handle;
            if ((ret = avcodec_open2(avctx, codec, &opts)) < 0)
            {
                goto fail;
            }

            stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            this.avctx = avctx;
            if (!fallbackToSw)
            {
                switch (avctx->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                    case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                        if (StreamComponentOpen(stream, ref ret) != 0)
                            goto fail;
                        break;
                    default:
                        break;
                }
            }
            fallbackToSw = false;
            return ret;

        fail:
            av_dict_free(&opts);
            avcodec_free_context(&avctx);
            this.avctx = null;
            fallbackToSw = false;
            return ret;
        }

        protected virtual void SetCodecContext(AVCodecContext* avctx, AVDictionary** opts)
        {

        }

        protected abstract int StreamComponentOpen(AVStream* stream, ref int ret);
    }
}
