#nullable enable
using System;
using System.Collections.Generic;

/*

Composites the layers of a Photoshop document the way Photoshop does, for files
whose merged image is missing or only a placeholder.

The layers are drawn bottom to top. A layer's coverage of each pixel is the product
of its transparency, its enabled masks (which have their default color outside their
rectangles), its opacity and its fill opacity. Hidden layers and groups are skipped,
along with everything inside a hidden group.

  * Clipping: a layer followed by layers that are clipped to it forms a clipping
    group. The base layer is drawn on its own, then each clipped layer is drawn onto
    it, keeping the base's transparency, and the result is composited with the base
    layer's blend mode and opacity.

  * Groups: a group with a blend mode other than "pass through" is drawn on its own,
    and the result is composited with the group's blend mode, opacity and mask. A
    pass-through group draws its contents directly onto what lies beneath it, and if
    it has an opacity or a mask, the result is faded back towards what was beneath.

  * Compositing follows the W3C/PDF compositing model, which matches Photoshop for
    straight (non-premultiplied) 8-bit data. With backdrop color b and alpha ab,
    source color s and coverage as, and blend function B:

      ao = as + ab(1 - as)
      co = (as((1 - ab)s + ab B(b, s)) + (1 - as)ab b) / ao

Layer effects (drop shadows and the like), adjustment and fill layers, vector masks,
and "Blend If" ranges are not rendered, since they exist only as settings, which
Photoshop renders into its merged image.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class PsdCompositor
    {
        private enum Blend
        {
            Normal, Dissolve, Darken, Multiply, ColorBurn, LinearBurn, DarkerColor, Lighten, Screen,
            ColorDodge, LinearDodge, LighterColor, Overlay, SoftLight, HardLight, VividLight, LinearLight,
            PinLight, HardMix, Difference, Exclusion, Subtract, Divide, Hue, Saturation, Color, Luminosity
        }

        private static readonly Dictionary<string, Blend> BlendModes = new()
        {
            // Pass through applies only to groups, and is handled separately.
            ["pass"] = Blend.Normal,
            ["norm"] = Blend.Normal,
            ["diss"] = Blend.Dissolve,
            ["dark"] = Blend.Darken,
            ["mul "] = Blend.Multiply,
            ["idiv"] = Blend.ColorBurn,
            ["lbrn"] = Blend.LinearBurn,
            ["dkCl"] = Blend.DarkerColor,
            ["lite"] = Blend.Lighten,
            ["scrn"] = Blend.Screen,
            ["div "] = Blend.ColorDodge,
            ["lddg"] = Blend.LinearDodge,
            ["lgCl"] = Blend.LighterColor,
            ["over"] = Blend.Overlay,
            ["sLit"] = Blend.SoftLight,
            ["hLit"] = Blend.HardLight,
            ["vLit"] = Blend.VividLight,
            ["lLit"] = Blend.LinearLight,
            ["pLit"] = Blend.PinLight,
            ["hMix"] = Blend.HardMix,
            ["diff"] = Blend.Difference,
            ["smud"] = Blend.Exclusion,
            ["fsub"] = Blend.Subtract,
            ["fdiv"] = Blend.Divide,
            ["hue "] = Blend.Hue,
            ["sat "] = Blend.Saturation,
            ["colr"] = Blend.Color,
            ["lum "] = Blend.Luminosity
        };

        // Tagged blocks that mark layers with features we can't render.
        private static readonly string[] EffectKeys = { "lrFX", "lfx2", "lmfx" };
        private static readonly string[] VectorMaskKeys = { "vmsk", "vsms" };
        private static readonly string[] FillKeys = { "SoCo", "GdFl", "PtFl" };
        private static readonly string[] AdjustmentKeys =
        {
            "brit", "levl", "curv", "expA", "vibA", "hue ", "hue2", "blnc", "blwh", "phfl", "mixr", "clrL",
            "nvrt", "post", "thrs", "grdm", "selc"
        };

        /// <summary>
        /// Straight (non-premultiplied) 8-bit color planes, plus an alpha plane.
        /// </summary>
        public sealed class Canvas
        {
            public readonly byte[][] Color;
            public readonly byte[] Alpha;

            public Canvas(int pixels, int channels)
            {
                Color = new byte[channels][];
                for (int c = 0; c < channels; c++)
                    Color[c] = new byte[pixels];
                Alpha = new byte[pixels];
            }

            private Canvas(byte[][] color, byte[] alpha)
            {
                Color = color;
                Alpha = alpha;
            }

            public Canvas Copy()
            {
                var color = new byte[Color.Length][];
                for (int c = 0; c < color.Length; c++)
                    color[c] = (byte[])Color[c].Clone();
                return new Canvas(color, (byte[])Alpha.Clone());
            }
        }

        /// <summary>
        /// A mask that applies to a layer or group, together with its pixels.
        /// </summary>
        private readonly struct MaskPlane
        {
            public readonly PsdMask Mask;
            public readonly byte[] Plane;

            public MaskPlane(PsdMask mask, byte[] plane)
            {
                Mask = mask;
                Plane = plane;
            }

            public int ValueAt(int x, int y)
            {
                if (y < Mask.Top || y >= Mask.Bottom || x < Mask.Left || x >= Mask.Right)
                    return Mask.DefaultColor;
                return Plane[(y - Mask.Top) * Mask.Width + (x - Mask.Left)];
            }
        }

        private readonly PsdDocument doc;
        private readonly int width;
        private readonly int height;
        private readonly int channels;
        private readonly HashSet<string> warnings = new();

        // Scratch space for blending one pixel.
        private readonly double[] backdrop = new double[3];
        private readonly double[] source = new double[3];
        private readonly double[] mixed = new double[3];

        public PsdCompositor(PsdDocument doc)
        {
            this.doc = doc;
            width = doc.Width;
            height = doc.Height;
            channels = doc.DisplayChannels;
        }

        public Canvas Render()
        {
            var canvas = NewCanvas();
            DrawNodes(canvas, doc.BuildLayerTree());
            return canvas;
        }

        private Canvas NewCanvas() => new Canvas(width * height, channels);

        private void Warn(string message)
        {
            if (warnings.Add(message))
                Util.log("PSD: " + message);
        }

        private void DrawNodes(Canvas canvas, List<PsdLayer> nodes)
        {
            int i = 0;
            while (i < nodes.Count)
            {
                var baseLayer = nodes[i];
                int j = i + 1;
                var clipped = new List<PsdLayer>();
                for (; j < nodes.Count && nodes[j].Clipping != 0; j++)
                {
                    if (!nodes[j].Hidden)
                        clipped.Add(nodes[j]);
                }
                // A hidden base layer hides everything that is clipped to it.
                if (!baseLayer.Hidden)
                {
                    if (clipped.Count > 0)
                        DrawClippingGroup(canvas, baseLayer, clipped);
                    else
                        DrawNode(canvas, baseLayer, false);
                }
                i = j;
            }
        }

        private void DrawNode(Canvas canvas, PsdLayer node, bool preserveAlpha)
        {
            if (!node.IsGroup)
            {
                DrawLayer(canvas, node, preserveAlpha);
                return;
            }

            var masks = Masks(node);
            if (node.BlendMode == "pass" && !preserveAlpha)
            {
                if (node.Opacity == 255 && masks.Length == 0)
                {
                    DrawNodes(canvas, node.Children);
                    return;
                }
                // Draw the contents straight onto a copy of the backdrop, then fade between
                // the two.
                var result = canvas.Copy();
                DrawNodes(result, node.Children);
                Mix(canvas, result, node.Opacity, masks);
                return;
            }

            var group = NewCanvas();
            DrawNodes(group, node.Children);
            string mode = node.BlendMode == "pass" ? "norm" : node.BlendMode;
            Composite(canvas, group.Color, group.Alpha, 0, 0, height, width, mode, node.Opacity, masks,
                      preserveAlpha, node.Name);
        }

        private void DrawClippingGroup(Canvas canvas, PsdLayer baseLayer, List<PsdLayer> clipped)
        {
            // The base and the layers clipped to it are composited on their own, and the
            // result then takes on the base's blend mode and opacity.
            var group = NewCanvas();
            if (baseLayer.IsGroup)
            {
                var inner = NewCanvas();
                DrawNodes(inner, baseLayer.Children);
                Composite(group, inner.Color, inner.Alpha, 0, 0, height, width, "norm", 255, Masks(baseLayer),
                          false, baseLayer.Name);
            }
            else
            {
                CheckFeatures(baseLayer);
                var (colors, alpha) = LayerPixels(baseLayer);
                Composite(group, colors, alpha, baseLayer.Top, baseLayer.Left, baseLayer.Bottom, baseLayer.Right,
                          "norm", baseLayer.FillOpacity, Masks(baseLayer), false, baseLayer.Name);
            }

            foreach (var layer in clipped)
                DrawNode(group, layer, true);

            string mode = baseLayer.BlendMode == "pass" ? "norm" : baseLayer.BlendMode;
            Composite(canvas, group.Color, group.Alpha, 0, 0, height, width, mode, baseLayer.Opacity,
                      Array.Empty<MaskPlane>(), false, baseLayer.Name);
        }

        private void DrawLayer(Canvas canvas, PsdLayer layer, bool preserveAlpha)
        {
            CheckFeatures(layer);
            if (layer.Width == 0 || layer.Height == 0)
                return;
            int opacity = (layer.Opacity * layer.FillOpacity + 127) / 255;
            var (colors, alpha) = LayerPixels(layer);
            Composite(canvas, colors, alpha, layer.Top, layer.Left, layer.Bottom, layer.Right, layer.BlendMode,
                      opacity, Masks(layer), preserveAlpha, layer.Name);
        }

        /// <summary>
        /// Reads the color planes of a layer, converted for display, and its transparency, if
        /// it has any.
        /// </summary>
        private (byte[][] Colors, byte[]? Alpha) LayerPixels(PsdLayer layer)
        {
            var planes = new byte[doc.ColorChannels][];
            for (int c = 0; c < planes.Length; c++)
                planes[c] = doc.ReadLayerChannel(layer, c);
            var alpha = layer.FindChannel(-1) != null ? doc.ReadLayerChannel(layer, -1) : null;
            return (doc.ToDisplayColor(planes), alpha);
        }

        private MaskPlane[] Masks(PsdLayer layer)
        {
            var masks = new List<MaskPlane>();
            foreach (var mask in layer.Masks)
            {
                if (!mask.Disabled && layer.FindChannel(mask.ChannelId) != null)
                    masks.Add(new MaskPlane(mask, doc.ReadLayerChannel(layer, mask.ChannelId)));
            }
            return masks.ToArray();
        }

        private void CheckFeatures(PsdLayer layer)
        {
            if (HasAnyKey(layer, EffectKeys))
                Warn("layer \"" + layer.Name + "\": layer effects are not rendered.");
            if (layer.Width == 0 && (HasAnyKey(layer, FillKeys) || HasAnyKey(layer, AdjustmentKeys)))
                Warn("layer \"" + layer.Name + "\": adjustment and fill layers are not rendered.");
            if (HasAnyKey(layer, VectorMaskKeys) && layer.FindChannel(-2) == null)
                Warn("layer \"" + layer.Name + "\": vector mask is not rendered.");
            if (layer.HasBlendRanges)
                Warn("layer \"" + layer.Name + "\": \"Blend If\" ranges are not applied.");
        }

        private static bool HasAnyKey(PsdLayer layer, string[] keys)
        {
            foreach (var key in keys)
            {
                if (layer.BlockKeys.Contains(key))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Multiplies two coverage values, where -1 stands for "no coverage specified", i.e.
        /// fully opaque, but distinct from an explicit 255.
        /// </summary>
        private static int MultiplyCoverage(int a, int b) => a < 0 ? b : (a * b + 127) / 255;

        private static byte ToByte(double x)
        {
            if (x <= 0)
                return 0;
            return x >= 255 ? (byte)255 : (byte)(int)(x + 0.5);
        }

        /// <summary>
        /// Composites planes of the given rectangle onto the canvas.
        /// </summary>
        /// <param name="alpha">Transparency of the source, or null if it is opaque.</param>
        /// <param name="preserveAlpha">If true, the canvas keeps its transparency, which is how
        /// clipped layers are drawn.</param>
        private void Composite(Canvas canvas, byte[][] colors, byte[]? alpha, int top, int left, int bottom, int right,
                               string modeKey, int opacity, MaskPlane[] masks, bool preserveAlpha, string name)
        {
            int x0 = Math.Max(left, 0), x1 = Math.Min(right, width);
            int y0 = Math.Max(top, 0), y1 = Math.Min(bottom, height);
            if (x0 >= x1 || y0 >= y1 || opacity == 0)
                return;

            if (!BlendModes.TryGetValue(modeKey, out var mode))
            {
                Warn("layer \"" + name + "\": unknown blend mode \"" + modeKey + "\" drawn as normal.");
                mode = Blend.Normal;
            }
            // Dissolve keeps each pixel either entirely or not at all, with a probability equal
            // to its coverage. There's no hope of reproducing Photoshop's random pattern.
            bool dissolve = mode == Blend.Dissolve;
            if (dissolve)
                mode = Blend.Normal;

            int srcWidth = right - left;
            for (int y = y0; y < y1; y++)
            {
                int src = (y - top) * srcWidth + (x0 - left);
                int dst = y * width + x0;
                for (int x = x0; x < x1; x++, src++, dst++)
                {
                    int coverage = alpha != null ? alpha[src] : -1;
                    for (int m = 0; m < masks.Length; m++)
                        coverage = MultiplyCoverage(coverage, masks[m].ValueAt(x, y));
                    if (opacity < 255)
                        coverage = MultiplyCoverage(coverage, opacity);
                    if (dissolve)
                        coverage = (coverage < 0 ? 255 : coverage) > DissolveThreshold(x, y) ? 255 : 0;
                    if (coverage == 0)
                        continue;
                    BlendPixel(canvas, dst, colors, src, coverage < 0 ? 255 : coverage, mode, preserveAlpha);
                }
            }
        }

        /// <summary>
        /// A pseudo-random value from 0 to 254 for each pixel, for the dissolve blend mode.
        /// </summary>
        private static int DissolveThreshold(int x, int y)
        {
            uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (int)(h % 255);
        }

        private void BlendPixel(Canvas canvas, int dst, byte[][] colors, int src, int sa, Blend mode, bool preserveAlpha)
        {
            var planes = canvas.Color;
            int ba = canvas.Alpha[dst];
            int nc = channels;

            if (ba == 0)
            {
                // Nothing underneath, so nothing to blend with.
                if (!preserveAlpha)
                {
                    for (int c = 0; c < nc; c++)
                        planes[c][dst] = colors[c][src];
                    canvas.Alpha[dst] = (byte)sa;
                }
                return;
            }

            if (mode == Blend.Normal)
            {
                if (!preserveAlpha && sa == 255)
                {
                    for (int c = 0; c < nc; c++)
                        planes[c][dst] = colors[c][src];
                    canvas.Alpha[dst] = 255;
                    return;
                }
                if (preserveAlpha || ba == 255)
                {
                    // The canvas's transparency stays the same, so this is a simple fade.
                    for (int c = 0; c < nc; c++)
                        planes[c][dst] = (byte)((colors[c][src] * sa + planes[c][dst] * (255 - sa) + 127) / 255);
                    return;
                }
            }

            for (int c = 0; c < nc; c++)
            {
                backdrop[c] = planes[c][dst];
                source[c] = colors[c][src];
            }
            if (mode == Blend.Normal)
            {
                for (int c = 0; c < nc; c++)
                    mixed[c] = source[c];
            }
            else
            {
                for (int c = 0; c < nc; c++)
                {
                    backdrop[c] /= 255;
                    source[c] /= 255;
                }
                BlendColors(mode, nc);
                for (int c = 0; c < nc; c++)
                {
                    mixed[c] *= 255;
                    backdrop[c] = planes[c][dst];
                    source[c] = colors[c][src];
                }
            }

            double fs = sa / 255.0, fb = ba / 255.0;
            if (preserveAlpha)
            {
                for (int c = 0; c < nc; c++)
                    planes[c][dst] = ToByte(fs * ((1 - fb) * source[c] + fb * mixed[c]) + (1 - fs) * backdrop[c]);
            }
            else
            {
                double fo = fs + fb - fs * fb;
                for (int c = 0; c < nc; c++)
                    planes[c][dst] = ToByte((fs * ((1 - fb) * source[c] + fb * mixed[c]) + (1 - fs) * fb * backdrop[c]) / fo);
                canvas.Alpha[dst] = ToByte(fo * 255);
            }
        }

        /// <summary>
        /// Fades the canvas towards the result of drawing a pass-through group, by the group's
        /// opacity and masks.
        /// </summary>
        private void Mix(Canvas canvas, Canvas result, int opacity, MaskPlane[] masks)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0, i = y * width; x < width; x++, i++)
                {
                    int w = -1;
                    for (int m = 0; m < masks.Length; m++)
                        w = MultiplyCoverage(w, masks[m].ValueAt(x, y));
                    if (opacity < 255)
                        w = MultiplyCoverage(w, opacity);

                    if (w < 0 || w == 255)
                    {
                        for (int c = 0; c < channels; c++)
                            canvas.Color[c][i] = result.Color[c][i];
                        canvas.Alpha[i] = result.Alpha[i];
                        continue;
                    }
                    if (w == 0)
                        continue;

                    double t = w / 255.0;
                    double ab = canvas.Alpha[i] / 255.0, ar = result.Alpha[i] / 255.0;
                    double ao = ab + (ar - ab) * t;
                    if (ao <= 0)
                    {
                        canvas.Alpha[i] = 0;
                        continue;
                    }
                    for (int c = 0; c < channels; c++)
                    {
                        double cb = canvas.Color[c][i] * ab, cr = result.Color[c][i] * ar;
                        canvas.Color[c][i] = ToByte((cb + (cr - cb) * t) / ao);
                    }
                    canvas.Alpha[i] = ToByte(ao * 255);
                }
            }
        }

        /// <summary>
        /// Applies the blend function B(backdrop, source) to colors with components from 0 to
        /// 1, putting the result in the mixed array.
        /// </summary>
        private void BlendColors(Blend mode, int nc)
        {
            double[] b = backdrop, s = source;
            switch (mode)
            {
                case Blend.Hue:
                case Blend.Saturation:
                case Blend.Color:
                case Blend.Luminosity:
                case Blend.DarkerColor:
                case Blend.LighterColor:
                    if (nc == 1)
                        BlendGrayNonSeparable(mode);
                    else
                        BlendNonSeparable(mode);
                    return;
            }
            for (int c = 0; c < nc; c++)
                mixed[c] = BlendSeparable(mode, b[c], s[c]);
        }

        private static double BlendSeparable(Blend mode, double b, double s)
        {
            switch (mode)
            {
                case Blend.Darken:
                    return Math.Min(b, s);
                case Blend.Multiply:
                    return b * s;
                case Blend.ColorBurn:
                    return ColorBurn(b, s);
                case Blend.LinearBurn:
                    return Math.Max(0.0, b + s - 1);
                case Blend.Lighten:
                    return Math.Max(b, s);
                case Blend.Screen:
                    return b + s - b * s;
                case Blend.ColorDodge:
                    return ColorDodge(b, s);
                case Blend.LinearDodge:
                    return Math.Min(1.0, b + s);
                case Blend.Overlay:
                    // Hard light, with the two operands exchanged.
                    return HardLight(s, b);
                case Blend.SoftLight:
                    // Photoshop's formula, which differs from the W3C's.
                    return s <= 0.5 ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * (Math.Sqrt(b) - b);
                case Blend.HardLight:
                    return HardLight(b, s);
                case Blend.VividLight:
                    return s <= 0.5 ? ColorBurn(b, 2 * s) : ColorDodge(b, 2 * s - 1);
                case Blend.LinearLight:
                    return Math.Min(1.0, Math.Max(0.0, b + 2 * s - 1));
                case Blend.PinLight:
                    return s <= 0.5 ? Math.Min(b, 2 * s) : Math.Max(b, 2 * s - 1);
                case Blend.HardMix:
                    return b + s >= 1 - 1e-9 ? 1.0 : 0.0;
                case Blend.Difference:
                    return Math.Abs(b - s);
                case Blend.Exclusion:
                    return b + s - 2 * b * s;
                case Blend.Subtract:
                    return Math.Max(0.0, b - s);
                case Blend.Divide:
                    if (s <= 0)
                        return b > 0 ? 1.0 : 0.0;
                    return Math.Min(1.0, b / s);
                default:
                    return s;
            }
        }

        private static double HardLight(double b, double s) => s <= 0.5 ? b * 2 * s : 1 - (1 - b) * (2 - 2 * s);

        private static double ColorDodge(double b, double s)
        {
            if (b <= 0)
                return 0.0;
            return s >= 1 ? 1.0 : Math.Min(1.0, b / (1 - s));
        }

        private static double ColorBurn(double b, double s)
        {
            if (b >= 1)
                return 1.0;
            return s <= 0 ? 0.0 : 1 - Math.Min(1.0, (1 - b) / s);
        }

        /// <summary>
        /// The blend modes that operate on the color as a whole, for grayscale documents.
        /// </summary>
        private void BlendGrayNonSeparable(Blend mode)
        {
            double b = backdrop[0], s = source[0];
            switch (mode)
            {
                case Blend.Luminosity:
                    mixed[0] = s;
                    break;
                case Blend.DarkerColor:
                    mixed[0] = Math.Min(b, s);
                    break;
                case Blend.LighterColor:
                    mixed[0] = Math.Max(b, s);
                    break;
                default:
                    // Hue, saturation and color have nothing to work with.
                    mixed[0] = b;
                    break;
            }
        }

        /// <summary>
        /// The blend modes that operate on the color as a whole, using the W3C's helper
        /// functions. Darker and lighter color pick whichever color has the lower or higher
        /// sum of its components, according to Adobe's description of them.
        /// </summary>
        private void BlendNonSeparable(Blend mode)
        {
            double[] b = backdrop, s = source;
            Span<double> t = stackalloc double[3];
            switch (mode)
            {
                case Blend.Hue:
                    SetSat(s, Sat(b), t);
                    SetLum(t, Lum(b), mixed);
                    break;
                case Blend.Saturation:
                    SetSat(b, Sat(s), t);
                    SetLum(t, Lum(b), mixed);
                    break;
                case Blend.Color:
                    SetLum(s, Lum(b), mixed);
                    break;
                case Blend.Luminosity:
                    SetLum(b, Lum(s), mixed);
                    break;
                case Blend.DarkerColor:
                    (s[0] + s[1] + s[2] < b[0] + b[1] + b[2] ? s : b).CopyTo(mixed, 0);
                    break;
                case Blend.LighterColor:
                    (s[0] + s[1] + s[2] > b[0] + b[1] + b[2] ? s : b).CopyTo(mixed, 0);
                    break;
            }
        }

        private static double Lum(ReadOnlySpan<double> c) => 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2];

        private static double Sat(ReadOnlySpan<double> c) => Math.Max(c[0], Math.Max(c[1], c[2])) - Math.Min(c[0], Math.Min(c[1], c[2]));

        private static void SetLum(ReadOnlySpan<double> c, double lum, Span<double> result)
        {
            double d = lum - Lum(c);
            for (int i = 0; i < 3; i++)
                result[i] = c[i] + d;
            ClipColor(result);
        }

        private static void ClipColor(Span<double> c)
        {
            double lum = Lum(c);
            double lo = Math.Min(c[0], Math.Min(c[1], c[2])), hi = Math.Max(c[0], Math.Max(c[1], c[2]));
            if (lo < 0 && lum != lo)
            {
                for (int i = 0; i < 3; i++)
                    c[i] = lum + (c[i] - lum) * lum / (lum - lo);
            }
            if (hi > 1 && hi != lum)
            {
                for (int i = 0; i < 3; i++)
                    c[i] = lum + (c[i] - lum) * (1 - lum) / (hi - lum);
            }
        }

        private static void SetSat(ReadOnlySpan<double> c, double sat, Span<double> result)
        {
            double lo = Math.Min(c[0], Math.Min(c[1], c[2])), hi = Math.Max(c[0], Math.Max(c[1], c[2]));
            for (int i = 0; i < 3; i++)
                result[i] = hi == lo ? 0.0 : (c[i] - lo) * sat / (hi - lo);
        }
    }
}
