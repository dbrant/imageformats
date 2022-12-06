using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DmitryBrant.ImageFormats
{
    internal static class ImageTool
    {
        public static Image LoadRgba(int width, int height, byte[] data)
        {
            return Image.LoadPixelData<Bgra32>(data, width, height);
        }

        public static Image LoadRgb(int width, int height, byte[] data)
        {
            const byte alpha = byte.MaxValue;
            for (var i = 0; i < data.Length; i += 4)
                data[i + 3] = alpha;
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
}