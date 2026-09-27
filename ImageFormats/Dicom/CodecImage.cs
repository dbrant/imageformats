/*

The common result type of the compressed-image decoders used by the DICOM reader
(JPEG, JPEG-LS, JPEG 2000, RLE). Samples are stored as plain integers, one per
component per pixel, interleaved (all components of the first pixel, then all
components of the second, and so on), top-down. Interpreting them (windowing,
color conversion and the like) is left to the caller, which knows what the
samples mean.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class CodecImage
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Number of components (samples) per pixel.</summary>
        public int Components { get; }

        /// <summary>Number of significant bits in each sample.</summary>
        public int Precision { get; }

        /// <summary>
        /// True if the samples are signed values, i.e. the codec itself knows them to
        /// be signed and has already sign-extended them.
        /// </summary>
        public bool Signed { get; }

        /// <summary>
        /// True if the codec has already converted color samples to RGB (for example,
        /// a lossy JPEG stored as YCbCr, or a JPEG 2000 image with a component
        /// transform), so the caller should not apply a color transform of its own.
        /// </summary>
        public bool ColorConverted { get; set; }

        /// <summary>Width * Height * Components samples, interleaved by pixel.</summary>
        public int[] Samples { get; }

        public CodecImage(int width, int height, int components, int precision, bool signed, int[] samples)
        {
            Width = width;
            Height = height;
            Components = components;
            Precision = precision;
            Signed = signed;
            Samples = samples;
        }
    }
}
