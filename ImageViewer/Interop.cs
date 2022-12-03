using System.IO;
using SixLabors.ImageSharp;
using WinImg = System.Drawing.Image;
using WinBit = System.Drawing.Bitmap;
using SixImg = SixLabors.ImageSharp.Image;

namespace ImageViewer
{
    internal static class Interop
    {
        public static WinBit AsNative(this SixImg image)
        {
            if (image == null)
                return null;
            using var mem = new MemoryStream();
            image.SaveAsPng(mem);
            var bitmap = WinImg.FromStream(mem);
            return (WinBit)bitmap;
        }
    }
}