using FFmpeg.AutoGen;
using LemonPlayer.Core;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Decoder
{
    internal unsafe class SubtitleDecoder : DecoderBase<SubtitleFrame>
    {
        internal readonly FrameQueue<SubtitleFrame> subpq;

        public SubtitleDecoder(FFMediaPlayer player) : base(player, player.subtitleq)
        {
            subpq = new FrameQueue<SubtitleFrame>(player.subtitleq, SUBPICTURE_QUEUE_SIZE, 0);
        }

        void subtitle_thread()
        {
            SubtitleFrame sp;
            int got_subtitle;
            double pts;

            for (; ; )
            {
                sp = subpq.frame_queue_peek_writable();
                if (sp == null)
                    return;
                //return 0;

                fixed (AVSubtitle* sub = &sp.sub)
                    if ((got_subtitle = decoder_decode_frame(null, sub)) < 0)
                        break;

                pts = 0;

                if (got_subtitle != 0 && sp.sub.format == 0)
                {
                    if (sp.sub.pts != AV_NOPTS_VALUE)
                        pts = sp.sub.pts / (double)AV_TIME_BASE;
                    sp.pts = pts;
                    sp.serial = pkt_serial;
                    sp.width = avctx->width;
                    sp.height = avctx->height;
                    sp.uploaded = false;

                    /* now we can update the picture count */
                    subpq.frame_queue_push();
                }
                else if (got_subtitle != 0)
                {
                    fixed (AVSubtitle* sub = &sp.sub)
                        avsubtitle_free(sub);
                }
            }
            return;
            //return 0;
        }

        protected override unsafe int StreamComponentOpen(AVStream* stream , ref int ret)
        {
            if ((ret = decoder_init(avctx)) < 0)
                return 1;
            if ((ret = decoder_start(subtitle_thread)) < 0)
                return 0;
            return 0;
        }
    }
}
