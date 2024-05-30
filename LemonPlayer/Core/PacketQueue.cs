using FFmpeg.AutoGen;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System;
using static FFmpeg.AutoGen.ffmpeg;
using static LemonPlayer.Common;

namespace LemonPlayer.Core
{
    unsafe struct MyAVPacketList
    {
        public AVPacket* pkt;
        public int serial;
    }

    unsafe class PacketQueue
    {
        Queue<MyAVPacketList> pkt_list;
        internal int nb_packets;
        internal int size;
        internal long duration;
        internal bool abort_request;
        internal int serial;
        SDL_mutex mutex;
        SDL_cond cond;

        int packet_queue_put_private(AVPacket* pkt)
        {
            MyAVPacketList pkt1;

            if (abort_request)
                return -1;

            pkt1.pkt = pkt;
            pkt1.serial = serial;

            pkt_list.Enqueue(pkt1);
            nb_packets++;
            size += pkt1.pkt->size + sizeof(MyAVPacketList);
            duration += pkt1.pkt->duration;
            /* XXX: should duplicate packet data in DV case */
            SDL_CondSignal(cond);
            return 0;
        }

        internal int packet_queue_put(AVPacket* pkt)
        {
            AVPacket* pkt1;
            int ret;

            pkt1 = av_packet_alloc();
            if (pkt1 == null)
            {
                av_packet_unref(pkt);
                return -1;
            }
            av_packet_move_ref(pkt1, pkt);

            SDL_LockMutex(mutex);
            ret = packet_queue_put_private(pkt1);
            SDL_UnlockMutex(mutex);

            if (ret < 0)
                av_packet_free(&pkt1);

            return ret;
        }

        internal int packet_queue_put_nullpacket(AVPacket* pkt, int stream_index)
        {
            pkt->stream_index = stream_index;
            return packet_queue_put(pkt);
        }

        /* packet queue handling */
        internal int packet_queue_init()
        {
            nb_packets = 0;
            size = 0;
            duration = 0;
            abort_request = false;
            serial = 0;
            //memset(q, 0, sizeof(PacketQueue));
            pkt_list = new Queue<MyAVPacketList>();
            if (pkt_list == null)
                return AVERROR(ENOMEM);
            mutex = SDL_CreateMutex();
            cond = SDL_CreateCond();
            abort_request = true;
            return 0;
        }

        internal void packet_queue_flush()
        {
            MyAVPacketList pkt1;

            SDL_LockMutex(mutex);
            while (pkt_list.Count > 0)
            {
                pkt1 = pkt_list.Dequeue();
                av_packet_free(&pkt1.pkt);
            }
            nb_packets = 0;
            size = 0;
            duration = 0;
            serial++;
            SDL_UnlockMutex(mutex);
        }

        internal void packet_queue_destroy()
        {
            packet_queue_flush();
            SDL_DestroyMutex(mutex);
            SDL_DestroyCond(cond);
            Marshal.FreeHGlobal((IntPtr)serial);
        }

        internal void packet_queue_abort()
        {
            SDL_LockMutex(mutex);

            abort_request = true;

            SDL_CondSignal(cond);

            SDL_UnlockMutex(mutex);
        }

        internal void packet_queue_start()
        {
            SDL_LockMutex(mutex);
            abort_request = false;
            serial++;
            SDL_UnlockMutex(mutex);
        }

        /* return < 0 if aborted, 0 if no packet and > 0 if packet.  */
        internal int packet_queue_get(AVPacket* pkt, bool block, ref int serial)
        {
            MyAVPacketList pkt1;
            int ret;

            SDL_LockMutex(mutex);

            for (; ; )
            {
                if (abort_request)
                {
                    ret = -1;
                    break;
                }

                if (pkt_list.Count > 0)
                {
                    pkt1 = pkt_list.Dequeue();
                    nb_packets--;
                    size -= pkt1.pkt->size + sizeof(MyAVPacketList);
                    duration -= pkt1.pkt->duration;
                    av_packet_move_ref(pkt, pkt1.pkt);
                    //if (serial != null)
                    serial = pkt1.serial;
                    av_packet_free(&pkt1.pkt);
                    ret = 1;
                    break;
                }
                else if (!block)
                {
                    ret = 0;
                    break;
                }
                else
                {
                    SDL_CondWait(cond, mutex);
                }
            }
            SDL_UnlockMutex(mutex);
            return ret;
        }
    }
}
