using System.IO;
using SixLabors.ImageSharp;

namespace ImageViewer
{
    internal static class Interop
    {
        public static System.Drawing.Bitmap FromImageSharp(this Image image)
        {
            if (image == null)
                return null;
            using var mem = new MemoryStream();
            image.SaveAsPng(mem);
            return (System.Drawing.Bitmap)System.Drawing.Image.FromStream(mem);
        }
    }
}
