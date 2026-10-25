using System;

/*

Color interpolation ("demosaicing") of raw images from sensors with a color filter
array, such as a Bayer pattern.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class Demosaic
    {
        /// <summary>
        /// Fills in the two missing channels at each pixel of a mosaic, by averaging the
        /// neighboring pixels of each of those channels. Every channel must appear in the
        /// 3x3 neighborhood of every pixel.
        /// </summary>
        /// <param name="mosaic">One sample per pixel, top-down.</param>
        /// <param name="width">Width of the image, in pixels.</param>
        /// <param name="height">Height of the image, in pixels.</param>
        /// <param name="cfa">The output channel (0 to 2) of each pixel of the filter pattern
        /// that repeats over the image, row by row, starting at the top left.</param>
        /// <param name="patternWidth">Width of the filter pattern, in pixels.</param>
        /// <returns>Three samples per pixel, in the order of the channels.</returns>
        public static int[] Bilinear(int[] mosaic, int width, int height, ReadOnlySpan<int> cfa, int patternWidth = 2)
        {
            int patternHeight = cfa.Length / patternWidth;

            var result = new int[width * height * 3];
            Span<int> sum = stackalloc int[3];
            Span<int> n = stackalloc int[3];
            for (int y = 0; y < height; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(width - 1, x + 1);
                    sum.Clear();
                    n.Clear();
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            int c = cfa[(yy % patternHeight) * patternWidth + xx % patternWidth];
                            sum[c] += mosaic[yy * width + xx];
                            n[c]++;
                        }
                    }
                    int i = y * width + x;
                    int own = cfa[(y % patternHeight) * patternWidth + x % patternWidth];
                    for (int c = 0; c < 3; c++)
                        result[i * 3 + c] = c == own ? mosaic[i] : n[c] > 0 ? (sum[c] + n[c] / 2) / n[c] : 0;
                }
            }
            return result;
        }
    }
}
