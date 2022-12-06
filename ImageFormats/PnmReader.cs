using System;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;

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
        
        private const int MAX_INTS_PER_LINE = 10000;

        /// <summary>
        /// Load a portable picture map (either PPM, PGM, or PBM) into a Bitmap object.
        /// </summary>
        /// <param name="fileName">File name of the picture to load.</param>
        /// <returns>Bitmap that contains the picture.</returns>
        public static Image Load(string fileName)
        {
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return Load(f);
            }
        }

        /// <summary>
        /// Load a portable picture map (either PPM, PGM, or PBM) into a Bitmap object.
        /// </summary>
        /// <param name="stream">Stream from which the picture will be loaded.</param>
        /// <returns>Bitmap that contains the picture.</returns>
        public static Image Load(Stream inStream)
        {
            int bytePtr = 0;
            byte[] bytes = new byte[inStream.Length];
            inStream.Read(bytes, 0, bytes.Length);
            
            int[] lineInts = new int[MAX_INTS_PER_LINE];
            int lineIntsRead;
            char pnmType;
            int bmpWidth = -1, bmpHeight = -1, bmpMaxVal = -1;

            //check if the format is correct...
            if ((char)bytes[bytePtr++] != 'P') throw new ApplicationException("Incorrect file format.");
            pnmType = (char)bytes[bytePtr++];
            if ((pnmType < '1') || (pnmType > '6')) throw new ApplicationException("Unrecognized bitmap type.");

            //if it's monochrome, it won't have a maxval, so set it to 1
            if ((pnmType == '1') || (pnmType == '4')) bmpMaxVal = 1;

            int nextByte = bytes[bytePtr];
            if (nextByte == 0x20 && pnmType >= '4')
            {
                // It's likely space-separated values on the same line, followed by binary data.
                string str = Encoding.ASCII.GetString(bytes, bytePtr, 32);
                string[] strArray = str.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (strArray.Length >= 3)
                {
                    bmpWidth = Convert.ToInt32(strArray[0]);
                    bmpHeight = Convert.ToInt32(strArray[1]);
                    bmpMaxVal = Convert.ToInt32(strArray[2]);

                    string searchStr = " " + bmpMaxVal.ToString() + " ";
                    bytePtr += str.LastIndexOf(searchStr) + searchStr.Length;
                }
            }
            else
            {
                while (bytePtr < bytes.Length)
                {
                    ReadLineOfInts(bytes, ref bytePtr, lineInts, out lineIntsRead);
                    if (lineIntsRead == 0) continue;
                    for (int i = 0; i < lineIntsRead; i++)
                    {
                        if (bmpWidth == -1) { bmpWidth = lineInts[i]; }
                        else if (bmpHeight == -1) { bmpHeight = lineInts[i]; }
                        else if (bmpMaxVal == -1) { bmpMaxVal = lineInts[i]; }
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
                    while (bytePtr < bytes.Length)
                    {
                        ReadLineOfSingleDigitInts(bytes, ref bytePtr, lineInts, out lineIntsRead);
                        for (int i = 0; i < lineIntsRead; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            elementVal = (byte)(lineInts[i] == 0 ? 255 : 0);
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
                    while (bytePtr < bytes.Length)
                    {
                        ReadLineOfInts(bytes, ref bytePtr, lineInts, out lineIntsRead);
                        for (int i = 0; i < lineIntsRead; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            bmpData[elementCount] = (byte)((lineInts[i] * 255) / bmpMaxVal);
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
                    while (bytePtr < bytes.Length)
                    {
                        ReadLineOfInts(bytes, ref bytePtr, lineInts, out lineIntsRead);
                        for (int i = 0; i < lineIntsRead; i++)
                        {
                            if (elementCount >= maxElementCount) break;
                            bmpData[elementCount + elementMod] = (byte)((lineInts[i] * 255) / bmpMaxVal);
                            elementMod--;
                            if (elementMod < 0) { elementCount += 4; elementMod = 2; }
                        }
                        if (elementCount >= maxElementCount) break;
                    }
                }
                else if (pnmType == '4') //monochrome bitmap (binary)
                {
                    byte pixel, pixelVal;
                    int x = 0;
                    int elementCount = 0;
                    while (true)
                    {
                        pixel = bytes[bytePtr++];
                        for (int p = 7; p >= 0; p--)
                        {
                            pixelVal = (byte)((pixel & (1 << p)) == 0 ? 255 : 0);
                            bmpData[elementCount++] = pixelVal;
                            bmpData[elementCount++] = pixelVal;
                            bmpData[elementCount++] = pixelVal;
                            elementCount++;
                            if (elementCount >= maxElementCount) break;
                            x++;
                            if (x >= bmpWidth)
                            {
                                // At the end of the current line, disregard any remaining bits of the current byte.
                                x = 0;
                                break;
                            }
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
                            pixel = bytes[bytePtr++];
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
                            pixel = bytes[bytePtr++];
                            bytePtr++;
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            bmpData[elementCount++] = pixel;
                            elementCount++;
                        }
                    }
                }
                else if (pnmType == '6') //color bitmap (binary)
                {
                    int elementCount = 0;
                    if (bmpMaxVal < 256)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            bmpData[elementCount++] = bytes[bytePtr + 2];
                            bmpData[elementCount++] = bytes[bytePtr + 1];
                            bmpData[elementCount++] = bytes[bytePtr];
                            bytePtr += 3;
                            elementCount++;
                        }
                    }
                    else if (bmpMaxVal < 65536)
                    {
                        for (int i = 0; i < numPixels; i++)
                        {
                            bmpData[elementCount++] = bytes[bytePtr + 4];
                            bmpData[elementCount++] = bytes[bytePtr + 2];
                            bmpData[elementCount++] = bytes[bytePtr];
                            bytePtr += 6;
                            elementCount++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing PNM file: " + e.Message);
            }

            return ImageTool.LoadRgb(bmpWidth, bmpHeight, bmpData);
        }
        
        private static void ReadLineOfInts(byte[] bytes, ref int bytePtr, int[] intArray, out int numIntsRead)
        {
            int currentInt = 0;
            int intIndex = 0;
            bool isComment = false;
            bool intStarted = false;
            byte b;
            do
            {
                b = bytes[bytePtr++];
                
                if (b < '0' || b > '9')
                {
                    // not a digit
                    if (intStarted)
                    {
                        // close out the current int
                        intArray[intIndex++] = currentInt;
                        intStarted = false;
                    }
                }
                else if (!isComment)
                {
                    // digit
                    if (intStarted)
                    {
                        // continue the current int
                        currentInt *= 10;
                        currentInt += b - '0';
                    }
                    else
                    {
                        // start a new int
                        intStarted = true;
                        currentInt = b - '0';
                    }
                }

                if ((b == '\r') || (b == '\n')) { break; }
                else if (b == '#') { isComment = true; }
                
            } while (bytePtr < bytes.Length);
            
            numIntsRead = intIndex;
        }

        private static void ReadLineOfSingleDigitInts(byte[] bytes, ref int bytePtr, int[] intArray, out int numIntsRead)
        {
            int intIndex = 0;
            bool isComment = false;
            byte b;
            do
            {
                b = bytes[bytePtr++];

                if (!isComment && b >= '0' && b <= '9')
                {
                    intArray[intIndex++] = b - '0';
                }

                if ((b == '\r') || (b == '\n')) { break; }
                else if (b == '#') { isComment = true; }

            } while (bytePtr < bytes.Length);

            numIntsRead = intIndex;
        }
    }
}
