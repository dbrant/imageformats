using System;
using SixLabors.ImageSharp;
using System.IO;
using System.Text;
using Bitmap = SixLabors.ImageSharp.Image;

/*

Decoder for FITS (Flexible Image Transport System) images.

Copyright 2019 Dmitry Brant.
https://dmitrybrant.com

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
    /// Handles reading FITS (Flexible Image Transport System) images
    /// </summary>
    public static class FitsReader
    {
        public const int HEADER_BLOCK_LENGTH = 2880;
        public const int HEADER_ITEM_LENGTH = 80;

        /// <summary>
        /// Reads a FITS (Flexible Image Transport System) image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName)
        {
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Load(f);
            }
        }

        /// <summary>
        /// Reads a FITS (Flexible Image Transport System) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            byte[] tempBytes = new byte[HEADER_ITEM_LENGTH];
            Bitmap bmp = null;
            int maxHeaderItems = 1000;

            string itemStr;
            int bitsPerPixel = 0;
            int numAxes = 0;
            int[] axisLength = new int[16];
            float[] dataMin = new float[8];
            float[] dataMax = new float[8];
            float dataMean = 0;

            for (int headerSeq = 0; headerSeq < 100; headerSeq++)
            {
                for (int i = 0; i < maxHeaderItems; i++)
                {
                    stream.Read(tempBytes, 0, HEADER_ITEM_LENGTH);

                    itemStr = Encoding.ASCII.GetString(tempBytes, 0, HEADER_ITEM_LENGTH);
                    if (itemStr.IndexOf('/') > 0)
                    {
                        itemStr = itemStr.Substring(0, itemStr.IndexOf('/'));
                    }
                    itemStr = itemStr.Trim();
                    if (itemStr == "END") { break; }

                    if (i == 0)
                    {
                        if (headerSeq == 0 && !itemStr.StartsWith("SIMPLE")) { return bmp; }
                        else if (headerSeq > 0 && !itemStr.StartsWith("XTENSION")) { return bmp; }
                    }

                    if (!itemStr.Contains("=")) { continue; }

                    string[] parts = itemStr.Split('=');
                    if (parts.Length < 2) { continue; }
                    parts[0] = parts[0].Trim();
                    parts[1] = parts[1].Trim();

                    try
                    {
                        if (parts[0] == "BITPIX") { int.TryParse(parts[1], out bitsPerPixel); }
                        else if (parts[0] == "NAXIS") { int.TryParse(parts[1], out numAxes); }
                        else if (parts[0] == "NAXIS1") { int.TryParse(parts[1], out axisLength[0]); }
                        else if (parts[0] == "NAXIS2") { int.TryParse(parts[1], out axisLength[1]); }
                        else if (parts[0] == "NAXIS3") { int.TryParse(parts[1], out axisLength[2]); }
                        else if (parts[0] == "NAXIS4") { int.TryParse(parts[1], out axisLength[3]); }
                        else if (parts[0] == "NAXIS5") { int.TryParse(parts[1], out axisLength[4]); }
                        else if (parts[0] == "DATAMIN") { Util.TryParseFloat(parts[1], out dataMin[0]); for (int m = 1; m < dataMin.Length; m++) { dataMin[m] = dataMin[0]; } }
                        else if (parts[0] == "DATAMAX") { Util.TryParseFloat(parts[1], out dataMax[0]); for (int m = 1; m < dataMax.Length; m++) { dataMax[m] = dataMax[0]; } }
                        else if (parts[0] == "GOODMIN") { Util.TryParseFloat(parts[1], out dataMin[0]); for (int m = 1; m < dataMin.Length; m++) { dataMin[m] = dataMin[0]; } }
                        else if (parts[0] == "GOODMAX") { Util.TryParseFloat(parts[1], out dataMax[0]); for (int m = 1; m < dataMax.Length; m++) { dataMax[m] = dataMax[0]; } }
                        else if (parts[0] == "DATAMEAN") { Util.TryParseFloat(parts[1], out dataMean); }
                        else if (parts[0] == "SI-LMIN1") { Util.TryParseFloat(parts[1], out dataMin[0]); }
                        else if (parts[0] == "SI-LMAX1") { Util.TryParseFloat(parts[1], out dataMax[0]); }
                        else if (parts[0] == "SI-LMIN2") { Util.TryParseFloat(parts[1], out dataMin[1]); }
                        else if (parts[0] == "SI-LMAX2") { Util.TryParseFloat(parts[1], out dataMax[1]); }
                        else if (parts[0] == "SI-LMIN3") { Util.TryParseFloat(parts[1], out dataMin[2]); }
                        else if (parts[0] == "SI-LMAX3") { Util.TryParseFloat(parts[1], out dataMax[2]); }
                    }
                    catch
                    {
                        // ignore any conversion/format errors.
                    }
                }

                if (stream.Position % HEADER_BLOCK_LENGTH > 0)
                {
                    stream.Seek(HEADER_BLOCK_LENGTH - (stream.Position % HEADER_BLOCK_LENGTH), SeekOrigin.Current);
                }

                for (int m = 0; m < dataMax.Length; m++)
                {
                    if (dataMax[m] == 0f) { dataMax[m] = 1f; }
                    if (dataMean > 0 && dataMax[m] > dataMean)
                    {
                        float s = (float)Math.Sqrt(dataMax[m]);
                        if (s > dataMean) { dataMax[m] = s; }

                        //float ms = dataMean > 2 ? dataMean * dataMean : dataMean * 2;
                        //if (ms < dataMax[m]) { dataMax[m] = ms; }

                        //dataMax[m] = (float)Math.Sqrt(dataMax[m] * dataMax[m] - dataMean * dataMean);
                        //dataMax[m] = dataMin[m] + 2 * dataMean;
                    }
                }

                int bytesPerPixel = Math.Abs(bitsPerPixel) / 8;

                long dataSize = 0;

                if (numAxes > 0)
                {
                    dataSize = bytesPerPixel;
                    for (int a = 0; a < numAxes; a++)
                    {
                        dataSize *= axisLength[a];
                    }
                }

                long prevPos = stream.Position;

                // bail if we can't make an image out of it.
                if (bmp != null
                    || numAxes < 2 || numAxes > 4
                    || (numAxes == 2 && (axisLength[0] < 16 || axisLength[0] > 5000 || axisLength[1] < 16 || axisLength[1] > 5000))
                    || (numAxes == 3 && (axisLength[0] < 16 || axisLength[0] > 5000 || axisLength[1] < 16 || axisLength[1] > 5000 || (axisLength[2] != 3 && axisLength[2] != 4))))
                {
                    // ignore this data block.
                }
                else
                {
                    bmp = LoadImageData(stream, bitsPerPixel, numAxes, axisLength[0], axisLength[1], axisLength[2], dataMin, dataMax);
                }

                stream.Seek(prevPos + dataSize, SeekOrigin.Begin);

                // again, align to the next block
                if (stream.Position % HEADER_BLOCK_LENGTH > 0)
                {
                    stream.Seek(HEADER_BLOCK_LENGTH - (stream.Position % HEADER_BLOCK_LENGTH), SeekOrigin.Current);
                }
            }
            return bmp;
        }

        private static Bitmap LoadImageData(Stream stream, int bitsPerPixel, int numAxes, int width, int height, int depth, float[] dataMin, float[] dataMax)
        {
            byte[] bmpData = null;
            float f;

            var reader = new ElementReader(stream, bitsPerPixel);

            try
            {
                if (numAxes == 2)
                {
                    bmpData = new byte[4 * width * height];

                    for (int y = height - 1; y >= 0; y--)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            f = reader.ReadElement();
                            f = (f - dataMin[0]) / (dataMax[0] - dataMin[0]);

                            f = Math.Min(f *= 255, 255);
                            bmpData[4 * (y * width + x)] = (byte)f;
                            bmpData[4 * (y * width + x) + 1] = (byte)f;
                            bmpData[4 * (y * width + x) + 2] = (byte)f;
                        }
                    }
                }
                else if (numAxes == 3 || numAxes == 4)
                {
                    bmpData = new byte[4 * width * height];

                    if (depth == 3)
                    {
                        // very likely an RGB image

                        for (int y = height - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                f = reader.ReadElement();

                                f = (f - dataMin[0]) / (dataMax[0] - dataMin[0]);
                                f = Math.Min(f *= 255, 255);

                                bmpData[4 * (y * width + x) + 2] = (byte)f;
                            }
                        }
                        for (int y = height - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                f = reader.ReadElement();

                                f = (f - dataMin[1]) / (dataMax[1] - dataMin[1]);
                                f = Math.Min(f *= 255, 255);

                                bmpData[4 * (y * width + x) + 1] = (byte)f;
                            }
                        }
                        for (int y = height - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                f = reader.ReadElement();

                                f = (f - dataMin[2]) / (dataMax[2] - dataMin[2]);
                                f = Math.Min(f *= 255, 255);

                                bmpData[4 * (y * width + x)] = (byte)f;
                            }
                        }
                    }
                    else
                    {
                        for (int y = height - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                f = reader.ReadElement();

                                f = (f - dataMin[0]) / (dataMax[0] - dataMin[0]);
                                f = Math.Min(f * 255, 255);

                                bmpData[4 * (y * width + x)] = (byte)f;
                                bmpData[4 * (y * width + x) + 1] = (byte)f;
                                bmpData[4 * (y * width + x) + 2] = (byte)f;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing FITS file: " + e.Message);
            }

            if (bmpData == null) { return null; }

            var theBitmap = ImageTool.LoadRgb(width, height, bmpData);
            return theBitmap;
        }

        private static float ReadSingle(byte[] bytes)
        {
            byte b = bytes[0]; bytes[0] = bytes[3]; bytes[3] = b;
            b = bytes[1]; bytes[1] = bytes[2]; bytes[2] = b;
            return BitConverter.ToSingle(bytes, 0);
        }

        private class ElementReader
        {
            private Stream stream;
            private int bitsPerElement;
            private byte[] bytes;

            public ElementReader(Stream stream, int bitsPerElement)
            {
                this.stream = stream;
                this.bitsPerElement = bitsPerElement;
                bytes = new byte[16];
            }

            public float ReadElement()
            {
                if (bitsPerElement == -64)
                {
                    stream.Read(bytes, 0, 8);
                    byte b = bytes[0]; bytes[0] = bytes[7]; bytes[7] = b;
                    b = bytes[1]; bytes[1] = bytes[6]; bytes[6] = b;
                    b = bytes[2]; bytes[2] = bytes[5]; bytes[5] = b;
                    b = bytes[3]; bytes[3] = bytes[4]; bytes[4] = b;
                    return (float)BitConverter.ToDouble(bytes, 0);
                }
                else if (bitsPerElement == -32)
                {
                    stream.Read(bytes, 0, 4);
                    byte b = bytes[0]; bytes[0] = bytes[3]; bytes[3] = b;
                    b = bytes[1]; bytes[1] = bytes[2]; bytes[2] = b;
                    return BitConverter.ToSingle(bytes, 0);
                }
                else if (bitsPerElement == 32)
                {
                    stream.Read(bytes, 0, 4);
                    return (int)Util.BigEndian(BitConverter.ToUInt32(bytes, 0));
                }
                else if (bitsPerElement == 16)
                {
                    stream.Read(bytes, 0, 2);
                    return (short)Util.BigEndian(BitConverter.ToUInt16(bytes, 0));
                }
                else if (bitsPerElement == 8)
                {
                    stream.Read(bytes, 0, 1);
                    return bytes[0];
                }
                return 0;
            }
        }
    }
}
