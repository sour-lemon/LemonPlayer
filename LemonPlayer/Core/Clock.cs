using static FFmpeg.AutoGen.ffmpeg;

namespace LemonPlayer.Core
{
    unsafe class Clock
    {
        readonly PacketQueue queue;
        internal double pts;                /* clock base */
        double pts_drift;                   /* clock base minus time at which we updated the clock */
        internal double last_updated;
        internal double speed;
        internal int serial;                /* clock is based on a packet with this serial */
        internal bool paused;

        int queue_serial => queue.serial;   /* pointer to the current packet queue serial, used for obsolete clock detection */

        public Clock(PacketQueue queue)
        {
            this.queue = queue;
        }

        internal double get_clock()
        {
            if (queue_serial != serial)
                return double.NaN;
            if (paused)
            {
                return pts;
            }
            else
            {
                double time = av_gettime_relative() / 1000000.0;
                return pts_drift + time - (time - last_updated) * (1.0 - speed);
            }
        }

        internal void set_clock_at(double pts, int serial, double time)
        {
            this.pts = pts;
            last_updated = time;
            pts_drift = pts - time;
            this.serial = serial;
        }

        internal void set_clock(double pts, int serial)
        {
            double time = av_gettime_relative() / 1000000.0;
            set_clock_at(pts, serial, time);
        }

        internal void set_clock_speed(double speed)
        {
            set_clock(get_clock(), serial);
            this.speed = speed;
        }

        internal void init_clock()
        {
            speed = 1.0;
            paused = false;
            set_clock(double.NaN, -1);
        }
    }
}
