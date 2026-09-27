using System;
using System.Buffers.Binary;

/*

Decoder for the RLE compression defined by DICOM (PS3.5 Annex G). A compressed
frame starts with a 64-byte header giving the number of segments (up to 15)
and the offset of each. Each segment holds one byte plane of the image: for
each sample of a pixel, the most significant byte of every pixel, then the next
byte, and so on, so that a 16-bit RGB image has six segments. Each segment is
compressed with a PackBits-style byte run-length scheme.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class RleDecoder
    {
        public static bool HasSignature(byte[] data)
        {
            if (data.Length < 64)
                return false;
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
            int firstOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            return count >= 1 && count <= 15 && firstOffset == 64;
        }

        /// <summary>
        /// Decodes an RLE-compressed frame. The resulting samples are the raw stored
        /// values, bitsAllocated wide.
        /// </summary>
        public static CodecImage Decode(byte[] data, int width, int height, int samplesPerPixel, int bitsAllocated)
        {
            if (data.Length < 64)
                throw new ImageDecodeException("RLE frame is too short.");
            int bytesPerSample = (bitsAllocated + 7) / 8;
            int numSegments = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
            if (numSegments < 1 || numSegments > 15)
                throw new ImageDecodeException("Invalid number of RLE segments.");
            if (numSegments != samplesPerPixel * bytesPerSample)
                Util.log("Unexpected number of RLE segments: " + numSegments);

            int numPixels = width * height;
            var samples = new int[numPixels * samplesPerPixel];
            var plane = new byte[numPixels];

            for (int seg = 0; seg < Math.Min(numSegments, samplesPerPixel * bytesPerSample); seg++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 + seg * 4));
                int end = seg + 1 < numSegments ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8 + seg * 4)) : data.Length;
                if (start < 64 || start > data.Length)
                    break;
                if (end < start || end > data.Length)
                    end = data.Length;

                Array.Clear(plane);
                DecodeSegment(data, start, end, plane);

                int sample = seg / bytesPerSample;
                int shift = 8 * (bytesPerSample - 1 - seg % bytesPerSample);
                for (int i = 0, j = sample; i < numPixels; i++, j += samplesPerPixel)
                    samples[j] |= plane[i] << shift;
            }
            return new CodecImage(width, height, samplesPerPixel, bitsAllocated, false, samples);
        }

        private static void DecodeSegment(byte[] data, int pos, int end, byte[] output)
        {
            int outPos = 0;
            while (pos < end && outPos < output.Length)
            {
                int n = (sbyte)data[pos++];
                if (n >= 0)
                {
                    // Copy the next n + 1 bytes literally.
                    int count = Math.Min(Math.Min(n + 1, end - pos), output.Length - outPos);
                    Array.Copy(data, pos, output, outPos, count);
                    pos += n + 1;
                    outPos += count;
                }
                else if (n != -128)
                {
                    // Repeat the next byte -n + 1 times.
                    if (pos >= end)
                        break;
                    byte b = data[pos++];
                    int count = Math.Min(-n + 1, output.Length - outPos);
                    output.AsSpan(outPos, count).Fill(b);
                    outPos += count;
                }
            }
        }
    }
}
