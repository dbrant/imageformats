using System;
using System.IO;
using System.Text;
using System.Drawing;

/*

Decoder for MacPaint (*.MAC) images.

Copyright 2019 Dmitry Brant
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
    public static class MacPaintReader
    {
        private const int MAC_PAINT_WIDTH = 576;
        private const int MAC_PAINT_HEIGHT = 720;

        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp = Load(f);
            }
            return bmp;
        }

        public static Bitmap Load(Stream stream)
        {
            byte[] headerBytes = new byte[0x80];
            stream.Read(headerBytes, 0, headerBytes.Length);

            if (headerBytes[0] != 0)
            {
                throw new ApplicationException("This is not a valid MacPaint file.");
            }

            string fileType = Encoding.ASCII.GetString(headerBytes, 0x41, 4);
            if (fileType != "PNTG")
            {
                throw new ApplicationException("This is not a valid MacPaint file.");
            }

            int fileNameLen = headerBytes[1];
            string fileName = Encoding.ASCII.GetString(headerBytes, 2, fileNameLen);

            // Not much other useful stuff in the header...

            stream.Read(headerBytes, 0, 4);
            uint startMagic = Util.BigEndian(BitConverter.ToUInt32(headerBytes, 0));
            if (startMagic != 0x2)
            {
                // I have actually seen MacPaint files that do not have this magic value...
                //throw new ApplicationException("This is not a valid MacPaint file.");
            }

            // Skip over pattern data
            stream.Seek(304, SeekOrigin.Current);

            // Skip over padding data
            stream.Seek(204, SeekOrigin.Current);

            byte[] bmpData = new byte[(MAC_PAINT_WIDTH + 1) * 4 * MAC_PAINT_HEIGHT];
            int x = 0, y = 0;
            RleReader rleReader = new RleReader(stream);

            try
            {
                int b;
                byte curByte;
                int bmpPtr = 0;
                bool val;

                while (bmpPtr < bmpData.Length)
                {
                    curByte = (byte)rleReader.ReadByte();
                    for (b = 7; b >= 0; b--)
                    {
                        val = (curByte & (1 << b)) != 0;

                        bmpData[bmpPtr] = (byte)(val ? 0 : 0xFF);
                        bmpData[bmpPtr + 1] = (byte)(val ? 0 : 0xFF);
                        bmpData[bmpPtr + 2] = (byte)(val ? 0 : 0xFF);
                        bmpPtr += 4;

                        x++;
                        if (x >= MAC_PAINT_WIDTH)
                        {
                            x = 0;
                            y++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file
                System.Diagnostics.Debug.WriteLine("Error while processing MacPaint file: " + e.Message);
            }

            var bmp = new Bitmap(MAC_PAINT_WIDTH, MAC_PAINT_HEIGHT, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, MAC_PAINT_WIDTH * 4 * MAC_PAINT_HEIGHT);
            bmp.UnlockBits(bmpBits);
            return bmp;
        }

        /// <summary>
        /// Helper class for reading a run-length encoded stream in a MacPaint file.
        /// </summary>
        private class RleReader
        {
            private int currentByte = 0;
            private int runLength = 0;
            private bool runType;
            private readonly Stream stream;

            public RleReader(Stream stream)
            {
                this.stream = stream;
            }

            public int ReadByte()
            {
                if (runLength > 0)
                {
                    if (runType)
                    {
                        currentByte = stream.ReadByte();
                    }
                }
                else
                {
                    currentByte = stream.ReadByte();
                    if (currentByte >= 0x80)
                    {
                        runLength = ((~currentByte) & 0xFF) + 2;
                        runType = false;
                    }
                    else
                    {
                        runLength = currentByte + 1;
                        runType = true;
                    }
                    currentByte = stream.ReadByte();
                }
                runLength--;
                return currentByte;
            }
        }
    }
}
