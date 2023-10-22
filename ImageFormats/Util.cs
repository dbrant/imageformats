using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;

namespace DmitryBrant.ImageFormats
{
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

        public static Image LoadRgba(int width, int height, byte[] data)
        {
            return Image.LoadPixelData<Bgra32>(data, width, height);
        }

        public static Image LoadRgb(int width, int height, byte[] data)
        {
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 0xFF;
            return Image.LoadPixelData<Bgra32>(data, width, height);
        }

        public static Image ResizeTo(this Image original, Size newSize)
        {
            return original.Clone(x => x.Resize(newSize));
        }

        public static uint ToArgb(this Color color)
        {
            return color.ToPixel<Argb32>().Argb;
        }

        public static void Flip(this Image image, params FlipMode[] modes)
        {
            image.Mutate(x =>
            {
                foreach (var mode in modes)
                    x.Flip(mode);
            });
        }
    }

    public class ImageDecodeException : Exception
    {
        protected ImageDecodeException() : base() { }
        public ImageDecodeException(string message) : base(message) { }
    }
}
