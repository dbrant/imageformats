using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DmitryBrant.ImageFormats;
using System;
using System.IO;

namespace ImageViewer
{
    internal static class Interop
    {
        /// <summary>
        /// Convert the raw pixel data returned by the ImageFormats library into an
        /// Avalonia bitmap. The library hands back its pixels in exactly the layout
        /// that Bgra8888 expects, so this is a straight copy, row by row.
        /// </summary>
        unsafe public static Bitmap ToAvaloniaBitmap(this ImageData image)
        {
            if (image == null)
                return null;

            // A few formats wrap their pixel data in some other well-known encoding
            // (for example, a DICOM file that contains a JPEG), which the library
            // passes through untouched. Let Avalonia decode those itself.
            if (image.IsEncoded)
            {
                using var encoded = new MemoryStream(image.EncodedData);
                return new Bitmap(encoded);
            }
            Bitmap bitmap;
            fixed (byte* data = image.Data)
            {
                IntPtr ptr = (IntPtr)data;
                bitmap = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Unpremul, ptr, new PixelSize(image.Width, image.Height), new Vector(96.0, 96.0), image.Stride);
            }
            return bitmap;
        }
    }
}
