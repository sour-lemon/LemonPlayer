using System;
using System.Collections.Generic;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Core
{
    class FrameQueue<T> where T : Frame, new()
    {
        readonly PacketQueue pktq;
        readonly List<T> queue;
        int rindex;
        int windex;
        internal int size;
        readonly int max_size;
        readonly bool keep_last;
        internal int rindex_shown;
        internal SDL_mutex mutex;
        SDL_cond cond;

        public FrameQueue(PacketQueue pktq, int max_size, int keep_last)
        {
            this.pktq = pktq;
            var FRAME_QUEUE_SIZE = Math.Max(SAMPLE_QUEUE_SIZE, Math.Max(VIDEO_PICTURE_QUEUE_SIZE, SUBPICTURE_QUEUE_SIZE));
            this.max_size = Math.Min(max_size, FRAME_QUEUE_SIZE);
            this.keep_last = keep_last != 0;
            queue = new List<T>(FRAME_QUEUE_SIZE);
        }

        unsafe void frame_queue_unref_item(T vp)
        {
            av_frame_unref(vp.frame);
            if (vp is SubtitleFrame sf)
            {
                var sub = sf.sub;
                sf.sub = default;
                avsubtitle_free(&sub);
            }
        }

        internal unsafe int frame_queue_init()
        {
            rindex = 0;
            windex = 0;
            size = 0;
            rindex_shown = 0;
            //memset(f, 0, sizeof(FrameQueue));
            mutex = SDL_CreateMutex();
            cond = SDL_CreateCond();
            queue.Clear();
            for (int i = 0; i < max_size; i++)
            {
                queue.Add(new T());
            }
            for (int i = 0; i < max_size; i++)
            {
                queue[i].frame = av_frame_alloc();
                if (queue[i].frame == null)
                    return AVERROR(ENOMEM);
            }
            return 0;
        }

        internal unsafe void frame_queue_destory()
        {
            int i;
            for (i = 0; i < max_size; i++)
            {
                T vp = queue[i];
                frame_queue_unref_item(vp);
                var frame = vp.frame;
                vp.frame = null;
                av_frame_free(&frame);
            }
            SDL_DestroyMutex(mutex);
            SDL_DestroyCond(cond);
            queue.Clear();
        }

        internal void frame_queue_signal()
        {
            SDL_LockMutex(mutex);
            SDL_CondSignal(cond);
            SDL_UnlockMutex(mutex);
        }

        internal T frame_queue_peek()
        {
            return queue[(rindex + rindex_shown) % max_size];
        }

        internal T frame_queue_peek_next()
        {
            return queue[(rindex + rindex_shown + 1) % max_size];
        }

        internal T frame_queue_peek_last()
        {
            return queue[rindex];
        }

        internal T frame_queue_peek_writable()
        {
            /* wait until we have space to put a new frame */
            SDL_LockMutex(mutex);
            while (size >= max_size &&
                   !pktq.abort_request)
            {
                SDL_CondWait(cond, mutex);
            }
            SDL_UnlockMutex(mutex);

            if (pktq.abort_request)
                return null;

            return queue[windex];
        }

        internal T frame_queue_peek_readable()
        {
            /* wait until we have a readable a new frame */
            SDL_LockMutex(mutex);
            while (size - rindex_shown <= 0 &&
                   !pktq.abort_request)
            {
                SDL_CondWait(cond, mutex);
            }
            SDL_UnlockMutex(mutex);

            if (pktq.abort_request)
                return null;

            return queue[(rindex + rindex_shown) % max_size];
        }

        internal void frame_queue_push()
        {
            if (++windex == max_size)
                windex = 0;
            SDL_LockMutex(mutex);
            size++;
            SDL_CondSignal(cond);
            SDL_UnlockMutex(mutex);
        }

        internal void frame_queue_next()
        {
            if (keep_last && rindex_shown == 0)
            {
                rindex_shown = 1;
                return;
            }
            frame_queue_unref_item(queue[rindex]);
            if (++rindex == max_size)
                rindex = 0;
            SDL_LockMutex(mutex);
            size--;
            SDL_CondSignal(cond);
            SDL_UnlockMutex(mutex);
        }

        /* return the number of undisplayed frames in the queue */
        internal int frame_queue_nb_remaining()
        {
            return size - rindex_shown;
        }
    }
}
