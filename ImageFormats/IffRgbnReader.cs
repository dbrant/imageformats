using System;
using SixLabors.ImageSharp;
using System.IO;
using System.Text;
using Bitmap = SixLabors.ImageSharp.Image;

/*

Decoder for IFF RGBN (Impulse Turbo Silver and Imagine) images.

Copyright 2022 Dmitry Brant
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
    /// Handles reading IFF RGBN images.
    /// </summary>
    public static class IffRgbnReader
    {

        /// <summary>
        /// Reads an IFF RGBN image from a file.
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
        /// Reads an IFF RGBN image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            int imgWidth = -1;
            int imgHeight = -1;
            int numPlanes = 0;
            int compressionType = 0;

            BinaryReader reader = new BinaryReader(stream);

            byte[] tempBytes = new byte[65536];

            stream.Read(tempBytes, 0, 4);
            if (Encoding.ASCII.GetString(tempBytes, 0, 4) != "FORM") { throw new ApplicationException("This is not a valid RGBN file."); }

            uint chunkSize = Util.BigEndian(reader.ReadUInt32());

            stream.Read(tempBytes, 0, 4);
            string fileType = Encoding.ASCII.GetString(tempBytes, 0, 4);
            if (fileType != "RGBN" && fileType != "RGB8") { throw new ApplicationException("This is not a valid RGBN file."); }

            bool isRgb8 = fileType == "RGB8";

            while (stream.Position < stream.Length)
            {
                stream.Read(tempBytes, 0, 4);
                string chunkName = Encoding.ASCII.GetString(tempBytes, 0, 4);
                chunkSize = Util.BigEndian(reader.ReadUInt32());

                // if (chunkSize % 2 > 0) { chunkSize++; }

                if (chunkName == "BODY")
                {
                    break;
                }

                if (chunkSize <= tempBytes.Length)
                {
                    stream.Read(tempBytes, 0, (int)chunkSize);
                }
                else
                {
                    stream.Seek(chunkSize, SeekOrigin.Current);
                }

                if (chunkName == "BMHD")
                {
                    imgWidth = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    imgHeight = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 2));

                    numPlanes = tempBytes[8];
                    int maskType = tempBytes[9];
                    compressionType = tempBytes[10];
                    int transparentColor = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 12));
                }
            }

            if (imgWidth == -1 || imgHeight == -1)
            {
                throw new ApplicationException("Invalid format of RGBN file.");
            }

            byte[] bmpData = new byte[(imgWidth + 1) * 4 * imgHeight];

            try
            {
                if (compressionType == 3)
                {
                    int ptr = 0;
                    for (int y = 0; y < imgHeight; y++)
                    {
                        for (int x = 0; x < imgWidth; x++)
                        {
                            stream.Read(tempBytes, 0, 4);
                            uint val = Util.BigEndian(BitConverter.ToUInt32(tempBytes, 0));

                            if (isRgb8)
                            {
                                bmpData[4 * (y * imgWidth + x)] = (byte)((val >> 8) & 0xFF);
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)((val >> 16) & 0xFF);
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)((val >> 24) & 0xFF);
                            }
                            else
                            {
                                bmpData[4 * (y * imgWidth + x)] = (byte)(((val >> 4) & 0xF) * 17);
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)(((val >> 8) & 0xF) * 17);
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)(((val >> 12) & 0xF) * 17);
                            }
                            bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                        }
                    }
                }
                else if (compressionType == 4)
                {
                    var decoder = new RgbnDecoder(stream, isRgb8);

                    int ptr = 0;
                    for (int y = 0; y < imgHeight; y++)
                    {
                        for (int x = 0; x < imgWidth; x++)
                        {
                            uint val = decoder.GetNextValue();

                            if (isRgb8)
                            {
                                bmpData[4 * (y * imgWidth + x)] = (byte)(val & 0xFF);
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)((val >> 8) & 0xFF);
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)((val >> 16) & 0xFF);
                            }
                            else
                            {
                                bmpData[4 * (y * imgWidth + x)] = (byte)((val & 0xF) * 17);
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)(((val >> 4) & 0xF) * 17);
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)(((val >> 8) & 0xF) * 17);
                            }
                            bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                        }
                    }
                }
                else
                {
                    throw new ApplicationException("Invalid compression type.");
                }
            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing RGBN file: " + e.Message);
            }

            var bmp = ImageTool.LoadRgba(imgWidth, imgHeight, bmpData);
            return bmp;
        }

        private class RgbnDecoder
        {
            private readonly Stream stream;
            private bool isRgb8;
            private uint curValue;
            private int curCount = 0;
            private byte[] tempBytes = new byte[4];

            public RgbnDecoder(Stream stream, bool isRgb8)
            {
                this.stream = stream;
                this.isRgb8 = isRgb8;
            }

            public uint GetNextValue()
            {
                if (curCount > 0)
                {
                    curCount--;
                    return curValue;
                }

                if (isRgb8)
                {
                    stream.Read(tempBytes, 0, 4);
                    curValue = Util.BigEndian(BitConverter.ToUInt32(tempBytes, 0));
                    bool genLock = (curValue & 0x80) != 0;
                    curCount = (int)(curValue & 0x7F);
                    curValue >>= 8;
                }
                else
                {
                    stream.Read(tempBytes, 0, 2);
                    curValue = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    bool genLock = (curValue & 0x8) != 0;
                    curCount = (int)(curValue & 0x7);
                    curValue >>= 4;
                }

                if (curCount == 0)
                {
                    curCount = stream.ReadByte();
                    if (curCount == 0)
                    {
                        stream.Read(tempBytes, 0, 2);
                        curCount = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    }
                }
                if (curCount > 0)
                {
                    // Decrement our count, since we're about to return the current value.
                    curCount--;
                }
                return curValue;
            }
        }
    }
}
