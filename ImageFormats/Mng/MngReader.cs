using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/*

Decoder for MNG (Multiple-image Network Graphics) animations, and for JNG (JPEG Network
Graphics) images.

An MNG datastream is a series of chunks, laid out as in PNG, that is less a picture than
a program for drawing a series of frames. It embeds images (complete PNG datastreams,
JNG datastreams which wrap a JPEG image with an optional alpha channel, BASI datastreams
which describe an image of a single color, and Delta-PNG datastreams which modify an
earlier image), stores them as numbered objects, and then places, moves, clips,
magnifies, pastes together and shows them on a canvas, in loops if need be. Each image
that is shown is drawn as a "layer" over the ones before, and the FRAM chunk decides
when the canvas is cleared to the background, and which layers make up a frame.

We run the program from start to end, and return each frame as it would be displayed.
Where the specification leaves details to the viewer, we follow libmng, the reference
implementation:

- A frame is complete, and is displayed, whenever the viewer would wait for an interframe
  delay that isn't zero. The background is drawn at the start of every subframe in
  framing mode 4, and also at every FRAM chunk that comes before the first layer.
- Each loop is run the number of times that it gives, except that loops which may
  end at the decoder's discretion run the minimum number of times, and infinite loops
  run once. The TERM chunk, which says how to repeat the whole animation, is ignored.

Unlike libmng, we use the background color and image of the BACK chunk even when
they're only advisory. Without a BACK chunk, the background is transparent. Colors are gamma corrected for a
display gamma of 2.2, using the gAMA or sRGB chunk of each image or of the MNG datastream.
Other color management chunks (cHRM and iCCP) are ignored.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading MNG animations and JNG images.
    /// </summary>
    public static class MngReader
    {
        private static readonly byte[] MngSignature = { 0x8A, (byte)'M', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] JngSignature = { 0x8B, (byte)'J', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

        // Limits on the work done for a single file, so that an endless or enormous loop
        // can't hang the caller.
        private const int MaxFrames = 10000;
        private const int MaxChunksProcessed = 1000000;

        /// <summary>
        /// Reads the first frame of an MNG or JNG file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads the first frame of an MNG or JNG file from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given frame of an MNG file. Since each frame is drawn on top of the ones
        /// before, all the frames before it are rendered too.
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
        /// Reads the given frame of an MNG file from a stream. Since each frame is drawn on top
        /// of the ones before, all the frames before it are rendered too.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            if (frame < 0)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range.");
            int count = 0;
            foreach (var image in new MngRenderer(stream).Render())
            {
                if (count++ == frame)
                    return image;
            }
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + count + " frame(s).");
        }

        /// <summary>
        /// Gets the number of frames in an MNG file. This renders the whole animation.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames in an MNG file. This renders the whole animation.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new MngRenderer(stream).Render().Count();
        }

        /// <summary>
        /// Reads all the frames of an MNG file. The file is read when this is called, and each
        /// frame is rendered when it is reached.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadFrames(f);
        }

        /// <summary>
        /// Reads all the frames of an MNG file from a stream. The stream is read when this is
        /// called, and each frame is rendered when it is reached.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return new MngRenderer(stream).Render();
        }

        /// <summary>
        /// Checks whether the given header is that of an MNG or JNG file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            return header.AsSpan().StartsWith(MngSignature) || header.AsSpan().StartsWith(JngSignature);
        }

        private readonly record struct Chunk(string Type, int Offset, int Length);

        /// <summary>
        /// The set of object attributes of an MNG object, and the object buffer that it points to.
        /// </summary>
        private sealed class MngObject
        {
            public MngImage? Buffer;
            public bool Visible = true;
            public bool Concrete;
            public bool Frozen;
            public int X, Y;
            public int ClipLeft, ClipRight, ClipTop, ClipBottom;

            public bool IsViewable => Buffer != null && Buffer.Viewable;

            public MngObject Copy()
            {
                return (MngObject)MemberwiseClone();
            }
        }

        private sealed class Magnification
        {
            public int MethodX, MethodY, MX, MY, ML, MR, MT, MB;
        }

        private sealed class Loop
        {
            public int NestLevel;
            public int Start;
            public long Remaining;
        }

        /// <summary>
        /// Runs an MNG datastream, producing its frames.
        /// </summary>
        private sealed class MngRenderer
        {
            private readonly byte[] data;
            private readonly List<Chunk> chunks = new();
            private readonly bool isJng;

            private int frameWidth, frameHeight;
            private byte[] canvas = Array.Empty<byte>();
            private readonly List<ImageData> output = new();
            private bool dirty;
            private int framesEmitted;

            private readonly Dictionary<int, MngObject> objects = new();
            private MngObject object0 = new();
            private int currentId;
            private Magnification? magnification0;

            private byte[]? globalPalette;
            private byte[]? globalPaletteAlpha;
            private double? globalGamma;

            private bool hasBack;
            private readonly byte[] backColor = new byte[4];
            private int backImageId;
            private bool backTile;

            // Framing state, named after its counterparts in libmng.
            private int framingMode = 1, defaultFramingMode = 1;
            private long frameDelay = 1, nextDelay = 1, defaultDelay = 1;
            private bool frameClipping, defaultFrameClipping;
            private int clipLeft, clipRight, clipTop, clipBottom;
            private int defaultClipLeft, defaultClipRight, defaultClipTop, defaultClipBottom;
            private int layerCount, frameCount;
            private bool frameDone;

            private readonly Dictionary<int, (int X, int Y)> pastTargets = new();
            private (int X, int Y) lastPastTarget;

            public MngRenderer(Stream stream)
            {
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    data = ms.ToArray();
                }
                if (data.Length < 8 || !HasSignature(data))
                    throw new ImageDecodeException("This is not a valid MNG or JNG file.");
                isJng = data[0] == JngSignature[0];

                int pos = 8;
                while (pos + 12 <= data.Length)
                {
                    long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                    if (pos + 12 + length > data.Length)
                        break;
                    string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    chunks.Add(new Chunk(type, pos + 8, (int)length));
                    pos += 12 + (int)length;
                    if (type == "MEND")
                        break;
                }

                var header = chunks.Count > 0 ? chunks[0] : default;
                if (isJng && header.Type == "JHDR" && header.Length >= 8)
                {
                    frameWidth = I32(header.Offset);
                    frameHeight = I32(header.Offset + 4);
                }
                else if (!isJng && header.Type == "MHDR" && header.Length >= 8)
                {
                    frameWidth = I32(header.Offset);
                    frameHeight = I32(header.Offset + 4);
                }
                else
                {
                    throw new ImageDecodeException("This is not a valid MNG or JNG file.");
                }
                if (frameWidth <= 0 || frameHeight <= 0 || (long)frameWidth * frameHeight * 4 > int.MaxValue)
                    throw new ImageDecodeException("MNG file has invalid frame dimensions.");
            }

            public IEnumerable<ImageData> Render()
            {
                canvas = new byte[frameWidth * frameHeight * 4];
                object0 = NewObject();
                var loops = new List<Loop>();
                int processed = 0;
                int i = isJng ? 0 : 1;
                while (i < chunks.Count && framesEmitted < MaxFrames && processed++ < MaxChunksProcessed)
                {
                    var chunk = chunks[i];
                    int next = i + 1;
                    switch (chunk.Type)
                    {
                        case "MEND":
                            next = chunks.Count;
                            break;
                        case "IHDR":
                        case "JHDR":
                        case "BASI":
                        case "DHDR":
                            next = EmbeddedEnd(i) + 1;
                            ProcessEmbedded(i, next - 1);
                            break;
                        case "LOOP":
                            next = ProcessLoop(chunk, i, loops);
                            break;
                        case "ENDL":
                            next = ProcessEndLoop(chunk, i, loops);
                            break;
                        case "DEFI": ProcessDefine(chunk); break;
                        case "PLTE": globalPalette = chunk.Length > 0 ? Bytes(chunk) : null; break;
                        case "tRNS": globalPaletteAlpha = chunk.Length > 0 ? Bytes(chunk) : null; break;
                        case "gAMA": globalGamma = ReadGamma(chunk); break;
                        case "sRGB": globalGamma = chunk.Length > 0 ? 0.45455 : null; break;
                        case "BACK": ProcessBackground(chunk); break;
                        case "FRAM": ProcessFrame(chunk); break;
                        case "MOVE": ProcessMove(chunk); break;
                        case "CLIP": ProcessClip(chunk); break;
                        case "SHOW": ProcessShow(chunk); break;
                        case "CLON": ProcessClone(chunk); break;
                        case "DISC": ProcessDiscard(chunk); break;
                        case "MAGN": ProcessMagnify(chunk); break;
                        case "PAST": ProcessPaste(chunk); break;
                        case "SAVE":
                            foreach (var obj in objects.Values)
                                obj.Frozen = true;
                            break;
                        case "SEEK":
                            Discard(null);
                            break;
                    }
                    i = next;

                    foreach (var frame in output)
                        yield return frame;
                    output.Clear();
                }

                // Display whatever hasn't been displayed yet, and at least one frame.
                if (dirty || framesEmitted == 0)
                    yield return Snapshot();
            }

            private MngObject NewObject()
            {
                return new MngObject { ClipRight = frameWidth, ClipBottom = frameHeight };
            }

            #region Reading chunk data

            private int U8(int offset) => data[offset];
            private int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            private int I32(int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            private long U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            private byte[] Bytes(Chunk chunk) => data.AsSpan(chunk.Offset, chunk.Length).ToArray();

            private double? ReadGamma(Chunk chunk)
            {
                return chunk.Length >= 4 && U32(chunk.Offset) > 0 ? U32(chunk.Offset) / 100000.0 : null;
            }

            /// <summary>
            /// Finds the IEND chunk that ends the embedded datastream starting at the given chunk.
            /// </summary>
            private int EmbeddedEnd(int start)
            {
                for (int i = start + 1; i < chunks.Count; i++)
                {
                    if (chunks[i].Type == "IEND")
                        return i;
                    if (chunks[i].Type == "MEND")
                        return i - 1;
                }
                return chunks.Count - 1;
            }

            #endregion

            #region Frames and layers

            private ImageData Snapshot()
            {
                framesEmitted++;
                dirty = false;
                return new ImageData(frameWidth, frameHeight, (byte[])canvas.Clone());
            }

            /// <summary>
            /// Called where the viewer would wait for the interframe delay: the frame so far is
            /// displayed, unless the delay is zero.
            /// </summary>
            private void InterframeDelay()
            {
                if (frameDelay > 0 && dirty)
                    output.Add(Snapshot());
                frameDelay = nextDelay;
            }

            /// <summary>
            /// Starts a new layer, drawing the background first if the framing mode calls for it.
            /// </summary>
            private void NextLayer()
            {
                if (layerCount > 0 && (framingMode == 1 || framingMode == 3))
                    InterframeDelay();
                else
                    frameDelay = nextDelay;

                if (layerCount == 0)
                {
                    DrawBackground();
                    layerCount++;
                }
                else if (framingMode == 3)
                {
                    DrawBackground();
                }
                layerCount++;
            }

            private void ProcessFrame(Chunk chunk)
            {
                int mode = 0, changeDelay = 0, changeClipping = 0, clipType = 0;
                long delay = 0;
                int l = 0, r = 0, t = 0, b = 0;
                if (chunk.Length > 0)
                {
                    mode = U8(chunk.Offset);
                    int end = chunk.Offset + chunk.Length;
                    int p = Array.IndexOf(data, (byte)0, chunk.Offset + 1, chunk.Length - 1);
                    if (p >= 0 && p + 4 < end)
                    {
                        changeDelay = U8(p + 1);
                        int changeTimeout = U8(p + 2);
                        changeClipping = U8(p + 3);
                        p += 5;
                        if (changeDelay != 0 && p + 4 <= end)
                        {
                            delay = U32(p);
                            p += 4;
                        }
                        if (changeTimeout != 0)
                            p += 4;
                        if (changeClipping != 0 && p + 17 <= end)
                        {
                            clipType = U8(p);
                            l = I32(p + 1);
                            r = I32(p + 5);
                            t = I32(p + 9);
                            b = I32(p + 13);
                        }
                        else
                        {
                            changeClipping = 0;
                        }
                    }
                }

                int oldMode = framingMode;
                if (oldMode == 2 || oldMode == 4)
                {
                    if (frameCount > 0 && mode != 1 && mode != 3)
                        InterframeDelay();
                    else
                        frameDelay = nextDelay;
                }
                else if (frameDone && mode == 4)
                {
                    InterframeDelay();
                }

                if (mode != 0)
                    framingMode = defaultFramingMode = mode;
                else
                    framingMode = defaultFramingMode;

                if (changeDelay != 0)
                {
                    nextDelay = delay;
                    if (oldMode == 2 || oldMode == 4)
                        frameDelay = defaultDelay;
                    if (changeDelay == 2)
                        defaultDelay = delay;
                }
                else
                {
                    nextDelay = defaultDelay;
                }

                if (changeClipping != 0)
                {
                    frameClipping = true;
                    (clipLeft, clipRight, clipTop, clipBottom) = clipType == 0
                        ? (l, r, t, b) : (clipLeft + l, clipRight + r, clipTop + t, clipBottom + b);
                    if (changeClipping == 2)
                    {
                        defaultFrameClipping = true;
                        (defaultClipLeft, defaultClipRight, defaultClipTop, defaultClipBottom) = clipType == 0
                            ? (l, r, t, b) : (defaultClipLeft + l, defaultClipRight + r, defaultClipTop + t, defaultClipBottom + b);
                    }
                }
                else
                {
                    frameClipping = defaultFrameClipping;
                    (clipLeft, clipRight, clipTop, clipBottom) = (defaultClipLeft, defaultClipRight, defaultClipTop, defaultClipBottom);
                }

                if (framingMode == 4 || layerCount == 0)
                    DrawBackground();
                frameCount++;
                frameDone = true;
            }

            private void ProcessBackground(Chunk chunk)
            {
                if (chunk.Length < 6)
                    return;
                hasBack = true;
                var lut = MngImage.GammaTable(65535, globalGamma);
                backColor[0] = lut[U16(chunk.Offset + 4)];
                backColor[1] = lut[U16(chunk.Offset + 2)];
                backColor[2] = lut[U16(chunk.Offset)];
                backColor[3] = 0xFF;
                backImageId = chunk.Length >= 9 ? U16(chunk.Offset + 7) : 0;
                backTile = chunk.Length >= 10 && U8(chunk.Offset + 9) == 1;
            }

            /// <summary>
            /// Draws the background layer: the background color, and then the background image,
            /// within the layer clipping boundaries.
            /// </summary>
            private void DrawBackground()
            {
                int left = 0, top = 0, right = frameWidth, bottom = frameHeight;
                if (frameClipping)
                {
                    left = Math.Max(left, clipLeft);
                    top = Math.Max(top, clipTop);
                    right = Math.Min(right, clipRight);
                    bottom = Math.Min(bottom, clipBottom);
                }
                if (left >= right || top >= bottom)
                    return;
                dirty = true;
                for (int y = top; y < bottom; y++)
                {
                    for (int x = left; x < right; x++)
                        backColor.CopyTo(canvas, (y * frameWidth + x) * 4);
                }

                if (!hasBack || backImageId == 0 || !objects.TryGetValue(backImageId, out var obj) || !obj.IsViewable || !obj.Visible)
                    return;
                var image = obj.Buffer!;
                left = Math.Max(Math.Max(left, obj.ClipLeft), obj.X);
                top = Math.Max(Math.Max(top, obj.ClipTop), obj.Y);
                right = Math.Min(right, obj.ClipRight);
                bottom = Math.Min(bottom, obj.ClipBottom);
                if (!backTile)
                {
                    right = Math.Min(right, obj.X + image.Width);
                    bottom = Math.Min(bottom, obj.Y + image.Height);
                }
                var pixels = image.ToBgra();
                for (int y = top; y < bottom; y++)
                {
                    int sy = (y - obj.Y) % image.Height;
                    for (int x = left; x < right; x++)
                        Blend(canvas, (y * frameWidth + x) * 4, pixels, (sy * image.Width + (x - obj.X) % image.Width) * 4);
                }
            }

            /// <summary>
            /// Displays an image as a new layer, at the location of the given object, clipped to
            /// its clipping boundaries and those of the layer.
            /// </summary>
            private void Display(MngObject obj, MngImage image)
            {
                NextLayer();
                int left = Math.Max(Math.Max(0, obj.X), obj.ClipLeft);
                int top = Math.Max(Math.Max(0, obj.Y), obj.ClipTop);
                int right = Math.Min(Math.Min(frameWidth, obj.X + image.Width), obj.ClipRight);
                int bottom = Math.Min(Math.Min(frameHeight, obj.Y + image.Height), obj.ClipBottom);
                if (frameClipping)
                {
                    left = Math.Max(left, clipLeft);
                    top = Math.Max(top, clipTop);
                    right = Math.Min(right, clipRight);
                    bottom = Math.Min(bottom, clipBottom);
                }
                if (left >= right || top >= bottom)
                    return;
                dirty = true;
                var pixels = image.ToBgra();
                for (int y = top; y < bottom; y++)
                {
                    for (int x = left; x < right; x++)
                        Blend(canvas, (y * frameWidth + x) * 4, pixels, ((y - obj.Y) * image.Width + x - obj.X) * 4);
                }
            }

            /// <summary>
            /// Composites a pixel over another, both BGRA with straight alpha.
            /// </summary>
            private static void Blend(byte[] dst, int d, byte[] src, int s)
            {
                int sa = src[s + 3];
                if (sa == 0)
                    return;
                int da = dst[d + 3];
                if (sa == 255 || da == 0)
                {
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = (byte)sa;
                    return;
                }
                // Each weight is out of 255 * 255.
                int ws = sa * 255, wd = da * (255 - sa), total = ws + wd;
                for (int c = 0; c < 3; c++)
                    dst[d + c] = (byte)((src[s + c] * ws + dst[d + c] * wd + total / 2) / total);
                dst[d + 3] = (byte)((total + 127) / 255);
            }

            #endregion

            #region Loops

            private int ProcessLoop(Chunk chunk, int index, List<Loop> loops)
            {
                if (chunk.Length < 5)
                    return index + 1;
                int nestLevel = U8(chunk.Offset);
                long count = U32(chunk.Offset + 1);
                int termination = chunk.Length >= 6 ? U8(chunk.Offset + 5) : 0;
                if (termination != 0 && termination != 4)
                    count = Math.Min(count, chunk.Length >= 10 ? U32(chunk.Offset + 6) : 1);
                if (count >= 0x7FFFFFFF)
                    count = 1;

                if (count == 0)
                {
                    // Skip to the matching ENDL.
                    for (int i = index + 1; i < chunks.Count; i++)
                    {
                        if (chunks[i].Type == "ENDL" && chunks[i].Length >= 1 && U8(chunks[i].Offset) == nestLevel)
                            return i + 1;
                    }
                    return chunks.Count;
                }
                loops.Add(new Loop { NestLevel = nestLevel, Start = index + 1, Remaining = count });
                return index + 1;
            }

            private int ProcessEndLoop(Chunk chunk, int index, List<Loop> loops)
            {
                if (chunk.Length < 1)
                    return index + 1;
                int nestLevel = U8(chunk.Offset);
                while (loops.Count > 0 && loops[^1].NestLevel > nestLevel)
                    loops.RemoveAt(loops.Count - 1);
                if (loops.Count == 0 || loops[^1].NestLevel != nestLevel)
                    return index + 1;
                var loop = loops[^1];
                if (--loop.Remaining > 0)
                    return loop.Start;
                loops.RemoveAt(loops.Count - 1);
                return index + 1;
            }

            #endregion

            #region Objects

            private MngObject? Find(int id)
            {
                return id == 0 ? object0 : objects.TryGetValue(id, out var obj) ? obj : null;
            }

            /// <summary>
            /// Gets the existing objects with IDs in the given range, in order from first to last
            /// (descending if last is less than first).
            /// </summary>
            private List<(int Id, MngObject Obj)> Range(int first, int last, bool includeZero = false)
            {
                int low = Math.Min(first, last), high = Math.Max(first, last);
                var result = objects.Where(o => o.Key >= low && o.Key <= high).Select(o => (o.Key, o.Value)).ToList();
                if (includeZero && low == 0)
                    result.Add((0, object0));
                result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                if (last < first)
                    result.Reverse();
                return result;
            }

            private void ProcessDefine(Chunk chunk)
            {
                if (chunk.Length < 2)
                    return;
                int id = U16(chunk.Offset);
                var existing = id == 0 ? null : Find(id);
                var obj = existing ?? NewObject();
                if (id != 0 && existing == null)
                    objects[id] = obj;
                if (id == 0)
                    object0 = obj;

                int p = chunk.Offset;
                obj.Visible = chunk.Length >= 3 ? U8(p + 2) == 0 : existing?.Visible ?? true;
                obj.Concrete = chunk.Length >= 4 ? U8(p + 3) == 1 : existing?.Concrete ?? false;
                if (chunk.Length >= 12)
                {
                    obj.X = I32(p + 4);
                    obj.Y = I32(p + 8);
                }
                if (chunk.Length >= 28)
                {
                    obj.ClipLeft = I32(p + 12);
                    obj.ClipRight = I32(p + 16);
                    obj.ClipTop = I32(p + 20);
                    obj.ClipBottom = I32(p + 24);
                }
                currentId = id;
            }

            private void ProcessMove(Chunk chunk)
            {
                if (chunk.Length < 13)
                    return;
                int p = chunk.Offset;
                bool relative = U8(p + 4) == 1;
                int x = I32(p + 5), y = I32(p + 9);
                foreach (var (_, obj) in Range(U16(p), U16(p + 2), true))
                {
                    obj.X = relative ? obj.X + x : x;
                    obj.Y = relative ? obj.Y + y : y;
                }
            }

            private void ProcessClip(Chunk chunk)
            {
                if (chunk.Length < 21)
                    return;
                int p = chunk.Offset;
                bool relative = U8(p + 4) == 1;
                int l = I32(p + 5), r = I32(p + 9), t = I32(p + 13), b = I32(p + 17);
                foreach (var (_, obj) in Range(U16(p), U16(p + 2), true))
                {
                    if (relative)
                        (obj.ClipLeft, obj.ClipRight, obj.ClipTop, obj.ClipBottom) = (obj.ClipLeft + l, obj.ClipRight + r, obj.ClipTop + t, obj.ClipBottom + b);
                    else
                        (obj.ClipLeft, obj.ClipRight, obj.ClipTop, obj.ClipBottom) = (l, r, t, b);
                }
            }

            private void ProcessShow(Chunk chunk)
            {
                int first = 1, last = 65535, mode = 2;
                if (chunk.Length >= 2)
                {
                    first = U16(chunk.Offset);
                    last = chunk.Length >= 4 ? U16(chunk.Offset + 2) : first;
                    mode = chunk.Length >= 5 ? U8(chunk.Offset + 4) : 0;
                }
                var range = Range(first, last);

                if (mode == 6 || mode == 7)
                {
                    // Make the next viewable object after the first visible one visible, and the
                    // rest invisible.
                    var viewable = range.Where(o => o.Obj.IsViewable).ToList();
                    int current = viewable.FindIndex(o => o.Obj.Visible);
                    int next = current >= 0 && current + 1 < viewable.Count ? current + 1 : 0;
                    foreach (var (_, obj) in range)
                        obj.Visible = false;
                    if (viewable.Count == 0)
                    {
                        if (mode == 6)
                            NextLayer();
                        return;
                    }
                    var shown = viewable[next].Obj;
                    shown.Visible = true;
                    if (mode == 6)
                        Display(shown, shown.Buffer!);
                    return;
                }

                foreach (var (_, obj) in range)
                {
                    if (mode == 0 || mode == 3)
                        obj.Visible = true;
                    else if (mode == 1)
                        obj.Visible = false;
                    else if (mode == 4 || mode == 5)
                        obj.Visible = !obj.Visible;
                    if ((mode == 0 || mode == 2 || mode == 4) && obj.Visible && obj.IsViewable)
                        Display(obj, obj.Buffer!);
                }
            }

            private void ProcessClone(Chunk chunk)
            {
                if (chunk.Length < 4)
                    return;
                int p = chunk.Offset;
                int sourceId = U16(p), cloneId = U16(p + 2);
                if (sourceId == 0 || cloneId == 0 || !objects.TryGetValue(sourceId, out var source))
                    return;
                int type = chunk.Length >= 5 ? U8(p + 4) : 0;
                var clone = source.Copy();
                clone.Frozen = false;
                if (type == 0)
                    clone.Buffer = source.Buffer?.Clone();
                if (chunk.Length >= 6)
                    clone.Visible = U8(p + 5) == 0;
                if (chunk.Length >= 7 && U8(p + 6) == 1)
                    clone.Concrete = false;
                if (chunk.Length >= 16)
                {
                    bool relative = U8(p + 7) == 1;
                    int x = I32(p + 8), y = I32(p + 12);
                    clone.X = relative ? source.X + x : x;
                    clone.Y = relative ? source.Y + y : y;
                }
                objects[cloneId] = clone;
                if (type == 2 && !source.Frozen)
                    objects.Remove(sourceId);
                if (clone.Visible && clone.IsViewable)
                    Display(clone, clone.Buffer!);
            }

            private void ProcessDiscard(Chunk chunk)
            {
                if (chunk.Length == 0)
                {
                    Discard(null);
                    return;
                }
                var ids = new List<int>();
                for (int p = 0; p + 2 <= chunk.Length; p += 2)
                    ids.Add(U16(chunk.Offset + p));
                Discard(ids);
            }

            /// <summary>
            /// Discards the given objects, or all of them if null, except for frozen ones.
            /// </summary>
            private void Discard(List<int>? ids)
            {
                foreach (var id in ids ?? objects.Keys.ToList())
                {
                    if (objects.TryGetValue(id, out var obj) && !obj.Frozen)
                    {
                        objects.Remove(id);
                        if (id == currentId)
                            currentId = 0;
                    }
                }
                if (ids == null)
                    currentId = 0;
            }

            private void ProcessMagnify(Chunk chunk)
            {
                if (chunk.Length < 2)
                {
                    magnification0 = null;
                    return;
                }
                int p = chunk.Offset, n = chunk.Length;
                int first = U16(p);
                int last = n >= 4 ? U16(p + 2) : first;
                var m = new Magnification();

                // Early versions of libmng wrote the methods as 16-bit fields, which makes for
                // lengths that are otherwise impossible. A length of 18 is ambiguous, so we do
                // as libmng does, and guess from the values.
                bool wide = n is 6 or 8 or 10 or 12 or 14 or 16 or 20
                    || (n == 18 && U16(p + 4) <= 5 && U16(p + 6) < 256 && U16(p + 8) < 256 && U16(p + 10) < 256
                        && U16(p + 12) < 256 && U16(p + 14) < 256 && U16(p + 16) < 256);
                int w = wide ? 1 : 0;
                m.MethodX = n >= 5 + w ? (wide ? U16(p + 4) & 0xFF : U8(p + 4)) : 0;
                m.MX = n >= 7 + w ? U16(p + 5 + w) : 1;
                m.MY = n >= 9 + w ? U16(p + 7 + w) : m.MX;
                m.ML = n >= 11 + w ? U16(p + 9 + w) : m.MX;
                m.MR = n >= 13 + w ? U16(p + 11 + w) : m.MX;
                m.MT = n >= 15 + w ? U16(p + 13 + w) : m.MY;
                m.MB = n >= 17 + w ? U16(p + 15 + w) : m.MY;
                m.MethodY = n >= 18 + 2 * w ? (wide ? U16(p + 18) & 0xFF : U8(p + 17)) : m.MethodX;
                if (m.MethodX > 5 || m.MethodY > 5)
                    return;

                var range = Range(first, last).Where(o => !o.Obj.Frozen).ToList();
                if (Math.Min(first, last) == 0)
                    magnification0 = m.MethodX == 0 && m.MethodY == 0 ? null : m;
                foreach (var (_, obj) in range)
                    Magnify(obj.Buffer, m);
                foreach (var (_, obj) in range)
                {
                    if (obj.Visible && obj.IsViewable)
                        Display(obj, obj.Buffer!);
                }
            }

            private static void Magnify(MngImage? image, Magnification m)
            {
                image?.Magnify(m.MethodX, m.MethodY, m.MX, m.MY, m.ML, m.MR, m.MT, m.MB);
            }

            private void ProcessPaste(Chunk chunk)
            {
                if (chunk.Length < 11)
                    return;
                int p = chunk.Offset;
                int destId = U16(p);
                int targetType = U8(p + 2);
                int targetX = I32(p + 3), targetY = I32(p + 7);
                if (targetType == 1 && pastTargets.TryGetValue(destId, out var previous))
                    (targetX, targetY) = (previous.X + targetX, previous.Y + targetY);
                else if (targetType == 2)
                    (targetX, targetY) = (lastPastTarget.X + targetX, lastPastTarget.Y + targetY);
                pastTargets[destId] = lastPastTarget = (targetX, targetY);

                if (!objects.TryGetValue(destId, out var dest) || dest.Buffer == null)
                    return;
                var destImage = dest.Buffer;
                int width = destImage.Width, height = destImage.Height;
                var pixels = (byte[])destImage.ToBgra().Clone();

                // Take every source as it was before any pasting, in case one is the destination.
                var sources = new List<(MngImage Image, byte[] Pixels, int Offset)>();
                for (int s = p + 11; s + 30 <= p + chunk.Length; s += 30)
                {
                    if (objects.TryGetValue(U16(s), out var source) && source.IsViewable)
                        sources.Add((source.Buffer!, source.Buffer!.ToBgra(), s));
                }

                foreach (var (image, src, s) in sources)
                {
                    int composition = U8(s + 2), orientation = U8(s + 3);
                    int originX = U8(s + 4) == 1 ? targetX : 0, originY = U8(s + 4) == 1 ? targetY : 0;
                    int offsetX = originX + I32(s + 5), offsetY = originY + I32(s + 9);
                    int boundsX = U8(s + 13) == 1 ? targetX : 0, boundsY = U8(s + 13) == 1 ? targetY : 0;
                    int left = Math.Max(0, boundsX + I32(s + 14)), right = Math.Min(width, boundsX + I32(s + 18));
                    int top = Math.Max(0, boundsY + I32(s + 22)), bottom = Math.Min(height, boundsY + I32(s + 26));
                    bool tile = orientation == 8;
                    bool flipX = orientation == 2 || orientation == 4, flipY = orientation == 2 || orientation == 6;
                    if (!tile)
                    {
                        left = Math.Max(left, offsetX);
                        top = Math.Max(top, offsetY);
                        right = Math.Min(right, offsetX + image.Width);
                        bottom = Math.Min(bottom, offsetY + image.Height);
                    }
                    else
                    {
                        left = Math.Max(left, offsetX);
                        top = Math.Max(top, offsetY);
                    }

                    var under = new byte[4];
                    for (int y = top; y < bottom; y++)
                    {
                        int sy = (y - offsetY) % image.Height;
                        if (flipY)
                            sy = image.Height - 1 - sy;
                        for (int x = left; x < right; x++)
                        {
                            int sx = (x - offsetX) % image.Width;
                            if (flipX)
                                sx = image.Width - 1 - sx;
                            int si = (sy * image.Width + sx) * 4, di = (y * width + x) * 4;
                            if (composition == 1)
                            {
                                Array.Copy(src, si, pixels, di, 4);
                            }
                            else if (composition == 2)
                            {
                                Array.Copy(pixels, di, under, 0, 4);
                                Array.Copy(src, si, pixels, di, 4);
                                Blend(pixels, di, under, 0);
                            }
                            else
                            {
                                Blend(pixels, di, src, si);
                            }
                        }
                    }
                }

                // The destination is now an abstract image, which we keep as 8-bit RGBA.
                var result = new MngImage
                {
                    Width = width,
                    Height = height,
                    ColorType = 6,
                    Depth = 8,
                    PixelSampleDepth = 8,
                    AlphaSampleDepth = 8,
                    Samples = new int[width * height * 4],
                    Viewable = destImage.Viewable
                };
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    result.Samples[i] = pixels[i + 2];
                    result.Samples[i + 1] = pixels[i + 1];
                    result.Samples[i + 2] = pixels[i];
                    result.Samples[i + 3] = pixels[i + 3];
                }
                destImage.CopyFrom(result);
                if (dest.Visible && destImage.Viewable)
                    Display(dest, destImage);
            }

            #endregion

            #region Embedded images

            /// <summary>
            /// Processes an embedded PNG, JNG, BASI or Delta-PNG datastream. If it can't be
            /// decoded, it is skipped, and the object that it would have defined is discarded.
            /// </summary>
            private void ProcessEmbedded(int start, int end)
            {
                string type = chunks[start].Type;
                int id = type == "DHDR" ? (chunks[start].Length >= 2 ? U16(chunks[start].Offset) : 0) : currentId;
                try
                {
                    if (type == "DHDR")
                    {
                        ProcessDelta(start, end);
                        return;
                    }
                    var image = type == "JHDR" ? DecodeJng(start, end) : DecodePng(start, end, type == "BASI");
                    if (id == 0)
                    {
                        if (magnification0 != null)
                            Magnify(image, magnification0);
                        if (object0.Visible && image.Viewable)
                            Display(object0, image);
                        return;
                    }
                    if (!objects.TryGetValue(id, out var obj))
                        objects[id] = obj = NewObject();
                    if (obj.Buffer != null)
                        obj.Buffer.CopyFrom(image);
                    else
                        obj.Buffer = image;
                    if (obj.Visible && obj.IsViewable)
                        Display(obj, obj.Buffer);
                }
                catch (Exception e) when (e is ImageDecodeException || e is InvalidDataException || e is IndexOutOfRangeException || e is ArgumentException)
                {
                    if (id != 0 && objects.TryGetValue(id, out var obj) && !obj.Frozen)
                        objects.Remove(id);
                }
            }

            /// <summary>
            /// Gets the gamma of an embedded image from its color space chunks, or if it has none,
            /// from those of the MNG datastream.
            /// </summary>
            private sealed class ColorSpace
            {
                public bool Seen;
                public double? Gamma;

                public void Read(MngRenderer r, Chunk chunk)
                {
                    switch (chunk.Type)
                    {
                        case "gAMA":
                            Seen = true;
                            Gamma = r.ReadGamma(chunk);
                            break;
                        case "sRGB":
                            Seen = true;
                            Gamma = 0.45455;
                            break;
                        case "iCCP":
                        case "cHRM":
                            Seen = true;
                            break;
                    }
                }
            }

            private MngImage DecodePng(int start, int end, bool isBasi)
            {
                var header = chunks[start];
                if (header.Length < 13)
                    throw new ImageDecodeException("Invalid MNG image header.");
                int p = header.Offset;
                int width = I32(p), height = I32(p + 4), depth = U8(p + 8), colorType = U8(p + 9);
                int filter = U8(p + 11), interlace = U8(p + 12);
                PngDecoder.Validate(colorType, depth);

                var colorSpace = new ColorSpace();
                byte[]? palette = null, paletteAlpha = null;
                int[]? key = null;
                bool hasPalette = false;
                using var idat = new MemoryStream();
                for (int i = start + 1; i < end; i++)
                {
                    var chunk = chunks[i];
                    switch (chunk.Type)
                    {
                        case "PLTE":
                            hasPalette = true;
                            if (chunk.Length == 0)
                            {
                                palette = globalPalette;
                                paletteAlpha ??= globalPaletteAlpha;
                            }
                            else
                            {
                                palette = Bytes(chunk);
                            }
                            break;
                        case "tRNS":
                            if (colorType == 3)
                                paletteAlpha = Bytes(chunk);
                            else
                                key = ReadKey(chunk, colorType);
                            break;
                        case "IDAT":
                            idat.Write(data, chunk.Offset, chunk.Length);
                            break;
                        default:
                            colorSpace.Read(this, chunk);
                            break;
                    }
                }

                MngImage image;
                if (isBasi && idat.Length == 0)
                {
                    int n = header.Length;
                    image = MngImage.Filled(width, height, colorType, depth,
                        n >= 15 ? U16(p + 13) : 0, n >= 17 ? U16(p + 15) : 0, n >= 19 ? U16(p + 17) : 0,
                        n >= 21 ? U16(p + 19) : null);
                    if (colorType == 3 && hasPalette)
                    {
                        image.Palette = palette;
                        image.PaletteAlpha = paletteAlpha ?? image.PaletteAlpha;
                    }
                }
                else
                {
                    if (colorType == 3 && palette == null)
                        throw new ImageDecodeException("Indexed MNG image has no palette.");
                    image = new MngImage
                    {
                        Width = width,
                        Height = height,
                        ColorType = colorType,
                        Depth = depth,
                        Samples = PngDecoder.Decode(idat.ToArray(), width, height, colorType, depth, filter, interlace),
                        Palette = palette,
                        PaletteAlpha = paletteAlpha
                    };
                }
                if (key != null)
                    image.TransparentKey = key;
                image.PixelSampleDepth = image.AlphaSampleDepth = depth;
                image.FilterMethod = filter;
                image.InterlaceMethod = interlace;
                image.Gamma = colorSpace.Seen ? colorSpace.Gamma : globalGamma;
                image.Viewable = !isBasi || (header.Length >= 22 && U8(p + 21) == 1);
                return image;
            }

            private int[]? ReadKey(Chunk chunk, int colorType)
            {
                if (colorType == 0 && chunk.Length >= 2)
                    return new[] { U16(chunk.Offset) };
                if (colorType == 2 && chunk.Length >= 6)
                    return new[] { U16(chunk.Offset), U16(chunk.Offset + 2), U16(chunk.Offset + 4) };
                return null;
            }

            private MngImage DecodeJng(int start, int end)
            {
                var header = chunks[start];
                if (header.Length < 16)
                    throw new ImageDecodeException("Invalid JNG image header.");
                int p = header.Offset;
                int width = I32(p), height = I32(p + 4), colorType = U8(p + 8);
                int alphaDepth = U8(p + 12), alphaCompression = U8(p + 13), alphaFilter = U8(p + 14), alphaInterlace = U8(p + 15);
                if (colorType is not (8 or 10 or 12 or 14))
                    throw new ImageDecodeException("Unsupported JNG color type: " + colorType);

                var colorSpace = new ColorSpace();
                using var jdat = new MemoryStream();
                using var alpha = new MemoryStream();
                bool separated = false;
                for (int i = start + 1; i < end; i++)
                {
                    var chunk = chunks[i];
                    switch (chunk.Type)
                    {
                        case "JDAT":
                            // Of an 8-bit image followed by a 12-bit one, we decode the first.
                            if (!separated)
                                jdat.Write(data, chunk.Offset, chunk.Length);
                            break;
                        case "JSEP":
                            separated = true;
                            break;
                        case "IDAT":
                        case "JDAA":
                            if (!separated)
                                alpha.Write(data, chunk.Offset, chunk.Length);
                            break;
                        default:
                            colorSpace.Read(this, chunk);
                            break;
                    }
                }

                var image = new MngImage { ColorType = colorType, AlphaDepth = alphaDepth };
                DecodeJngData(image, jdat.ToArray(), alpha.ToArray(), width, height, alphaDepth, alphaCompression, alphaFilter, alphaInterlace);
                image.PixelSampleDepth = image.AlphaSampleDepth = alphaDepth;
                image.FilterMethod = alphaFilter;
                image.InterlaceMethod = alphaInterlace;
                image.Gamma = colorSpace.Seen ? colorSpace.Gamma : globalGamma;
                return image;
            }

            /// <summary>
            /// Decodes the JPEG image of a JNG datastream and its alpha channel, if it has one,
            /// into the given image, whose color type is already set.
            /// </summary>
            private static void DecodeJngData(MngImage image, byte[] jpeg, byte[] alphaData, int width, int height,
                int alphaDepth, int alphaCompression, int alphaFilter, int alphaInterlace)
            {
                var decoded = JpegDecoder.Decode(jpeg);
                if (decoded.Components != 1 && decoded.Components != 3)
                    throw new ImageDecodeException("Unsupported JNG component count: " + decoded.Components);
                width = decoded.Width;
                height = decoded.Height;

                int[]? alpha = null;
                if (image.HasAlpha)
                {
                    if (alphaData.Length > 0 && alphaCompression == 8)
                    {
                        alpha = JpegDecoder.Decode(alphaData).Samples;
                        alphaDepth = 8;
                    }
                    else if (alphaData.Length > 0)
                    {
                        alpha = PngDecoder.Decode(alphaData, width, height, 0, alphaDepth, alphaFilter, alphaInterlace);
                    }
                    else
                    {
                        alphaDepth = Math.Max(1, alphaDepth);
                    }
                }

                int colorChannels = image.ColorType is 10 or 14 ? 3 : 1;
                int channels = colorChannels + (image.HasAlpha ? 1 : 0);
                var samples = new int[width * height * channels];
                var s = decoded.Samples;
                int n = decoded.Components, opaque = (1 << alphaDepth) - 1;
                for (int i = 0; i < width * height; i++)
                {
                    int o = i * channels;
                    if (colorChannels == 3)
                    {
                        samples[o] = s[i * n];
                        samples[o + 1] = s[i * n + n / 2];
                        samples[o + 2] = s[i * n + n - 1];
                    }
                    else
                    {
                        samples[o] = n == 3 ? (s[i * 3] * 299 + s[i * 3 + 1] * 587 + s[i * 3 + 2] * 114 + 500) / 1000 : s[i];
                    }
                    if (image.HasAlpha)
                        samples[o + channels - 1] = alpha != null && i < alpha.Length ? alpha[i] : opaque;
                }
                image.Width = width;
                image.Height = height;
                image.Depth = decoded.Precision;
                image.AlphaDepth = image.HasAlpha ? alphaDepth : 0;
                image.Samples = samples;
                image.Invalidate();
            }

            /// <summary>
            /// Applies a Delta-PNG datastream to the object that it names.
            /// </summary>
            private void ProcessDelta(int start, int end)
            {
                var header = chunks[start];
                if (header.Length < 4)
                    throw new ImageDecodeException("Invalid Delta-PNG header.");
                int p = header.Offset;
                int id = U16(p), deltaType = U8(p + 3);
                if (!objects.TryGetValue(id, out var obj) || obj.Buffer == null || obj.Frozen)
                    return;
                int blockWidth = header.Length >= 12 ? I32(p + 4) : obj.Buffer.Width;
                int blockHeight = header.Length >= 12 ? I32(p + 8) : obj.Buffer.Height;
                int blockX = header.Length >= 20 ? I32(p + 12) : 0;
                int blockY = header.Length >= 20 ? I32(p + 16) : 0;

                var image = obj.Buffer.Clone();
                int? newDepth = null, newColorType = null, filter = null, interlace = null;
                int alphaCompression = 0;
                using var idat = new MemoryStream();
                using var jdat = new MemoryStream();
                using var jdaa = new MemoryStream();
                bool separated = false;
                for (int i = start + 1; i < end; i++)
                {
                    var chunk = chunks[i];
                    int c = chunk.Offset;
                    switch (chunk.Type)
                    {
                        case "PROM":
                            if (chunk.Length >= 3)
                                image.Promote(U8(c), U8(c + 1), U8(c + 2) == 1);
                            break;
                        case "IHDR":
                            if (chunk.Length >= 13)
                            {
                                newDepth = U8(c + 8);
                                newColorType = U8(c + 9);
                                filter = U8(c + 11);
                                interlace = U8(c + 12);
                            }
                            break;
                        case "JHDR":
                            if (chunk.Length >= 16)
                            {
                                newDepth = U8(c + 12);
                                alphaCompression = U8(c + 13);
                                filter = U8(c + 14);
                                interlace = U8(c + 15);
                            }
                            break;
                        case "PLTE":
                            if (chunk.Length > 0)
                            {
                                image.Palette = Bytes(chunk);
                                if (image.PaletteAlpha != null && image.PaletteAlpha.Length * 3 > chunk.Length)
                                    Array.Resize(ref image.PaletteAlpha, chunk.Length / 3);
                            }
                            break;
                        case "tRNS":
                            if (image.ColorType == 3)
                                image.PaletteAlpha = Bytes(chunk);
                            else
                                image.TransparentKey = ReadKey(chunk, image.ColorType);
                            break;
                        case "PPLT":
                            ApplyPartialPalette(image, chunk);
                            break;
                        case "gAMA":
                            image.Gamma = ReadGamma(chunk);
                            break;
                        case "sRGB":
                            image.Gamma = 0.45455;
                            break;
                        case "IDAT":
                            idat.Write(data, chunk.Offset, chunk.Length);
                            break;
                        case "JDAT":
                            if (!separated)
                                jdat.Write(data, chunk.Offset, chunk.Length);
                            break;
                        case "JDAA":
                            if (!separated)
                                jdaa.Write(data, chunk.Offset, chunk.Length);
                            break;
                        case "JSEP":
                            separated = true;
                            break;
                    }
                }

                if (image.IsJng)
                    ApplyJngDelta(image, deltaType, newDepth, alphaCompression, filter, interlace, idat.ToArray(), jdat.ToArray(), jdaa.ToArray(), blockWidth, blockHeight, blockX, blockY);
                else
                    ApplyPngDelta(image, deltaType, newDepth, newColorType, filter, interlace, idat.ToArray(), blockWidth, blockHeight, blockX, blockY);

                image.Viewable = true;
                image.Invalidate();
                obj.Buffer.CopyFrom(image);
                if (obj.Visible)
                    Display(obj, obj.Buffer);
            }

            private static void ApplyPngDelta(MngImage image, int deltaType, int? newDepth, int? newColorType, int? filter, int? interlace,
                byte[] idat, int blockWidth, int blockHeight, int blockX, int blockY)
            {
                if (filter != null)
                {
                    image.FilterMethod = filter.Value;
                    image.InterlaceMethod = interlace!.Value;
                }
                bool alphaOnly = deltaType == 2 || deltaType == 5;
                if (newDepth != null && deltaType != 0)
                {
                    if (alphaOnly)
                        image.AlphaSampleDepth = newDepth.Value;
                    else
                        image.PixelSampleDepth = newDepth.Value;
                }
                if (deltaType == 7 || idat.Length == 0)
                    return;

                if (deltaType == 0)
                {
                    int colorType = newColorType ?? image.ColorType, depth = newDepth ?? image.Depth;
                    image.Samples = PngDecoder.Decode(idat, blockWidth, blockHeight, colorType, depth, image.FilterMethod, image.InterlaceMethod);
                    image.Width = blockWidth;
                    image.Height = blockHeight;
                    image.ColorType = colorType;
                    image.Depth = depth;
                    if (newDepth != null)
                        image.PixelSampleDepth = image.AlphaSampleDepth = depth;
                    return;
                }

                bool colorOnly = deltaType == 3 || deltaType == 6;
                if ((alphaOnly || colorOnly) && !image.HasAlpha)
                    throw new ImageDecodeException("Delta-PNG requires an image with an alpha channel.");
                int deltaColorType = newColorType ?? (alphaOnly ? 0 : colorOnly ? (image.ColorType == 6 ? 2 : 0) : image.ColorType);
                int deltaDepth = alphaOnly ? image.AlphaSampleDepth : image.PixelSampleDepth;
                var delta = PngDecoder.Decode(idat, blockWidth, blockHeight, deltaColorType, deltaDepth, image.FilterMethod, image.InterlaceMethod);
                int firstChannel = alphaOnly ? image.Channels - 1 : 0;
                int channelCount = alphaOnly ? 1 : colorOnly ? image.ColorChannels : image.Channels;
                ApplyBlock(image, delta, PngDecoder.Channels(deltaColorType), firstChannel, channelCount, deltaDepth, image.Depth,
                    deltaType <= 3, image.ColorType == 3, blockWidth, blockHeight, blockX, blockY);
            }

            private static void ApplyJngDelta(MngImage image, int deltaType, int? newDepth, int alphaCompression, int? filter, int? interlace,
                byte[] idat, byte[] jdat, byte[] jdaa, int blockWidth, int blockHeight, int blockX, int blockY)
            {
                if (filter != null)
                {
                    image.FilterMethod = filter.Value;
                    image.InterlaceMethod = interlace!.Value;
                }
                if (newDepth != null && deltaType != 0)
                    image.AlphaSampleDepth = newDepth.Value;
                if (deltaType == 0)
                {
                    if (jdat.Length == 0)
                        return;
                    int alphaDepth = newDepth ?? image.AlphaDepth;
                    DecodeJngData(image, jdat, jdaa.Length > 0 ? jdaa : idat, blockWidth, blockHeight, alphaDepth,
                        jdaa.Length > 0 ? 8 : alphaCompression, image.FilterMethod, image.InterlaceMethod);
                    image.AlphaSampleDepth = image.AlphaDepth;
                    return;
                }
                if (deltaType != 2 && deltaType != 5)
                    return;
                if (!image.HasAlpha)
                    throw new ImageDecodeException("Delta-PNG requires an image with an alpha channel.");

                int[] delta;
                int deltaDepth;
                if (jdaa.Length > 0)
                {
                    delta = JpegDecoder.Decode(jdaa).Samples;
                    deltaDepth = 8;
                }
                else if (idat.Length > 0)
                {
                    deltaDepth = image.AlphaSampleDepth;
                    delta = PngDecoder.Decode(idat, blockWidth, blockHeight, 0, deltaDepth, image.FilterMethod, image.InterlaceMethod);
                }
                else
                {
                    return;
                }
                ApplyBlock(image, delta, 1, image.Channels - 1, 1, deltaDepth, image.AlphaDepth, deltaType == 2, false,
                    blockWidth, blockHeight, blockX, blockY);
            }

            /// <summary>
            /// Adds a block of delta samples to, or replaces with them, the given channels of a
            /// block of pixels, after scaling them to the image's sample depth.
            /// </summary>
            private static void ApplyBlock(MngImage image, int[] delta, int deltaChannels, int firstChannel, int channelCount,
                int deltaDepth, int depth, bool add, bool indexed, int blockWidth, int blockHeight, int blockX, int blockY)
            {
                if (deltaChannels != channelCount)
                    throw new ImageDecodeException("Delta-PNG color type doesn't match its parent.");
                if (blockX < 0 || blockY < 0 || blockWidth <= 0 || blockHeight <= 0
                    || (long)blockX + blockWidth > image.Width || (long)blockY + blockHeight > image.Height)
                    throw new ImageDecodeException("Delta-PNG block is outside of its parent.");
                if (delta.Length < blockWidth * blockHeight * deltaChannels)
                    throw new ImageDecodeException("Delta-PNG data is truncated.");

                int channels = image.Channels, mask = (1 << depth) - 1;
                var samples = image.Samples;
                for (int y = 0; y < blockHeight; y++)
                {
                    for (int x = 0; x < blockWidth; x++)
                    {
                        int d = (y * blockWidth + x) * deltaChannels;
                        int o = ((blockY + y) * image.Width + blockX + x) * channels + firstChannel;
                        for (int c = 0; c < channelCount; c++)
                        {
                            int v = delta[d + c];
                            if (!indexed && deltaDepth != depth)
                            {
                                v = add ? (deltaDepth < depth ? v << (depth - deltaDepth) : v >> (deltaDepth - depth))
                                    : MngImage.Widen(v, deltaDepth, depth, false);
                            }
                            samples[o + c] = add ? (samples[o + c] + v) & mask : v & mask;
                        }
                    }
                }
            }

            private void ApplyPartialPalette(MngImage image, Chunk chunk)
            {
                if (chunk.Length < 1)
                    return;
                int type = U8(chunk.Offset);
                int size = type switch { 0 or 1 => 3, 2 or 3 => 1, _ => 4 };
                bool delta = type % 2 == 1;
                bool hasColor = type is 0 or 1 or 4 or 5, hasAlpha = type >= 2;
                var palette = image.Palette ?? Array.Empty<byte>();
                var alpha = image.PaletteAlpha;
                int originalEntries = palette.Length / 3;
                int end = chunk.Offset + chunk.Length;
                for (int p = chunk.Offset + 1; p + 2 <= end;)
                {
                    int first = U8(p), last = U8(p + 1);
                    p += 2;
                    for (int index = first; index <= last && p + size <= end; index++, p += size)
                    {
                        if (palette.Length < (index + 1) * 3)
                            Array.Resize(ref palette, (index + 1) * 3);
                        if (hasAlpha && (alpha == null || alpha.Length < index + 1))
                        {
                            int oldLength = alpha?.Length ?? 0;
                            Array.Resize(ref alpha, Math.Max(index + 1, palette.Length / 3));
                            Array.Fill(alpha, (byte)0xFF, oldLength, alpha.Length - oldLength);
                        }
                        bool add = delta && index < originalEntries;
                        if (hasColor)
                        {
                            for (int c = 0; c < 3; c++)
                                palette[index * 3 + c] = (byte)((add ? palette[index * 3 + c] : 0) + data[p + c]);
                        }
                        if (hasAlpha)
                            alpha![index] = (byte)((add ? alpha[index] : 0) + data[p + size - 1]);
                    }
                }
                image.Palette = palette;
                image.PaletteAlpha = alpha;
            }

            #endregion
        }
    }
}
