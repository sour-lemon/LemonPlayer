using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System;
using System.Threading;

namespace LemonPlayer
{
    internal unsafe static class Common
    {
        public const int MAX_QUEUE_SIZE = (15 * 1024 * 1024);
        public const int MIN_FRAMES = 25;

        /* no AV sync correction is done if below the minimum AV sync threshold */
        public const double AV_SYNC_THRESHOLD_MIN = 0.04;
        /* AV sync correction is done if above the maximum AV sync threshold */
        public const double AV_SYNC_THRESHOLD_MAX = 0.1;
        /* If a frame duration is longer than this, it will not be duplicated to compensate AV sync */
        public const double AV_SYNC_FRAMEDUP_THRESHOLD = 0.1;
        /* no AV correction is done if too big error */
        public const double AV_NOSYNC_THRESHOLD = 10.0;

        /* maximum audio speed change to get correct sync */
        public const int SAMPLE_CORRECTION_PERCENT_MAX = 10;

        /* we use about AUDIO_DIFF_AVG_NB A-V differences to make the average */
        public const int AUDIO_DIFF_AVG_NB = 20;

        /* polls for possible required screen refresh at least this often, should be less than 1/fps */
        public const double REFRESH_RATE = 0.01;

        public const int VIDEO_PICTURE_QUEUE_SIZE = 3;
        public const int SUBPICTURE_QUEUE_SIZE = 16;
        public const int SAMPLE_QUEUE_SIZE = 9;
        //public const int FRAME_QUEUE_SIZE = FFMAX(SAMPLE_QUEUE_SIZE, FFMAX(VIDEO_PICTURE_QUEUE_SIZE, SUBPICTURE_QUEUE_SIZE));

        public const int AVMEDIA_TYPE_VIDEO = (int)AVMediaType.AVMEDIA_TYPE_VIDEO;
        public const int AVMEDIA_TYPE_AUDIO = (int)AVMediaType.AVMEDIA_TYPE_AUDIO;
        public const int AVMEDIA_TYPE_SUBTITLE = (int)AVMediaType.AVMEDIA_TYPE_SUBTITLE;
        public const int AVMEDIA_TYPE_NB = (int)AVMediaType.AVMEDIA_TYPE_NB;
        public const int ENOSYS = 38;

        public static bool isnan(double d) => double.IsNaN(d);
        public static double fabs(double value) => Math.Abs(value);
        public static int av_clip(int num, int min, int max)
        {
            return Math.Min(Math.Max(num, min), max);
        }
        public static bool strcmp(byte* s1, string s2) => strncmp(s1, s2, s2.Length);
        public static bool strncmp(byte* s1, string s2, int n)
        {
            byte u1, u2;
            int index = 0;
            {
                while (n-- > 0)
                {
                    u1 = *s1++;
                    u2 = (byte)s2[index++];
                    if (u1 != u2)
                        return false;
                    if (u1 == '\0')
                        return true;
                }
                return true;
            }
        }

        public static string tostr(byte* s)
        {
            return Marshal.PtrToStringAnsi((IntPtr)s);
        }
        public static string av_err2str(int err)
        {
            byte[] buf = new byte[1024];
            fixed (byte* ptr = buf)
            {
                ffmpeg.av_make_error_string(ptr, 1024, err);
                string msg = Marshal.PtrToStringAnsi((IntPtr)ptr);
                return msg;
            }
        }

        public class SDL_mutex { }
        public static SDL_mutex SDL_CreateMutex() => new SDL_mutex();
#pragma warning disable IDE0060 // 删除未使用的参数
        public static void SDL_DestroyMutex(SDL_mutex mutex) { }
#pragma warning restore IDE0060 // 删除未使用的参数
        public static void SDL_LockMutex(SDL_mutex mutex) => Monitor.Enter(mutex);
        public static void SDL_UnlockMutex(SDL_mutex mutex) => Monitor.Exit(mutex);

        public class SDL_cond : IDisposable
        {
            readonly AutoResetEvent cond = new AutoResetEvent(false);

            public void Set() => cond.Set();
            public void WaitOne() => cond.WaitOne();
            public void WaitOne(int time) => cond.WaitOne(time);
            public void Dispose() => cond.Dispose();
        }
        public static SDL_cond SDL_CreateCond() => new SDL_cond();
        public static void SDL_DestroyCond(SDL_cond cond) => cond.Dispose();
        public static void SDL_CondSignal(SDL_cond cond) => cond.Set();
        public static void SDL_CondWait(SDL_cond cond, SDL_mutex mutex)
        {
            Monitor.Exit(mutex);
            cond.WaitOne();
            Monitor.Enter(mutex);
        }
        public static void SDL_CondWaitTimeout(SDL_cond cond, SDL_mutex mutex, int time)
        {
            Monitor.Exit(mutex);
            cond.WaitOne(time);
            Monitor.Enter(mutex);
        }
    }
}
