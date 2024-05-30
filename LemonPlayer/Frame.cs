using FFmpeg.AutoGen;
using System;

namespace LemonPlayer
{
    /* Common struct for handling all types of decoded data and allocated render buffers. */
    public unsafe abstract class Frame
    {
        public AVFrame* frame;
        internal int serial;
        internal double pts;           /* presentation timestamp for the frame */
        internal double duration;      /* estimated duration of the frame */
        internal long pos;             /* byte position of the frame in the input file */
        public IntPtr data => (IntPtr)frame->data[0];

        ~Frame()
        {
            if (frame != null)
            {
                fixed (AVFrame** ptr = &frame)
                    ffmpeg.av_frame_free(ptr);
            }
        }
    }

    public unsafe class VideoFrame : Frame
    {
        public IDeviceContext hwctx;
        public int width;
        public int height;
        public int format;
        internal AVRational sar;
        internal bool uploaded;
        internal bool flip_v;

        public AVColorRange color_range => frame->color_range;
        public AVColorSpace colorspace => frame->colorspace;
        public int interlaced_frame => frame->interlaced_frame;
        public int top_field_first => frame->top_field_first;

        public bool IsHwFrame => frame->hw_frames_ctx != null;
    }

    public class AudioFrame : Frame
    {
    }

    public class SubtitleFrame : Frame
    {
        internal AVSubtitle sub;
        public int width;
        public int height;
        internal bool uploaded;
    }
}
