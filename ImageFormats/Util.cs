using System;

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Which axis to mirror an image about.
    /// </summary>
    public enum FlipMode
    {
        /// <summary>Mirror left to right.</summary>
        Horizontal,
        /// <summary>Mirror top to bottom.</summary>
        Vertical
    }

    public static class Util
    {
        public static void log(string str)
        {
            System.Diagnostics.Debug.WriteLine(str);
        }

        public static bool TryParseFloat(string str, out float f)
        {
            return float.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);
        }

        public static ushort LittleEndian(ushort val)
        {
            return BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }
        public static uint LittleEndian(uint val)
        {
            return BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }

        public static ushort BigEndian(ushort val)
        {
            return !BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }
        public static uint BigEndian(uint val)
        {
            return !BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }

        private static ushort ConvEndian(ushort val)
        {
            ushort temp;
            temp = (ushort)(val << 8); temp &= 0xFF00; temp |= (ushort)((val >> 8) & 0xFF);
            return temp;
        }
        private static uint ConvEndian(uint val)
        {
            uint temp = (val & 0x000000FF) << 24;
            temp |= (val & 0x0000FF00) << 8;
            temp |= (val & 0x00FF0000) >> 8;
            temp |= (val & 0xFF000000) >> 24;
            return temp;
        }

        /// <summary>
        /// Wrap a buffer of BGRA pixel data, in which the alpha channel is meaningful,
        /// into an ImageData object.
        /// </summary>
        public static ImageData LoadRgba(int width, int height, byte[] data)
        {
            return new ImageData(width, height, data);
        }

        /// <summary>
        /// Wrap a buffer of BGRA pixel data, in which the alpha channel was never
        /// populated, into an ImageData object. The alpha channel is forced to opaque.
        /// </summary>
        public static ImageData LoadRgb(int width, int height, byte[] data)
        {
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 0xFF;
            return new ImageData(width, height, data);
        }

        /// <summary>
        /// Mirror the given image in place, about each of the given axes in turn.
        /// </summary>
        public static void Flip(this ImageData image, params FlipMode[] modes)
        {
            foreach (var mode in modes)
            {
                if (mode == FlipMode.Horizontal)
                    FlipHorizontal(image);
                else
                    FlipVertical(image);
            }
        }

        private static void FlipHorizontal(ImageData image)
        {
            byte[] data = image.Data;
            int stride = image.Stride;
            var temp = new byte[ImageData.BytesPerPixel];
            for (int y = 0; y < image.Height; y++)
            {
                int left = y * stride;
                int right = left + stride - ImageData.BytesPerPixel;
                while (left < right)
                {
                    Buffer.BlockCopy(data, left, temp, 0, ImageData.BytesPerPixel);
                    Buffer.BlockCopy(data, right, data, left, ImageData.BytesPerPixel);
                    Buffer.BlockCopy(temp, 0, data, right, ImageData.BytesPerPixel);
                    left += ImageData.BytesPerPixel;
                    right -= ImageData.BytesPerPixel;
                }
            }
        }

        private static void FlipVertical(ImageData image)
        {
            byte[] data = image.Data;
            int stride = image.Stride;
            var temp = new byte[stride];
            int top = 0, bottom = (image.Height - 1) * stride;
            while (top < bottom)
            {
                Buffer.BlockCopy(data, top, temp, 0, stride);
                Buffer.BlockCopy(data, bottom, data, top, stride);
                Buffer.BlockCopy(temp, 0, data, bottom, stride);
                top += stride;
                bottom -= stride;
            }
        }
    }

    public class ImageDecodeException : Exception
    {
        protected ImageDecodeException() : base() { }
        public ImageDecodeException(string message) : base(message) { }
    }
}
