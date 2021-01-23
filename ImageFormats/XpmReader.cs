using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Collections.Generic;

/*

Decoder for XPM icons.
Handles a good majority of icons, but will probably stumble on icons
that use custom color names (instead of hex values).

Copyright 2016 Dmitry Brant
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
    /// Handles reading XPM icons.
    /// </summary>
    public static class XpmReader
    {
        private static char[] whitespace = { ' ', '\t', '\r', '\n' };
        private static char[] whitespacequote = { ' ', '\t', '\r', '\n', '"' };

        /// <summary>
        /// Reads an XPM image from a file.
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
        /// Load an XPM icon into a Bitmap object.
        /// </summary>
        /// <param name="stream">Stream from which the picture will be loaded.</param>
        /// <returns>Bitmap that contains the picture, or null if loading failed.</returns>
        public static Bitmap Load(Stream stream)
        {
            var colorDict = new Dictionary<string, UInt32>();

            string str;
            string[] strArray;

            str = ReadUntil(stream, '"');
            str = ReadUntil(stream, '"');

            strArray = str.Split(whitespacequote, StringSplitOptions.RemoveEmptyEntries);
            if (strArray.Length < 4)
            {
                throw new ApplicationException("Invalid file format.");
            }

            int bmpWidth = Convert.ToInt32(strArray[0]);
            int bmpHeight = Convert.ToInt32(strArray[1]);
            int numColors = Convert.ToInt32(strArray[2]);
            int charsPerPixel = Convert.ToInt32(strArray[3]);

            //check for nonsensical dimensions
            if ((bmpWidth <= 0) || (bmpHeight <= 0) || (numColors <= 0) || (charsPerPixel <= 0))
            {
                throw new ApplicationException("Invalid image dimensions.");
            }

            string sampleChar, sampleValue;
            uint intColor;
            ulong longColor;
            for (int i = 0; i < numColors; i++)
            {
                str = ReadUntil(stream, '"');
                str = ReadUntil(stream, '"');

                sampleChar = str.Substring(0, charsPerPixel);
                strArray = str.Split(whitespacequote, StringSplitOptions.RemoveEmptyEntries);
                
                sampleValue = strArray[strArray.Length - 1];
                if (sampleValue.ToLower().Contains("none"))
                {
                    intColor = 0x0;
                }
                else if (sampleValue.StartsWith("#"))
                {
                    sampleValue = sampleValue.Replace("#", "");
                    longColor = Convert.ToUInt64(sampleValue, 16);
                    if (sampleValue.Length > 6)
                    {
                        intColor = 0xFF000000;
                        intColor |= (UInt32)((longColor & 0xFF0000000000) >> 24);
                        intColor |= (UInt32)((longColor & 0xFF000000) >> 16);
                        intColor |= (UInt32)((longColor & 0xFF00) >> 8);
                    }
                    else { intColor = (UInt32)longColor | 0xFF000000; }
                }
                else
                {
                    intColor = (uint) Color.FromName(sampleValue).ToArgb();
                }
                colorDict.Add(sampleChar, intColor);
            }

            int numPixels = bmpWidth * bmpHeight;
            int elementCount = 0, strIndex;
            int maxElementCount = numPixels * 4;
            var bmpData = new byte[maxElementCount];

            try
            {
                while (stream.Position < stream.Length)
                {
                    str = ReadUntil(stream, '"');
                    str = ReadUntil(stream, '"');
                    strIndex = 0;

                    while (strIndex < str.Length - 1)
                    {
                        intColor = colorDict[str.Substring(strIndex, charsPerPixel)];
                        strIndex += charsPerPixel;

                        bmpData[elementCount++] = (byte)(intColor & 0xFF);
                        bmpData[elementCount++] = (byte)((intColor & 0xFF00) >> 8);
                        bmpData[elementCount++] = (byte)((intColor & 0xFF0000) >> 16);
                        bmpData[elementCount++] = (byte)((intColor & 0xFF000000) >> 24);

                        if (elementCount >= maxElementCount)
                        {
                            break;
                        }
                    }

                    if (elementCount >= maxElementCount)
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file
                System.Diagnostics.Debug.WriteLine("Error while processing XPM file: " + e.Message);
            }

            var bmp = new Bitmap(bmpWidth, bmpHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bmpBits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, bmpData.Length);
            bmp.UnlockBits(bmpBits);
            return bmp;
        }
        
        private static string ReadUntil(Stream stream, char stopChar)
        {
            string str = "";
            char c;
            int numRead = 0;
            while (stream.Position < stream.Length)
            {
                c = (char)stream.ReadByte();
                str += c;
                if (c == stopChar)
                {
                    break;
                }
                numRead++;
                if (numRead > 4096)
                {
                    break;
                }
            }
            return str;
        }
    }
}
