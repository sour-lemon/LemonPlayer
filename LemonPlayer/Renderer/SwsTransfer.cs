using FFmpeg.AutoGen;
using System;
using static FFmpeg.AutoGen.ffmpeg;

namespace LemonPlayer.Renderer
{
    public unsafe class SwsTransfer : IDisposable
    {
        SwsContext* img_convert_ctx;
        byte*[] dstArray = new byte*[1];
        int[] dstStrideArray = new int[1];

        private bool CheckSwsCtx(AVFrame* frame, int dstW, int dstH, AVPixelFormat dstF)
        {
            SwsContext* current = img_convert_ctx;
            img_convert_ctx = sws_getCachedContext(img_convert_ctx,
                 frame->width, frame->height, (AVPixelFormat)frame->format,
                 dstW, dstH, dstF,
                 SWS_BICUBIC, null, null, null);
            if (img_convert_ctx == null)
                return false;
            if (img_convert_ctx == current)
                return true;
            // 不进行color_range的转换
            int* inv_table = null;
            int srcRange = default;
            int* table = null;
            int dstRange = default;
            int brightness = default;
            int contrast = default;
            int saturation = default;
            int hr = sws_getColorspaceDetails(img_convert_ctx, &inv_table, &srcRange, &table, &dstRange, &brightness, &contrast, &saturation);
            int_array4 invTable = default;
            for (uint i = 0; i < 4; i++)
                invTable[i] = inv_table[i];
            int_array4 Table = default;
            for (uint i = 0; i < 4; i++)
                Table[i] = table[i];
            hr = sws_setColorspaceDetails(img_convert_ctx, invTable, 1, Table, 1, brightness, contrast, saturation);
            return true;
        }

        /// <summary>
        /// <see cref="sws_scale(SwsContext*, byte*[], int[], int, int, byte*[], int[])"/>
        /// </summary>
        /// <remarks>如果原始帧是硬件帧，会先通过<see cref="av_hwframe_transfer_data"/>将数据搬到内存上；
        /// 由于像素格式复制，难以通过av_get_padded_bits_per_pixel与<paramref name="dstWidth"/>计算跨距，因此单独加一个参数；
        /// 转换时会忽略color_range的转换，我们在自定义渲染器上通过GPU进行处理；</remarks>
        public bool SwsScale(AVFrame* src, int dstWidth, int dstHeight, AVPixelFormat dstFmt, void* dst, int dstStride)
        {
            AVFrame* srcTemp = src;
            try
            {
                if (src->hw_frames_ctx != null)
                {
                    srcTemp = av_frame_alloc();
                    if (srcTemp == null) return false;
                    AVPixelFormat* fmts = default;
                    int ret = av_hwframe_transfer_get_formats(src->hw_frames_ctx, AVHWFrameTransferDirection.AV_HWFRAME_TRANSFER_DIRECTION_FROM, &fmts, 0);
                    if (ret != 0) return false;
                    for (int i = 0; ; i++)
                    {
                        AVPixelFormat swsfmt = fmts[i];
                        if (swsfmt == AVPixelFormat.AV_PIX_FMT_NONE) return false;
                        srcTemp->format = (int)swsfmt;
                        ret = av_hwframe_transfer_data(srcTemp, src, 0);
                        if (ret == 0 && CheckSwsCtx(srcTemp, dstWidth, dstHeight, dstFmt)) break;
                    }
                }
                else if (!CheckSwsCtx(srcTemp, dstWidth, dstHeight, dstFmt))
                {
                    return false;
                }
                dstArray[0] = (byte*)dst;
                dstStrideArray[0] = dstStride;
                _ = sws_scale(img_convert_ctx, srcTemp->data, srcTemp->linesize, 0, srcTemp->height, dstArray, dstStrideArray);
                return true;
            }
            finally
            {
                if (srcTemp != src) av_frame_free(&srcTemp);
            }
        }

        public void Dispose()
        {
            if (img_convert_ctx != null)
                sws_freeContext(img_convert_ctx);
            img_convert_ctx = null;
        }
    }
}
