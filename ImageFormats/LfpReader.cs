using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/*

Decoder for pictures from Lytro light field cameras (.LFP, .LFR).

An LFP file is a series of sections. Each starts with an 8-byte signature ("\x89LFP",
"\x89LFM" or "\x89LFC", followed by "\r\n\x1a\n"), four reserved bytes, and the length
of its content as a big-endian 32-bit integer. The first section (LFP) is the file
header, and has no content. Every other section continues with an 80-byte name ("sha1-"
and the hash of its content, padded with zeros), then its content, padded with zeros to
a multiple of 16 bytes. The first metadata section (LFM) is the table of contents: a
JSON document that refers to the content sections (LFC) by name.

A picture straight from the camera holds a raw frame: the image from the sensor, one
sample per pixel in a Bayer mosaic, along with JSON metadata that gives its size,
packing, black and white levels, white balance, color matrix and gamma. We demosaic it
with bilinear interpolation, and apply all of those. Since the sensor sits behind an
array of microlenses, the result looks like the scene seen through a honeycomb. Turning
it into an ordinary photo means resampling the light field, using calibration data for
the particular camera, which we don't attempt.

Samples are packed with no padding. The first-generation camera packs 12-bit samples
big-endian, i.e. most significant bit first. The Illum packs 10-bit samples
little-endian, which means in groups of four: the high eight bits of each, followed by
a byte with the low two bits of each, starting from the least significant bits.

A picture processed by Lytro's desktop software instead holds a "refocus stack": JPEG
images of the scene focused at a series of depths ("lambda" values). Each of them is a
frame, and the default frame is the one focused nearest to the default depth. Without
raw frames or a refocus stack, we fall back to the thumbnails, which are also JPEG.

The table of contents comes in two layouts: an older one ("picture", "frameArray" and
"accelerationArray"), and a newer one ("frames", with "accelerations" in each frame).

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading pictures from Lytro light field cameras.
    /// </summary>
    public static class LfpReader
    {
        private static readonly byte[] Signature = { 0x89, (byte)'L', (byte)'F', (byte)'P', 0x0D, 0x0A, 0x1A, 0x0A };
        private const int SectionHeaderSize = 16;
        private const int SectionNameSize = 80;

        /// <summary>
        /// Reads the default frame of an LFP file: the refocused image at the default depth
        /// if the file has been processed, or otherwise the raw image from the sensor.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads the default frame of an LFP file from a stream: the refocused image at the
        /// default depth if the file has been processed, or otherwise the raw image from the
        /// sensor.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            var lfp = new LfpFile(stream);
            return lfp.Decode(lfp.DefaultFrame);
        }

        /// <summary>
        /// Reads the given frame of an LFP file. Raw frames come first, followed by the
        /// images of the refocus stack, in the order they are stored.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(string)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(string fileName, int frame)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, frame);
        }

        /// <summary>
        /// Reads the given frame of an LFP file from a stream. Raw frames come first, followed
        /// by the images of the refocus stack, in the order they are stored.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            var lfp = new LfpFile(stream);
            if (frame < 0 || frame >= lfp.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + lfp.FrameCount + " frame(s).");
            return lfp.Decode(frame);
        }

        /// <summary>
        /// Gets the number of frames in an LFP file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames in an LFP file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new LfpFile(stream).FrameCount;
        }

        /// <summary>
        /// Reads all the frames of an LFP file. The file stays open while the sequence is
        /// enumerated, and each frame is only read and decoded when it is reached.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            // Check the file now, so that an invalid file fails here rather than when
            // the sequence is first enumerated.
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = new LfpFile(f);
            return EnumerateFrames(fileName);
        }

        /// <summary>
        /// Reads all the frames of an LFP file from a stream. Each frame is only read and
        /// decoded when it is reached, so the stream must stay open while the sequence is
        /// enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return EnumerateFrames(new LfpFile(stream));
        }

        private static IEnumerable<ImageData> EnumerateFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var frame in EnumerateFrames(new LfpFile(f)))
                yield return frame;
        }

        private static IEnumerable<ImageData> EnumerateFrames(LfpFile lfp)
        {
            for (int i = 0; i < lfp.FrameCount; i++)
                yield return lfp.Decode(i);
        }

        /// <summary>
        /// Checks whether the given header is that of an LFP file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            return header.AsSpan().StartsWith(Signature);
        }

        /// <summary>
        /// A frame of an LFP file: a raw image, if it has metadata, or else a JPEG image.
        /// </summary>
        private sealed record Frame(string ImageRef, string? MetadataRef);

        /// <summary>
        /// The sections of an LFP file, the frames listed in its table of contents, and the
        /// stream that holds them.
        /// </summary>
        private sealed class LfpFile
        {
            private readonly Stream stream;
            private readonly Dictionary<string, (long Offset, int Length)> sections = new();
            private readonly List<Frame> frames = new();

            public int FrameCount => frames.Count;
            public int DefaultFrame { get; private set; } = -1;

            public LfpFile(Stream stream)
            {
                if (!stream.CanSeek)
                {
                    var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;
                    stream = copy;
                }
                this.stream = stream;

                var header = new byte[SectionHeaderSize];
                if (stream.ReadAtLeast(header, header.Length, false) < header.Length || !HasSignature(header))
                    throw new ImageDecodeException("This is not a valid LFP file.");

                string? tocName = null;
                var name = new byte[SectionNameSize];
                long pos = stream.Position + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
                while (pos + SectionHeaderSize + SectionNameSize <= stream.Length)
                {
                    stream.Position = pos;
                    stream.ReadExactly(header);
                    stream.ReadExactly(name);
                    if (header[0] != 0x89 || header[1] != 'L' || header[2] != 'F' || !header.AsSpan(4, 4).SequenceEqual(Signature.AsSpan(4)))
                        break;
                    long offset = pos + SectionHeaderSize + SectionNameSize;
                    long length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
                    if (length > int.MaxValue || offset + length > stream.Length)
                        break;
                    string key = Encoding.ASCII.GetString(name).TrimEnd('\0');
                    sections.TryAdd(key, (offset, (int)length));
                    if (header[3] == 'M')
                        tocName ??= key;
                    pos = offset + (length + 15) / 16 * 16;
                }
                if (tocName == null)
                    throw new ImageDecodeException("LFP file has no table of contents.");

                using (var toc = ReadJson(tocName))
                    ReadTableOfContents(toc.RootElement);
                if (frames.Count == 0)
                    throw new ImageDecodeException("LFP file contains no images that can be read.");
                if (DefaultFrame < 0)
                    DefaultFrame = 0;
            }

            private void ReadTableOfContents(JsonElement root)
            {
                var accelerations = new List<JsonElement>(Items(root, "picture", "accelerationArray"));
                foreach (var item in Items(root, "picture", "frameArray").Concat(Items(root, "frames")))
                {
                    if (Str(item, "frame", "imageRef") is string imageRef && Str(item, "frame", "metadataRef") is string metadataRef
                        && sections.ContainsKey(imageRef) && sections.ContainsKey(metadataRef))
                        frames.Add(new Frame(imageRef, metadataRef));
                    accelerations.AddRange(Items(item, "accelerations"));
                }

                foreach (var acceleration in accelerations)
                {
                    if (FindParent(acceleration, "imageArray") is not JsonElement stack)
                        continue;
                    double? defaultLambda = Num(stack, "defaultLambda");
                    int nearestFrame = -1;
                    double nearest = double.MaxValue;
                    foreach (var image in Items(stack, "imageArray"))
                    {
                        if (Str(image, "representation") != "jpeg" || Str(image, "imageRef") is not string imageRef || !sections.ContainsKey(imageRef))
                            continue;
                        double distance = Num(image, "lambda") is double lambda && defaultLambda != null ? Math.Abs(lambda - defaultLambda.Value) : double.MaxValue;
                        if (nearestFrame < 0 || distance < nearest)
                        {
                            nearestFrame = frames.Count;
                            nearest = distance;
                        }
                        frames.Add(new Frame(imageRef, null));
                    }
                    if (DefaultFrame < 0)
                        DefaultFrame = nearestFrame;
                }

                if (frames.Count == 0)
                {
                    foreach (var thumbnail in Items(root, "thumbnailArray").Concat(Items(root, "thumbnails")))
                    {
                        if ((Str(thumbnail, "thumbnailRef") ?? Str(thumbnail, "imageRef")) is string imageRef && sections.ContainsKey(imageRef))
                            frames.Add(new Frame(imageRef, null));
                    }
                }
            }

            public ImageData Decode(int index)
            {
                var frame = frames[index];
                if (frame.MetadataRef != null)
                {
                    using var metadata = ReadJson(frame.MetadataRef);
                    return DecodeRaw(ReadSection(frame.ImageRef), metadata.RootElement);
                }
                return DecodeJpeg(ReadSection(frame.ImageRef));
            }

            private static ImageData DecodeRaw(byte[] packed, JsonElement metadata)
            {
                if (Str(metadata, "image", "representation") is string representation && representation != "rawPacked")
                    throw new ImageDecodeException("Unsupported LFP image representation: " + representation);
                int width = (int)(Num(metadata, "image", "width") ?? 0);
                int height = (int)(Num(metadata, "image", "height") ?? 0);
                if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                    throw new ImageDecodeException("LFP image has invalid dimensions.");

                var details = Child(metadata, "image", "rawDetails") ?? default;
                int bits = (int)(Num(details, "pixelPacking", "bitsPerPixel") ?? 0);
                bool bigEndian = Str(details, "pixelPacking", "endianness") != "little";
                int rightShift = (int)(Num(details, "pixelFormat", "rightShift") ?? 0);
                if (bits < 1 || bits > 16 || (!bigEndian && bits != 8 && bits != 10 && bits != 16))
                    throw new ImageDecodeException("Unsupported LFP pixel packing: " + bits + " bits, " + (bigEndian ? "big" : "little") + "-endian.");
                int pixels = width * height;
                long packedSize = !bigEndian && bits == 10 ? (pixels + 3L) / 4 * 5 : ((long)pixels * bits + 7) / 8;
                if (packed.Length < packedSize)
                    throw new ImageDecodeException("LFP image data is truncated.");

                // The color filter pattern is given as the names of the filters of a tile, a
                // row at a time ("r,gr:gb,b"), along with the name of the one at the top left
                // of the image.
                var mosaic = Child(details, "mosaic") ?? Child(metadata, "devices", "sensor", "mosaic") ?? default;
                var tile = (Str(mosaic, "tile") ?? "").Split(':');
                string upperLeft = Str(mosaic, "upperLeftPixel") ?? "";
                int patternHeight = tile.Length, patternWidth = tile[0].Split(',').Length;
                var names = new string[patternWidth * patternHeight];
                for (int y = 0; y < patternHeight; y++)
                {
                    var row = tile[y].Split(',');
                    if (row.Length != patternWidth)
                        throw new ImageDecodeException("Invalid LFP color filter pattern.");
                    row.CopyTo(names, y * patternWidth);
                }
                int start = Array.IndexOf(names, upperLeft);
                if (start < 0)
                    throw new ImageDecodeException("Invalid LFP color filter pattern.");

                var color = Child(metadata, "image", "color") ?? default;
                var cfa = new int[names.Length];
                var black = new float[names.Length];
                var scale = new float[names.Length];
                for (int y = 0; y < patternHeight; y++)
                {
                    for (int x = 0; x < patternWidth; x++)
                    {
                        string filter = names[(start / patternWidth + y) % patternHeight * patternWidth + (start % patternWidth + x) % patternWidth];
                        int i = y * patternWidth + x;
                        cfa[i] = filter switch
                        {
                            "r" => 0,
                            "g" or "gr" or "gb" => 1,
                            "b" => 2,
                            _ => throw new ImageDecodeException("Unsupported LFP color filter: " + filter)
                        };
                        black[i] = (float)(Num(details, "pixelFormat", "black", filter) ?? 0);
                        float white = (float)(Num(details, "pixelFormat", "white", filter) ?? (1 << bits) - 1);
                        if (white <= black[i])
                            throw new ImageDecodeException("Invalid LFP black and white levels.");
                        scale[i] = (float)(Num(color, "whiteBalanceGain", filter) ?? 1) * 65535f / (white - black[i]);
                    }
                }

                // Unpack the samples, and scale them to 0-65535, white balanced.
                var samples = new int[pixels];
                Unpack(packed, samples, bits, bigEndian);
                for (int y = 0, i = 0; y < height; y++)
                {
                    int patternRow = y % patternHeight * patternWidth;
                    for (int x = 0; x < width; x++, i++)
                    {
                        int k = patternRow + x % patternWidth;
                        float v = ((samples[i] >> rightShift) - black[k]) * scale[k];
                        samples[i] = v <= 0 ? 0 : v >= 65535 ? 65535 : (int)(v + 0.5f);
                    }
                }

                var ccm = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                var ccmItems = new List<JsonElement>(Items(color, "ccmRgbToSrgbArray"));
                if (ccmItems.Count == 9)
                {
                    for (int i = 0; i < 9; i++)
                        ccm[i] = ccmItems[i].ValueKind == JsonValueKind.Number ? (float)ccmItems[i].GetDouble() : ccm[i];
                }
                double gamma = Num(color, "gamma") ?? 1 / 2.2;
                var toOutput = new byte[65536];
                for (int i = 0; i < toOutput.Length; i++)
                    toOutput[i] = (byte)(Math.Pow(i / 65535.0, gamma) * 255 + 0.5);

                var data = new byte[pixels * 4];
                var rgb = new int[width * 3];
                for (int y = 0; y < height; y++)
                {
                    Demosaic.BilinearRow(samples, width, height, cfa, y, rgb, patternWidth);
                    int o = y * width * 4;
                    for (int x = 0; x < width; x++, o += 4)
                    {
                        float r = rgb[x * 3], g = rgb[x * 3 + 1], b = rgb[x * 3 + 2];
                        data[o] = toOutput[Clamp(ccm[6] * r + ccm[7] * g + ccm[8] * b)];
                        data[o + 1] = toOutput[Clamp(ccm[3] * r + ccm[4] * g + ccm[5] * b)];
                        data[o + 2] = toOutput[Clamp(ccm[0] * r + ccm[1] * g + ccm[2] * b)];
                        data[o + 3] = 0xFF;
                    }
                }
                return new ImageData(width, height, data);
            }

            private static int Clamp(float v)
            {
                return v <= 0 ? 0 : v >= 65535 ? 65535 : (int)(v + 0.5f);
            }

            private static void Unpack(byte[] packed, int[] samples, int bits, bool bigEndian)
            {
                if (bigEndian || bits == 8)
                {
                    int mask = (1 << bits) - 1;
                    ulong buffer = 0;
                    int buffered = 0, pos = 0;
                    for (int i = 0; i < samples.Length; i++)
                    {
                        while (buffered < bits)
                        {
                            buffer = (buffer << 8) | packed[pos++];
                            buffered += 8;
                        }
                        buffered -= bits;
                        samples[i] = (int)(buffer >> buffered) & mask;
                    }
                }
                else if (bits == 16)
                {
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = packed[i * 2] | (packed[i * 2 + 1] << 8);
                }
                else
                {
                    for (int i = 0, pos = 0; i < samples.Length; i += 4, pos += 5)
                    {
                        int low = packed[pos + 4];
                        for (int j = 0; j < 4 && i + j < samples.Length; j++)
                            samples[i + j] = (packed[pos + j] << 2) | ((low >> (j * 2)) & 3);
                    }
                }
            }

            private static ImageData DecodeJpeg(byte[] data)
            {
                if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
                    throw new ImageDecodeException("Unsupported LFP image format.");
                CodecImage image;
                try
                {
                    image = JpegDecoder.Decode(data);
                }
                catch (ImageDecodeException)
                {
                    // A flavor of JPEG we can't decode; let the caller do it.
                    return ImageData.FromEncoded(0, 0, data, "jpeg")!;
                }
                if (image.Precision != 8 || (image.Components != 1 && image.Components != 3))
                    return ImageData.FromEncoded(image.Width, image.Height, data, "jpeg")!;

                // Samples are either gray, or red, green and blue.
                var s = image.Samples;
                int n = image.Components;
                var bmp = new byte[image.Width * image.Height * 4];
                for (int i = 0, p = 0; p < bmp.Length; i += n, p += 4)
                {
                    bmp[p] = (byte)s[i + n - 1];
                    bmp[p + 1] = (byte)s[i + n / 2];
                    bmp[p + 2] = (byte)s[i];
                    bmp[p + 3] = 0xFF;
                }
                return new ImageData(image.Width, image.Height, bmp);
            }

            private byte[] ReadSection(string name)
            {
                var (offset, length) = sections[name];
                var data = new byte[length];
                stream.Position = offset;
                stream.ReadExactly(data);
                return data;
            }

            private JsonDocument ReadJson(string name)
            {
                try
                {
                    return JsonDocument.Parse(ReadSection(name).AsMemory().TrimEnd((byte)0));
                }
                catch (JsonException e)
                {
                    throw new ImageDecodeException("Invalid LFP metadata: " + e.Message);
                }
            }
        }

        private static JsonElement? Child(JsonElement element, params string[] path)
        {
            foreach (var key in path)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var child))
                    return null;
                element = child;
            }
            return element;
        }

        private static string? Str(JsonElement element, params string[] path)
        {
            return Child(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        }

        private static double? Num(JsonElement element, params string[] path)
        {
            return Child(element, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
        }

        private static IEnumerable<JsonElement> Items(JsonElement element, params string[] path)
        {
            return Child(element, path) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : Array.Empty<JsonElement>();
        }

        /// <summary>
        /// Finds the first object at or below the given element that has the given property.
        /// </summary>
        private static JsonElement? FindParent(JsonElement element, string key)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(key, out _))
                    return element;
                foreach (var property in element.EnumerateObject())
                {
                    if (FindParent(property.Value, key) is JsonElement found)
                        return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (FindParent(item, key) is JsonElement found)
                        return found;
                }
            }
            return null;
        }
    }
}
