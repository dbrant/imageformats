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

Copyright 2013 by Dmitry Brant.
You may use this source code in your application(s) free of charge,
as long as attribution is given to me (Dmitry Brant) and my URL
(http://dmitrybrant.com) in your application's "about" box and/or
documentation. Of course, donations are always welcome:
http://dmitrybrant.com/donate

If you would like to use this source code without attribution, please
contact me through http://dmitrybrant.com, or visit this page:
http://dmitrybrant.com/noattributionlicense

-----------------------------------------------------------
Full License Agreement for this source code module:

"Author" herein shall refer to Dmitry Brant. "Software" shall refer
to this source code module.
This software is supplied to you by the Author in consideration of
your agreement to the following terms, and your use, installation,
modification or redistribution of this software constitutes acceptance
of these terms. If you do not agree with these terms, please do not use,
install, modify or redistribute this software.

In consideration of your agreement to abide by the following terms,
and subject to these terms, the Author grants you a personal,
non-exclusive license, to use, reproduce, modify and redistribute
the software, with or without modifications, in source and/or binary
forms; provided that if you redistribute the software in its entirety
and without modifications, you must retain this notice and the following
text and disclaimers in all such redistributions of the software, and
that in all cases attribution of the Author as the original author
of the source code shall be included in all such resulting software
products or distributions. Neither the name, trademarks, service marks
or logos of the Author may be used to endorse or promote products
derived from the software without specific prior written permission
from the Author. Except as expressly stated in this notice, no other
rights or licenses, express or implied, are granted by the Author
herein, including but not limited to any patent rights that may be
infringed by your derivative works or by other works in which the 
oftware may be incorporated.

The software is provided by the Author on an "AS IS" basis. THE AUTHOR
MAKES NO WARRANTIES, EXPRESS OR IMPLIED, INCLUDING WITHOUT
LIMITATION THE IMPLIED WARRANTIES OF NON-INFRINGEMENT, MERCHANTABILITY
AND FITNESS FOR A PARTICULAR PURPOSE, REGARDING THE SOFTWARE OR ITS USE
AND OPERATION ALONE OR IN COMBINATION WITH YOUR PRODUCTS.

IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, INDIRECT,
INCIDENTAL OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) ARISING IN ANY WAY OUT OF THE USE,
REPRODUCTION, MODIFICATION AND/OR DISTRIBUTION OF THE SOFTWARE, HOWEVER
CAUSED AND WHETHER UNDER THEORY OF CONTRACT, TORT (INCLUDING NEGLIGENCE),
STRICT LIABILITY OR OTHERWISE, EVEN IF THE AUTHOR HAS BEEN ADVISED
OF THE POSSIBILITY OF SUCH DAMAGE.
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

            while (stream.Position < stream.Length)
            {
                line = ReadLine(stream);
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;
                lineArray = line.Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
                if(lineArray.Length == 0) continue;

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
