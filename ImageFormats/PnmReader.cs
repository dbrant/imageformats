using System;
using System.IO;
using System.Text;
using System.Drawing;

/*

Decoder for Netpbm (.PPM, .PGM, .PBM, .PNM) images.
Supports pretty much the full range of Netpbm images.
At the very least, it decodes all Netpbm images that
I've found in the wild.  If you find one that it fails to decode,
let me know!

Copyright 2013-2018 Dmitry Brant
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
    /// Handles reading portable bitmap (.PNM, .PBM, .PGM, .PPM) images.
    /// </summary>
    public static class PnmReader
    {

        /// <summary>
        /// Load a portable picture map (either PPM, PGM, or PBM) into a Bitmap object.
        /// </summary>
        /// <param name="fileName">File name of the picture to load.</param>
        /// <returns>Bitmap that contains the picture.</returns>
        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp = Load(f);
            }
            return bmp;
        }

        /// <summary>
        /// Load a portable picture map (either PPM, PGM, or PBM) into a Bitmap object.
        /// </summary>
        /// <param name="stream">Stream from which the picture will be loaded.</param>
        /// <returns>Bitmap that contains the picture.</returns>
        public static Bitmap Load(Stream stream)
        {
            Bitmap bmp = null;
            string line;
            string[] lineArray;
            char pnmType;
            int bmpWidth = -1, bmpHeight = -1, bmpMaxVal = -1;

            //check if the format is correct...
            if ((char)stream.ReadByte() != 'P') throw new ApplicationException("Incorrect file format.");
            pnmType = (char)stream.ReadByte();
            if ((pnmType < '1') || (pnmType > '6')) throw new ApplicationException("Unrecognized bitmap type.");

            //if it's monochrome, it won't have a maxval, so set it to 1
            if ((pnmType == '1') || (pnmType == '4')) bmpMaxVal = 1;

            int nextByte = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.Current);
            if (nextByte == 0x20)
            {
                // It's likely space-separated values on the same line, followed by binary data.
                byte[] bytes = new byte[32];
                stream.Read(bytes, 0, bytes.Length);
                stream.Seek(-bytes.Length, SeekOrigin.Current);
                string str = Encoding.ASCII.GetString(bytes);
                string[] strArray = str.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (strArray.Length >= 3)
                {
                    bmpWidth = Convert.ToInt32(strArray[0]);
                    bmpHeight = Convert.ToInt32(strArray[1]);
                    bmpMaxVal = Convert.ToInt32(strArray[2]);

                    string searchStr = " " + bmpMaxVal.ToString() + " ";
                    stream.Seek(str.LastIndexOf(searchStr) + searchStr.Length, SeekOrigin.Current);
                }
            }
            else
            {
                while (stream.Position < stream.Length)
                {
                    line = ReadLine(stream);
                    if (line.Length == 0) continue;
                    if (line[0] == '#') continue;
                    lineArray = line.Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
                    if (lineArray.Length == 0) continue;

                    for (int i = 0; i < lineArray.Length; i++)
                    {
                        if (bmpWidth == -1) { bmpWidth = Convert.ToInt32(lineArray[i]); }
                        else if (bmpHeight == -1) { bmpHeight = Convert.ToInt32(lineArray[i]); }
                        else if (bmpMaxVal == -1) { bmpMaxVal = Convert.ToInt32(lineArray[i]); }
                    }

                    //check if we have all necessary attributes
                    if ((bmpWidth != -1) && (bmpHeight != -1) && (bmpMaxVal != -1))
                        break;
                }
            }

            //check for nonsensical dimensions
            if ((bmpWidth <= 0) || (bmpHeight <= 0) || (bmpMaxVal <= 0))
                throw new ApplicationException("Invalid image dimensions.");

            int numPixels = bmpWidth * bmpHeight;
            int maxElementCount = numPixels * 4;
            var bmpData = new byte[maxElementCount];

            try
            {
                if (pnmType == '1') //monochrome bitmap (ascii)
                {
                    int elementCount = 0;
                    byte elementVal;
                    while (stream.Position < stream.Length)
                    {
                        line = ReadLine(stream);
                        if (line.Length == 0) continue;
                        if (line[0] == '#') continue;

                        lineArray = line.Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < lineArray.Length; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            elementVal = (byte)(lineArray[i] == "0" ? 255 : 0);
                            bmpData[elementCount] = elementVal;
                            bmpData[elementCount + 1] = elementVal;
                            bmpData[elementCount + 2] = elementVal;
                            elementCount += 4;
                        }
                        if (elementCount >= maxElementCount) break;
                    }
                }
                else if (pnmType == '2') //grayscale bitmap (ascii)
                {
                    int elementCount = 0;
                    int elementVal;
                    while (stream.Position < stream.Length)
                    {
                        line = ReadLine(stream);
                        if (line.Length == 0) continue;
                        if (line[0] == '#') continue;

                        lineArray = line.Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < lineArray.Length; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            elementVal = Convert.ToInt32(lineArray[i]);
                            bmpData[elementCount] = (byte)((elementVal * 255) / bmpMaxVal);
                            bmpData[elementCount + 1] = bmpData[elementCount];
                            bmpData[elementCount + 2] = bmpData[elementCount];
                            elementCount += 4;
                        }
                        if (elementCount >= maxElementCount) break;
                    }
                }
                else if (pnmType == '3') //color bitmap (ascii)
                {
                    int elementCount = 0, elementMod = 2;
                    int elementVal;
                    while (stream.Position < stream.Length)
                    {
                        line = ReadLine(stream);
                        if (line.Length == 0) continue;
                        if (line[0] == '#') continue;

                        lineArray = line.Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < lineArray.Length; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            elementVal = Convert.ToInt32(lineArray[i]);
                            bmpData[elementCount + elementMod] = (byte)((elementVal * 255) / bmpMaxVal);
                            elementMod--;
                            if (elementMod < 0) { elementCount += 4; elementMod = 2; }
                        }
                        if (elementCount >= maxElementCount) break;
                    }
                }
                else if (pnmType == '4') //monochrome bitmap (binary)
                {
                    byte pixel, pixelVal;
                    int elementCount = 0;
                    while (true)
                    {
                        pixel = (byte)stream.ReadByte();
                        for (int p = 7; p >= 0; p--)
                        {
                            pixelVal = (byte)((pixel & (1 << p)) == 0 ? 255 : 0);
                            bmpData[elementCount++] = pixelVal;
                            bmpData[elementCount++] = pixelVal;
                            bmpData[elementCount++] = pixelVal;
                            elementCount++;
                            if (elementCount >= maxElementCount) break;
                        }
                        if (elementCount >= maxElementCount) break;
                    }
                }
                else if (pnmType == '5') //grayscale bitmap (binary)
                {
                    byte pixel;
                    int elementCount = 0;
                    if (bmpMaxVal < 256)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            pixel = (byte)stream.ReadByte();
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            elementCount++;
                        }
                    }
                    else if (bmpMaxVal < 65536)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            pixel = (byte)stream.ReadByte();
                            stream.ReadByte();
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            elementCount++;
                        }
                    }
                }
                else if (pnmType == '6') //color bitmap (binary)
                {
                    byte[] pixel = new byte[16];
                    int elementCount = 0;
                    if (bmpMaxVal < 256)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            stream.Read(pixel, 0, 3);
                            bmpData[elementCount++] = pixel[2];
                            bmpData[elementCount++] = pixel[1];
                            bmpData[elementCount++] = pixel[0];
                            elementCount++;
                        }
                    }
                    else if (bmpMaxVal < 65536)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            stream.Read(pixel, 0, 6);
                            bmpData[elementCount++] = pixel[4];
                            bmpData[elementCount++] = pixel[2];
                            bmpData[elementCount++] = pixel[0];
                            elementCount++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing PNM file: " + e.Message);
            }

            bmp = new Bitmap(bmpWidth, bmpHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, bmpData.Length);
            bmp.UnlockBits(bmpBits);
            return bmp;
        }


        private static char[] whitespace = { ' ', '\t', '\r', '\n' };

        private static string ReadLine(Stream stream)
        {
            string str = "";
            byte[] lineBytes = new byte[1024];
            int startPos = (int)stream.Position;
            stream.Read(lineBytes, 0, 1024);
            int strLen = 0;
            while (strLen < 1024)
            {
                if ((lineBytes[strLen] == '\r') || (lineBytes[strLen] == '\n')) { strLen++; break; }
                strLen++;
            }
            if (strLen > 1)
                str = Encoding.ASCII.GetString(lineBytes, 0, strLen - 1);

            stream.Position = startPos + strLen;
            return str;
        }

    }
}
