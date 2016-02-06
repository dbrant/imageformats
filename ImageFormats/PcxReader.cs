using System;
using System.Drawing;
using System.IO;

/*

Decoder for ZSoft Paintbrush (.PCX) images.
Supports pretty much the full PCX specification (all bit
depths, etc).  At the very least, it decodes all PCX images that
I've found in the wild.  If you find one that it fails to decode,
let me know!

Copyright 2013-2016 Dmitry Brant
http://dmitrybrant.com

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading ZSoft PCX images.
    /// </summary>
    public static class PcxReader
    {

        /// <summary>
        /// Reads a PCX image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp = Load(f);
            }
            return bmp;
        }

        /// <summary>
        /// Reads a PCX image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            int imgWidth = -1;
            int imgHeight = -1;
            int imgBpp = -1;
            Bitmap theBitmap = null;
            BinaryReader reader = new BinaryReader(stream);

            byte tempByte = (byte)stream.ReadByte();
            if (tempByte != 10)
                throw new ApplicationException("This is not a valid PCX file.");

            tempByte = (byte)stream.ReadByte();
            if (tempByte != 5)
                throw new ApplicationException("Only Version-5 PCX files are supported.");

            tempByte = (byte)stream.ReadByte();
            if (tempByte != 1)
                throw new ApplicationException("Invalid PCX compression type.");

            imgBpp = stream.ReadByte();
            if (imgBpp != 8 && imgBpp != 4 && imgBpp != 2 && imgBpp != 1)
                throw new ApplicationException("Only 8, 4, 2, and 1-bit PCX samples are supported.");

            UInt16 xmin = LittleEndian(reader.ReadUInt16());
            UInt16 ymin = LittleEndian(reader.ReadUInt16());
            UInt16 xmax = LittleEndian(reader.ReadUInt16());
            UInt16 ymax = LittleEndian(reader.ReadUInt16());

            imgWidth = xmax - xmin + 1;
            imgHeight = ymax - ymin + 1;

            if ((imgWidth < 1) || (imgHeight < 1) || (imgWidth > 32767) || (imgHeight > 32767))
                throw new ApplicationException("This PCX file appears to have invalid dimensions.");

            LittleEndian(reader.ReadUInt16()); //hdpi
            LittleEndian(reader.ReadUInt16()); //vdpi

            byte[] colorPalette = new byte[48];
            stream.Read(colorPalette, 0, 48);

            stream.ReadByte();

            int numPlanes = stream.ReadByte();
            int bytesPerLine = LittleEndian(reader.ReadUInt16());
            if (bytesPerLine == 0) bytesPerLine = xmax - xmin + 1;

            if (imgBpp == 8 && numPlanes == 1)
            {
                colorPalette = new byte[768];
                stream.Seek(-768, SeekOrigin.End);
                stream.Read(colorPalette, 0, 768);
            }

            //fix color palette if it's a 1-bit image, and there's no palette information
            if (imgBpp == 1)
            {
                if ((colorPalette[0] == colorPalette[3]) && (colorPalette[1] == colorPalette[4]) && (colorPalette[2] == colorPalette[5]))
                {
                    colorPalette[0] = colorPalette[1] = colorPalette[2] = 0;
                    colorPalette[3] = colorPalette[4] = colorPalette[5] = 0xFF;
                }
            }

            byte[] bmpData = new byte[(imgWidth + 1) * 4 * imgHeight];
            stream.Seek(128, SeekOrigin.Begin);
            int x = 0, y = 0, i, j;

            RleReader rleReader = new RleReader(stream);

            try
            {
                if (imgBpp == 1)
                {
                    int b, p;
                    byte val;
                    byte[] scanline = new byte[bytesPerLine];
                    byte[] realscanline = new byte[bytesPerLine * 8];

                    for (y = 0; y < imgHeight; y++)
                    {
                        //add together all the planes...
                        Array.Clear(realscanline, 0, realscanline.Length);
                        for (p = 0; p < numPlanes; p++)
                        {
                            x = 0;
                            for (i = 0; i < bytesPerLine; i++)
                            {
                                scanline[i] = (byte)rleReader.ReadByte();

                                for (b = 7; b >= 0; b--)
                                {
                                    if ((scanline[i] & (1 << b)) != 0) val = 1; else val = 0;
                                    realscanline[x] |= (byte)(val << p);
                                    x++;
                                }
                            }
                        }

                        for (x = 0; x < imgWidth; x++)
                        {
                            i = realscanline[x];
                            bmpData[4 * (y * imgWidth + x)] = colorPalette[i * 3 + 2];
                            bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[i * 3 + 1];
                            bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[i * 3];
                        }
                    }
                }
                else
                {
                    if (numPlanes == 1)
                    {
                        if (imgBpp == 8)
                        {
                            byte[] scanline = new byte[bytesPerLine];
                            for (y = 0; y < imgHeight; y++)
                            {
                                for (i = 0; i < bytesPerLine; i++)
                                    scanline[i] = (byte)rleReader.ReadByte();

                                for (x = 0; x < imgWidth; x++)
                                {
                                    i = scanline[x];
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[i * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[i * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[i * 3];
                                }
                            }
                        }
                        else if (imgBpp == 4)
                        {
                            byte[] scanline = new byte[bytesPerLine];
                            for (y = 0; y < imgHeight; y++)
                            {
                                for (i = 0; i < bytesPerLine; i++)
                                    scanline[i] = (byte)rleReader.ReadByte();

                                for (x = 0; x < imgWidth; x++)
                                {
                                    i = scanline[x / 2];
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[((i >> 4) & 0xF) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[((i >> 4) & 0xF) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[((i >> 4) & 0xF) * 3];
                                    x++;
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[(i & 0xF) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[(i & 0xF) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[(i & 0xF) * 3];
                                }
                            }
                        }
                        else if (imgBpp == 2)
                        {
                            byte[] scanline = new byte[bytesPerLine];
                            for (y = 0; y < imgHeight; y++)
                            {
                                for (i = 0; i < bytesPerLine; i++)
                                    scanline[i] = (byte)rleReader.ReadByte();

                                for (x = 0; x < imgWidth; x++)
                                {
                                    i = scanline[x / 4];
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[((i >> 6) & 0x3) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[((i >> 6) & 0x3) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[((i >> 6) & 0x3) * 3];
                                    x++;
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[((i >> 4) & 0x3) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[((i >> 4) & 0x3) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[((i >> 4) & 0x3) * 3];
                                    x++;
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[((i >> 2) & 0x3) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[((i >> 2) & 0x3) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[((i >> 2) & 0x3) * 3];
                                    x++;
                                    bmpData[4 * (y * imgWidth + x)] = colorPalette[(i & 0x3) * 3 + 2];
                                    bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[(i & 0x3) * 3 + 1];
                                    bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[(i & 0x3) * 3];
                                }
                            }
                        }
                    }
                    else if (numPlanes == 3)
                    {
                        byte[] scanlineR = new byte[bytesPerLine];
                        byte[] scanlineG = new byte[bytesPerLine];
                        byte[] scanlineB = new byte[bytesPerLine];
                        int bytePtr = 0;

                        for (y = 0; y < imgHeight; y++)
                        {
                            for (i = 0; i < bytesPerLine; i++)
                                scanlineR[i] = (byte)rleReader.ReadByte();
                            for (i = 0; i < bytesPerLine; i++)
                                scanlineG[i] = (byte)rleReader.ReadByte();
                            for (i = 0; i < bytesPerLine; i++)
                                scanlineB[i] = (byte)rleReader.ReadByte();

                            for (int n = 0; n < imgWidth; n++)
                            {
                                bmpData[bytePtr++] = scanlineB[n];
                                bmpData[bytePtr++] = scanlineG[n];
                                bmpData[bytePtr++] = scanlineR[n];
                                bytePtr++;
                            }
                        }
                    }

                }//bpp

            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing PCX file: " + e.Message);
            }

            theBitmap = new Bitmap((int)imgWidth, (int)imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = theBitmap.LockBits(new Rectangle(0, 0, theBitmap.Width, theBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, imgWidth * 4 * imgHeight);
            theBitmap.UnlockBits(bmpBits);
            return theBitmap;
        }


        /// <summary>
        /// Helper class for reading a run-length encoded stream in a PCX file.
        /// </summary>
        private class RleReader
        {
            private int currentByte = 0;
            private int runLength = 0, runIndex = 0;
            private Stream stream;

            public RleReader(Stream stream)
            {
                this.stream = stream;
            }

            public int ReadByte()
            {
                if (runLength > 0)
                {
                    runIndex++;
                    if (runIndex == (runLength - 1))
                        runLength = 0;
                }
                else
                {
                    currentByte = stream.ReadByte();
                    if (currentByte > 191)
                    {
                        runLength = currentByte - 192;
                        currentByte = stream.ReadByte();
                        if (runLength == 1)
                            runLength = 0;
                        runIndex = 0;
                    }
                }
                return currentByte;
            }
        }


        private static UInt16 LittleEndian(UInt16 val)
        {
            if (BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        private static UInt32 LittleEndian(UInt32 val)
        {
            if (BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }

        private static UInt16 conv_endian(UInt16 val)
        {
            UInt16 temp;
            temp = (UInt16)(val << 8); temp &= 0xFF00; temp |= (UInt16)((val >> 8) & 0xFF);
            return temp;
        }
        private static UInt32 conv_endian(UInt32 val)
        {
            UInt32 temp = (val & 0x000000FF) << 24;
            temp |= (val & 0x0000FF00) << 8;
            temp |= (val & 0x00FF0000) >> 8;
            temp |= (val & 0xFF000000) >> 24;
            return (temp);
        }

    }
}
