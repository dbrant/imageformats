using System;

/*

The contents of an object buffer in an MNG datastream: an image as decoded from a PNG,
JNG or BASI datastream, kept in its original form (samples, palette, transparency and
gamma), so that a Delta-PNG datastream can later modify it.

JNG images are kept as decoded JPEG samples (gray, or RGB already converted from YCbCr)
followed by an alpha sample if there is one; the alpha samples can have a sample depth
of their own.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class MngImage
    {
        public int Width;
        public int Height;

        /// <summary>PNG color type (0, 2, 3, 4 or 6), or JNG color type (8, 10, 12 or 14).</summary>
        public int ColorType;

        /// <summary>Sample depth; for a JNG image, that of its color samples.</summary>
        public int Depth;

        /// <summary>Sample depth of the alpha samples of a JNG image.</summary>
        public int AlphaDepth;

        /// <summary>The samples of the image, top-down, with all the samples of each pixel together.</summary>
        public int[] Samples = Array.Empty<int>();

        /// <summary>Palette of an indexed image, as red, green and blue bytes.</summary>
        public byte[]? Palette;

        /// <summary>Alpha values of the palette entries, from tRNS, if any; missing entries are opaque.</summary>
        public byte[]? PaletteAlpha;

        /// <summary>The gray or RGB value of a gray or RGB image that is transparent, from tRNS, if any.</summary>
        public int[]? TransparentKey;

        /// <summary>The gamma with which the color samples are encoded (from gAMA or sRGB), if known.</summary>
        public double? Gamma;

        public bool Viewable = true;

        // The pixels as returned by ToBgra, until the image changes.
        private byte[]? bgra;

        // How to decode the pixel data of a subsequent Delta-PNG datastream that doesn't give
        // these itself.
        public int PixelSampleDepth;
        public int AlphaSampleDepth;
        public int FilterMethod;
        public int InterlaceMethod;

        public bool IsJng => ColorType >= 8;
        public bool HasAlpha => ColorType is 4 or 6 or 12 or 14;
        public int Channels => ColorType switch { 0 or 3 or 8 => 1, 4 or 12 => 2, 2 or 10 => 3, _ => 4 };
        public int ColorChannels => HasAlpha ? Channels - 1 : Channels;

        public MngImage Clone()
        {
            var copy = (MngImage)MemberwiseClone();
            copy.Samples = (int[])Samples.Clone();
            copy.Palette = (byte[]?)Palette?.Clone();
            copy.PaletteAlpha = (byte[]?)PaletteAlpha?.Clone();
            copy.TransparentKey = (int[]?)TransparentKey?.Clone();
            copy.bgra = null;
            return copy;
        }

        /// <summary>
        /// Replaces the contents of this buffer with those of another, so that every object
        /// that shares this buffer sees the change.
        /// </summary>
        public void CopyFrom(MngImage other)
        {
            Width = other.Width;
            Height = other.Height;
            ColorType = other.ColorType;
            Depth = other.Depth;
            AlphaDepth = other.AlphaDepth;
            Samples = other.Samples;
            Palette = other.Palette;
            PaletteAlpha = other.PaletteAlpha;
            TransparentKey = other.TransparentKey;
            Gamma = other.Gamma;
            Viewable = other.Viewable;
            PixelSampleDepth = other.PixelSampleDepth;
            AlphaSampleDepth = other.AlphaSampleDepth;
            FilterMethod = other.FilterMethod;
            InterlaceMethod = other.InterlaceMethod;
            bgra = null;
        }

        /// <summary>
        /// Must be called after changing the image, other than through the methods of this class.
        /// </summary>
        public void Invalidate()
        {
            bgra = null;
        }

        /// <summary>
        /// Converts the image to 8-bit BGRA pixels with straight alpha, correcting the color
        /// samples for a display gamma of 2.2 if the image's gamma is known. The result must
        /// not be modified.
        /// </summary>
        public byte[] ToBgra()
        {
            return bgra ??= BuildBgra();
        }

        private byte[] BuildBgra()
        {
            int pixels = Width * Height;
            var data = new byte[pixels * 4];
            int channels = Channels;
            var s = Samples;

            if (ColorType == 3)
            {
                var lut = GammaTable(255, Gamma);
                var bgra = new byte[256 * 4];
                int entries = Palette == null ? 0 : Math.Min(256, Palette.Length / 3);
                for (int i = 0; i < entries; i++)
                {
                    bgra[i * 4] = lut[Palette![i * 3 + 2]];
                    bgra[i * 4 + 1] = lut[Palette[i * 3 + 1]];
                    bgra[i * 4 + 2] = lut[Palette[i * 3]];
                    bgra[i * 4 + 3] = PaletteAlpha != null && i < PaletteAlpha.Length ? PaletteAlpha[i] : (byte)0xFF;
                }
                for (int i = 0; i < pixels; i++)
                    Array.Copy(bgra, (s[i] & 0xFF) * 4, data, i * 4, 4);
                return data;
            }

            int maxColor = (1 << Depth) - 1;
            int maxAlpha = IsJng ? (1 << AlphaDepth) - 1 : maxColor;
            var colorLut = GammaTable(maxColor, Gamma);
            bool gray = ColorChannels == 1;
            var key = TransparentKey;
            for (int i = 0, o = 0, p = 0; i < pixels; i++, o += 4, p += channels)
            {
                int r = s[p], g = gray ? r : s[p + 1], b = gray ? r : s[p + 2];
                data[o] = colorLut[b];
                data[o + 1] = colorLut[g];
                data[o + 2] = colorLut[r];
                if (HasAlpha)
                    data[o + 3] = Scale(s[p + channels - 1], maxAlpha);
                else if (key != null && r == key[0] && (gray || (key.Length >= 3 && g == key[1] && b == key[2])))
                    data[o + 3] = 0;
                else
                    data[o + 3] = 0xFF;
            }
            return data;
        }

        private static byte Scale(int value, int max)
        {
            return max == 255 ? (byte)value : (byte)((Math.Clamp(value, 0, max) * 255 + max / 2) / max);
        }

        /// <summary>
        /// Builds a table that converts samples from 0 to max into 8-bit display values.
        /// </summary>
        public static byte[] GammaTable(int max, double? gamma)
        {
            var table = new byte[max + 1];
            double exponent = gamma > 0 ? 1 / (gamma.Value * 2.2) : 1;
            if (Math.Abs(exponent - 1) < 0.01)
            {
                for (int i = 0; i <= max; i++)
                    table[i] = (byte)((i * 255 + max / 2) / max);
            }
            else
            {
                for (int i = 0; i <= max; i++)
                    table[i] = (byte)(Math.Pow((double)i / max, exponent) * 255 + 0.5);
            }
            return table;
        }

        /// <summary>
        /// Creates an image filled with a single color, as described by a BASI chunk. The
        /// samples are given at the image's sample depth; a null alpha means opaque.
        /// </summary>
        public static MngImage Filled(int width, int height, int colorType, int depth, int red, int green, int blue, int? alpha)
        {
            PngDecoder.Validate(colorType, depth);
            int channels = PngDecoder.Channels(colorType);
            if (width <= 0 || height <= 0 || (long)width * height * channels > CodecImage.MaxSamples)
                throw new ImageDecodeException("MNG image has invalid dimensions.");
            int max = (1 << depth) - 1;
            var image = new MngImage
            {
                Width = width,
                Height = height,
                ColorType = colorType,
                Depth = depth,
                PixelSampleDepth = depth,
                AlphaSampleDepth = depth,
                Samples = new int[width * height * channels]
            };
            int[] pixel = colorType switch
            {
                0 => new[] { red & max },
                2 => new[] { red & max, green & max, blue & max },
                3 => new[] { 0 },
                4 => new[] { red & max, alpha ?? max },
                _ => new[] { red & max, green & max, blue & max, alpha ?? max }
            };
            for (int i = 0; i < image.Samples.Length; i += channels)
                pixel.CopyTo(image.Samples, i);

            if (colorType == 3)
            {
                image.Palette = new byte[(1 << depth) * 3];
                image.Palette[0] = (byte)red;
                image.Palette[1] = (byte)green;
                image.Palette[2] = (byte)blue;
                if (alpha != null && (alpha & 0xFF) != 0xFF)
                    image.PaletteAlpha = new[] { (byte)alpha.Value };
            }
            else if ((colorType == 0 || colorType == 2) && alpha == 0)
            {
                image.TransparentKey = (int[])pixel.Clone();
            }
            return image;
        }

        /// <summary>
        /// Converts the image to a color type with more channels and/or a greater sample depth,
        /// as directed by a PROM chunk. For a JNG image, the sample depth is that of the alpha
        /// samples.
        /// </summary>
        public void Promote(int newColorType, int newDepth, bool zeroFill)
        {
            if (IsJng)
            {
                PromoteJng(newColorType, newDepth, zeroFill);
                return;
            }

            PngDecoder.Validate(newColorType, newDepth);
            if (ColorType == 3 && newColorType == 3)
            {
                // Palette indexes keep their values.
                Depth = Math.Max(Depth, newDepth);
                return;
            }
            int pixels = Width * Height;
            int oldChannels = Channels, newChannels = PngDecoder.Channels(newColorType);
            bool addAlpha = !HasAlpha && newColorType is 4 or 6;
            bool toColor = ColorChannels == 1 && newColorType is 2 or 6;
            if ((HasAlpha && newColorType is 0 or 2) || (ColorChannels == 3 && newColorType is 0 or 4) || (ColorType != 3 && newColorType == 3))
                throw new ImageDecodeException("Invalid MNG promotion from color type " + ColorType + " to " + newColorType + ".");

            int oldDepth = Depth;
            var samples = new int[pixels * newChannels];
            if (ColorType == 3 && newColorType != 3)
            {
                // Expand the palette, whose samples always have a depth of 8.
                oldDepth = 8;
                int entries = Palette == null ? 0 : Palette.Length / 3;
                for (int i = 0; i < pixels; i++)
                {
                    int index = Samples[i], o = i * newChannels;
                    for (int c = 0; c < 3; c++)
                        samples[o + c] = index < entries ? Palette![index * 3 + c] : 0;
                    if (newChannels == 4)
                        samples[o + 3] = PaletteAlpha != null && index < PaletteAlpha.Length ? PaletteAlpha[index] : 0xFF;
                }
                Palette = null;
                PaletteAlpha = null;
            }
            else
            {
                int max = (1 << oldDepth) - 1;
                int colorChannels = ColorChannels;
                for (int i = 0; i < pixels; i++)
                {
                    int p = i * oldChannels, o = i * newChannels;
                    bool transparent = TransparentKey != null && Samples[p] == TransparentKey[0]
                        && (colorChannels == 1 || (Samples[p + 1] == TransparentKey[1] && Samples[p + 2] == TransparentKey[2]));
                    for (int c = 0; c < (toColor ? 3 : colorChannels); c++)
                        samples[o + c] = Samples[p + (toColor ? 0 : c)];
                    if (addAlpha)
                        samples[o + newChannels - 1] = transparent ? 0 : max;
                    else if (HasAlpha)
                        samples[o + newChannels - 1] = Samples[p + oldChannels - 1];
                }
                if (addAlpha)
                    TransparentKey = null;
                else if (toColor && TransparentKey != null)
                    TransparentKey = new[] { TransparentKey[0], TransparentKey[0], TransparentKey[0] };
            }

            if (newDepth != oldDepth)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = Widen(samples[i], oldDepth, newDepth, zeroFill);
                if (TransparentKey != null)
                {
                    for (int i = 0; i < TransparentKey.Length; i++)
                        TransparentKey[i] = Widen(TransparentKey[i], oldDepth, newDepth, zeroFill);
                }
            }
            Samples = samples;
            bgra = null;
            ColorType = newColorType;
            Depth = newDepth;
        }

        private void PromoteJng(int newColorType, int newDepth, bool zeroFill)
        {
            if (newColorType is not (8 or 10 or 12 or 14) || (HasAlpha && newColorType is 8 or 10) || (ColorChannels == 3 && newColorType is 8 or 12))
                throw new ImageDecodeException("Invalid MNG promotion from color type " + ColorType + " to " + newColorType + ".");
            int pixels = Width * Height;
            int oldChannels = Channels;
            bool newAlpha = newColorType is 12 or 14;
            int newChannels = (newColorType is 10 or 14 ? 3 : 1) + (newAlpha ? 1 : 0);
            bool toColor = ColorChannels == 1 && newColorType is 10 or 14;
            int alphaDepth = newAlpha ? newDepth : 0;
            var samples = new int[pixels * newChannels];
            for (int i = 0; i < pixels; i++)
            {
                int p = i * oldChannels, o = i * newChannels;
                for (int c = 0; c < (toColor ? 3 : ColorChannels); c++)
                    samples[o + c] = Samples[p + (toColor ? 0 : c)];
                if (newAlpha)
                {
                    samples[o + newChannels - 1] = HasAlpha
                        ? Widen(Samples[p + oldChannels - 1], AlphaDepth, alphaDepth, zeroFill)
                        : (1 << alphaDepth) - 1;
                }
            }
            Samples = samples;
            bgra = null;
            ColorType = newColorType;
            if (newAlpha)
                AlphaDepth = alphaDepth;
        }

        /// <summary>
        /// Scales a sample to a different sample depth: widening by left bit replication or
        /// by zero filling, or narrowing by dropping the low bits.
        /// </summary>
        public static int Widen(int value, int depth, int newDepth, bool zeroFill)
        {
            if (newDepth <= depth || depth <= 0)
                return value >> Math.Max(0, depth - newDepth);
            if (zeroFill)
                return value << (newDepth - depth);
            int result = 0;
            for (int shift = newDepth - depth; shift > -depth; shift -= depth)
                result |= shift >= 0 ? value << shift : value >> -shift;
            return result;
        }

        /// <summary>
        /// Magnifies the image as directed by a MAGN chunk. An indexed image, or one with a
        /// transparent color, is first converted to one with an alpha channel, so that its
        /// samples can be interpolated.
        /// </summary>
        public void Magnify(int methodX, int methodY, int mx, int my, int ml, int mr, int mt, int mb)
        {
            if (methodX == 0 && methodY == 0)
                return;
            if (!IsJng && (ColorType == 3 || TransparentKey != null))
                Promote(ColorType == 0 ? 4 : 6, ColorType == 3 ? 8 : Depth, false);

            var tapsY = Taps(Height, methodY, mt, my, mb);
            var tapsX = Taps(Width, methodX, ml, mx, mr);
            int channels = Channels;
            if ((long)tapsX.Length * tapsY.Length * channels > CodecImage.MaxSamples)
                throw new ImageDecodeException("Magnified MNG image is too large.");

            // Interpolate between rows, then between columns.
            int rowLength = Width * channels;
            var rows = new int[tapsY.Length * rowLength];
            for (int y = 0; y < tapsY.Length; y++)
            {
                var t = tapsY[y];
                for (int x = 0; x < Width; x++)
                    for (int c = 0; c < channels; c++)
                        rows[y * rowLength + x * channels + c] = Interpolate(Samples[t.From * rowLength + x * channels + c],
                            Samples[t.To * rowLength + x * channels + c], t, IsLinear(methodY, c, channels));
            }
            int newRowLength = tapsX.Length * channels;
            var result = new int[tapsY.Length * newRowLength];
            for (int y = 0; y < tapsY.Length; y++)
            {
                for (int x = 0; x < tapsX.Length; x++)
                {
                    var t = tapsX[x];
                    for (int c = 0; c < channels; c++)
                        result[y * newRowLength + x * channels + c] = Interpolate(rows[y * rowLength + t.From * channels + c],
                            rows[y * rowLength + t.To * channels + c], t, IsLinear(methodX, c, channels));
                }
            }
            Samples = result;
            bgra = null;
            Width = tapsX.Length;
            Height = tapsY.Length;
        }

        private bool IsLinear(int method, int channel, int channels)
        {
            bool alpha = HasAlpha && channel == channels - 1;
            return method == 2 || (method == 4 && !alpha) || (method == 5 && alpha);
        }

        /// <summary>
        /// One output sample of a magnified row or column: Step/Steps of the way from the
        /// sample at From to the one at To.
        /// </summary>
        private readonly record struct Tap(int From, int To, int Step, int Steps);

        private static Tap[] Taps(int length, int method, int first, int middle, int last)
        {
            first = Math.Max(1, first);
            middle = Math.Max(1, middle);
            last = Math.Max(1, last);
            var taps = new System.Collections.Generic.List<Tap>();
            if (method == 0)
            {
                for (int i = 0; i < length; i++)
                    taps.Add(new Tap(i, i, 0, 1));
            }
            else if (method == 1 || length == 1)
            {
                // Replicate each pixel, the first and last ones by their own factors.
                for (int i = 0; i < length; i++)
                {
                    int count = i == 0 ? first : i == length - 1 ? last : middle;
                    for (int k = 0; k < count; k++)
                        taps.Add(new Tap(i, i, 0, 1));
                }
            }
            else
            {
                // Divide each interval between pixels, the first and last ones by their own factors.
                for (int i = 0; i < length - 1; i++)
                {
                    int steps = i == 0 ? first : i == length - 2 ? last : middle;
                    for (int k = 0; k < steps; k++)
                        taps.Add(new Tap(i, i + 1, k, steps));
                }
                taps.Add(new Tap(length - 1, length - 1, 0, 1));
            }
            return taps.ToArray();
        }

        private static int Interpolate(int s0, int s1, Tap t, bool linear)
        {
            if (t.Step == 0 || s0 == s1)
                return s0;
            if (linear)
                return (2 * t.Step * (s1 - s0) + t.Steps) / (t.Steps * 2) + s0;
            return t.Step * 2 <= t.Steps ? s0 : s1;
        }
    }
}
