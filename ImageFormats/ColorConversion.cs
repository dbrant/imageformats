using System;

/*

Color space conversions shared by more than one decoder.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class ColorConversion
    {
        /// <summary>
        /// Encodes a value in linear light, nominally between 0 and 1, as an 8-bit sRGB value.
        /// </summary>
        public static byte LinearToSrgbByte(double v)
        {
            if (!(v > 0)) // also catches NaN
                return 0;
            if (v >= 1)
                return 255;
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)(int)(v * 255 + 0.5);
        }

        /// <summary>
        /// Converts CIE L*a*b* (D50) to sRGB, returned as (red << 16) | (green << 8) | blue.
        /// </summary>
        /// <param name="l">L*, from 0 to 100.</param>
        /// <param name="a">a*, nominally from -128 to 127.</param>
        /// <param name="b">b*, nominally from -128 to 127.</param>
        public static int LabToRgb(double l, double a, double b)
        {
            static double FInv(double t) => t > 6.0 / 29 ? Math.Pow(t, 3) : 3 * Math.Pow(6.0 / 29, 2) * (t - 4.0 / 29);

            double fy = (l + 16) / 116;
            double x = 0.96422 * FInv(fy + a / 500.0);
            double y = FInv(fy);
            double z = 0.82521 * FInv(fy - b / 200.0);
            // XYZ (D50) to linear sRGB, with Bradford adaptation to D65.
            return (LinearToSrgbByte(3.1338561 * x - 1.6168667 * y - 0.4906146 * z) << 16)
                | (LinearToSrgbByte(-0.9787684 * x + 1.9161415 * y + 0.0334540 * z) << 8)
                | LinearToSrgbByte(0.0719453 * x - 0.2289914 * y + 1.4052427 * z);
        }
    }
}
