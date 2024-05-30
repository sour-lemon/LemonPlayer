using FFmpeg.AutoGen;
using System;
using System.Numerics;

namespace LemonPlayer
{
    public static class ColorMatrix
    {
        static readonly Matrix4x4 matYuv709ToRgb = new Matrix4x4(
            1, 1, 1, 0,
            0, -0.1873242729f, 1.8556f, 0,
            1.5748f, -0.4681242729f, 0, 0,
            0, 0, 0, 1);
        static readonly Matrix4x4 matYuv601ToRgb = new Matrix4x4(
            1, 1, 1, 0,
            0, -0.3441362862f, 1.772f, 0,
            1.402f, -0.7141362862f, 0, 0,
            0, 0, 0, 1);
        static readonly Matrix4x4 matYuv2020ToRgb = new Matrix4x4(
            1, 1, 1, 0,
            0, -0.1645531268f, 1.8814f, 0,
            1.4746f, -0.5713918732f, 0, 0,
            0, 0, 0, 1);

        public static Matrix4x4 Yuv709ToRgb => matYuv709ToRgb;
        public static Matrix4x4 Yuv601ToRgb => matYuv601ToRgb;
        public static Matrix4x4 Yuv2020ToRgb => matYuv2020ToRgb;

        /// <summary>
        /// 获取格式转换矩阵，已包含了颜色空间的转换
        /// </summary>
        /// <param name="frame">包含帧的一些信息</param>
        /// <param name="bits">位深度</param>
        /// <returns>转换矩阵，包含颜色范围及yuv到rgb的转换</returns>
        /// <remarks>对于<see cref="AVColorRange"/>的转换，需要知道位深度，而对于<see cref="AVPixelFormat.AV_PIX_FMT_D3D11"/>等类型又无法得知相关信息，因此需要单独指定</remarks>
        public static Matrix4x4 GetColorMatrix(VideoFrame frame, int bits)
        {
            var matColor = GetMatYuvToRgb(frame);
            if (frame.color_range == AVColorRange.AVCOL_RANGE_JPEG)
                return matColor;
            return GetMatLimitToFull(bits, true) * matColor;
        }

        public static Matrix4x4 GetMatLimitToFull(int bits, bool yuv)
        {
            float max = (float)Math.Pow(2, bits) - 1;
            float val = (float)Math.Pow(2, bits - 8);
            float yMax = 219f * val;

            if (yuv)
            {
                float uvMax = 224f * val;
                return new Matrix4x4(
                    max / yMax, 0, 0, 0,
                    0, max / uvMax, 0, 0,
                    0, 0, max / uvMax, 0,
                    -16f * val / yMax, -128f * val / uvMax, -128f * val / uvMax, 1);
            }
            else
            {
                return new Matrix4x4(
                    max / yMax, 0, 0, 0,
                    0, max / yMax, 0, 0,
                    0, 0, max / yMax, 0,
                    -16f * val / yMax, -16f * val / yMax, -16f * val / yMax, 1);
            }
        }

        public static Matrix4x4 GetMatYuvToRgb(VideoFrame frame)
        {
            Matrix4x4 yuvToRgb;
            if (frame.colorspace == AVColorSpace.AVCOL_SPC_BT470BG)
                yuvToRgb = matYuv601ToRgb;
            else if (frame.colorspace == AVColorSpace.AVCOL_SPC_BT709)
                yuvToRgb = matYuv709ToRgb;
            else if (frame.colorspace == AVColorSpace.AVCOL_SPC_BT2020_CL ||
                frame.colorspace == AVColorSpace.AVCOL_SPC_BT2020_NCL)
                yuvToRgb = matYuv2020ToRgb;
            else if (frame.height > 576)
                yuvToRgb = matYuv709ToRgb;
            else
                yuvToRgb = matYuv601ToRgb;
            return yuvToRgb;
        }
    }
}
