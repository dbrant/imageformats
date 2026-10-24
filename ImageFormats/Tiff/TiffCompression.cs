#nullable enable
using System;
using System.IO;
using System.IO.Compression;

/*

The general-purpose compression schemes of TIFF (LZW, PackBits and Deflate), and the
predictors that may be applied to the data before compression.

Each decompressor fills a buffer of the size that the strip or tile should have, and
stops when either the input or the output runs out, so that a truncated or damaged
chunk only spoils its own part of the image.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class TiffCompression
    {
        /// <summary>
        /// Decompresses LZW data. TIFF's LZW codes are 9 to 12 bits long, packed most
        /// significant bit first, and the code length goes up one code earlier than it
        /// strictly needs to. Files written by very old software (before TIFF 5.0) instead
        /// pack the codes least significant bit first, without the early change; we tell
        /// them apart the same way libtiff does.
        /// </summary>
        public static void DecompressLzw(ReadOnlySpan<byte> input, byte[] output)
        {
            const int clearCode = 256, eoiCode = 257, maxCodes = 4096;
            bool oldStyle = input.Length >= 2 && input[0] == 0 && (input[1] & 1) != 0;
            int early = oldStyle ? 0 : 1;

            var prefix = new int[maxCodes];
            var suffix = new byte[maxCodes];
            var first = new byte[maxCodes];
            var lengths = new int[maxCodes];
            for (int i = 0; i < 256; i++)
            {
                suffix[i] = first[i] = (byte)i;
                lengths[i] = 1;
            }

            int nextCode = 258, codeLength = 9, previous = -1;
            int outPos = 0, inPos = 0;
            uint bitBuf = 0;
            int bitCount = 0;

            while (outPos < output.Length)
            {
                while (bitCount < codeLength && inPos < input.Length)
                {
                    if (oldStyle)
                        bitBuf |= (uint)input[inPos++] << bitCount;
                    else
                        bitBuf = (bitBuf << 8) | input[inPos++];
                    bitCount += 8;
                }
                if (bitCount < codeLength)
                    break;
                int code;
                if (oldStyle)
                {
                    code = (int)(bitBuf & ((1u << codeLength) - 1));
                    bitBuf >>= codeLength;
                }
                else
                {
                    code = (int)((bitBuf >> (bitCount - codeLength)) & ((1u << codeLength) - 1));
                }
                bitCount -= codeLength;

                if (code == eoiCode)
                    break;
                if (code == clearCode)
                {
                    nextCode = 258;
                    codeLength = 9;
                    previous = -1;
                    continue;
                }

                if (previous < 0)
                {
                    if (code > 255)
                        break;
                    output[outPos++] = (byte)code;
                    previous = code;
                    continue;
                }

                // A code not yet in the table must be the one about to be added: the previous
                // string plus its own first byte.
                int added = -1;
                if (nextCode < maxCodes)
                {
                    if (code > nextCode)
                        break;
                    added = nextCode++;
                    prefix[added] = previous;
                    suffix[added] = first[code == added ? previous : code];
                    first[added] = first[previous];
                    lengths[added] = lengths[previous] + 1;
                }
                else if (code >= maxCodes)
                {
                    break;
                }

                // Write out the string for this code, back to front.
                int len = lengths[code];
                int end = outPos + len;
                for (int c = code, p = end - 1; p >= outPos; c = prefix[c], p--)
                {
                    if (p < output.Length)
                        output[p] = suffix[c];
                }
                outPos = Math.Min(end, output.Length);
                previous = code;

                if (nextCode + early >= (1 << codeLength) && codeLength < 12)
                    codeLength++;
            }
        }

        /// <summary>
        /// Decompresses PackBits (Macintosh run-length) data.
        /// </summary>
        public static void DecompressPackBits(ReadOnlySpan<byte> input, byte[] output)
        {
            int inPos = 0, outPos = 0;
            while (inPos < input.Length && outPos < output.Length)
            {
                int n = (sbyte)input[inPos++];
                if (n >= 0)
                {
                    int count = Math.Min(Math.Min(n + 1, input.Length - inPos), output.Length - outPos);
                    input.Slice(inPos, count).CopyTo(output.AsSpan(outPos));
                    inPos += n + 1;
                    outPos += count;
                }
                else if (n != -128 && inPos < input.Length)
                {
                    byte value = input[inPos++];
                    int count = Math.Min(1 - n, output.Length - outPos);
                    output.AsSpan(outPos, count).Fill(value);
                    outPos += count;
                }
            }
        }

        /// <summary>
        /// Decompresses Deflate (zlib) data.
        /// </summary>
        public static void DecompressDeflate(byte[] input, byte[] output)
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
                zlib.ReadAtLeast(output, output.Length, false);
            }
            catch (InvalidDataException e)
            {
                Util.log("Error while decompressing TIFF Deflate data: " + e.Message);
            }
        }

        /// <summary>
        /// Reverses the order of the bits in each byte (for FillOrder 2).
        /// </summary>
        public static void ReverseBits(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                uint b = data[i];
                data[i] = (byte)((b * 0x0202020202UL & 0x010884422010UL) % 1023);
            }
        }

        /// <summary>
        /// Undoes horizontal differencing (Predictor 2), in which each sample is stored as
        /// the difference from the same sample of the pixel to its left. Only 8, 16, 32 and
        /// 64-bit samples can be predicted.
        /// </summary>
        public static void UndoHorizontalPredictor(byte[] data, int rows, int rowBytes, int samplesPerPixel, int bitsPerSample, bool bigEndian)
        {
            int bytesPerSample = bitsPerSample / 8;
            if (bitsPerSample % 8 != 0 || bytesPerSample > 8 || (bytesPerSample & (bytesPerSample - 1)) != 0)
                throw new ImageDecodeException("Unsupported bit depth for the TIFF horizontal predictor: " + bitsPerSample);
            int stride = samplesPerPixel * bytesPerSample;
            for (int r = 0; r < rows; r++)
            {
                int start = r * rowBytes;
                int end = start + rowBytes - rowBytes % stride;
                if (bytesPerSample == 1)
                {
                    for (int i = start + stride; i < end; i++)
                        data[i] += data[i - stride];
                    continue;
                }
                for (int i = start + stride; i < end; i += bytesPerSample)
                {
                    ulong value = ReadSample(data, i, bytesPerSample, bigEndian) + ReadSample(data, i - stride, bytesPerSample, bigEndian);
                    WriteSample(data, i, bytesPerSample, bigEndian, value);
                }
            }
        }

        /// <summary>
        /// Undoes the floating point predictor (Predictor 3). Each row is stored as the
        /// bytes of all its samples, grouped by significance (the most significant bytes
        /// of every sample first), and then differenced byte by byte like Predictor 2.
        /// </summary>
        public static void UndoFloatPredictor(byte[] data, int rows, int rowBytes, int samplesPerPixel, int bitsPerSample, int width, bool bigEndian)
        {
            int bytesPerSample = bitsPerSample / 8;
            int samples = width * samplesPerPixel;
            int count = samples * bytesPerSample;
            if (bitsPerSample % 8 != 0 || count > rowBytes)
                throw new ImageDecodeException("Unsupported bit depth for the TIFF floating point predictor: " + bitsPerSample);
            var temp = new byte[count];
            for (int r = 0; r < rows; r++)
            {
                int start = r * rowBytes;
                for (int i = start + samplesPerPixel; i < start + count; i++)
                    data[i] += data[i - samplesPerPixel];
                Array.Copy(data, start, temp, 0, count);
                for (int s = 0; s < samples; s++)
                {
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        // Byte b of the sample, in file order, comes from significance plane
                        // b (big-endian) or bytesPerSample - 1 - b (little-endian).
                        int plane = bigEndian ? b : bytesPerSample - 1 - b;
                        data[start + s * bytesPerSample + b] = temp[plane * samples + s];
                    }
                }
            }
        }

        public static ulong ReadSample(byte[] data, int pos, int bytes, bool bigEndian)
        {
            ulong v = 0;
            for (int i = 0; i < bytes; i++)
                v |= (ulong)data[pos + (bigEndian ? bytes - 1 - i : i)] << (8 * i);
            return v;
        }

        public static void WriteSample(byte[] data, int pos, int bytes, bool bigEndian, ulong value)
        {
            for (int i = 0; i < bytes; i++)
                data[pos + (bigEndian ? bytes - 1 - i : i)] = (byte)(value >> (8 * i));
        }
    }
}
