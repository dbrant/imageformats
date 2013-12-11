using System;
using System.Drawing;
using System.IO;

/*

Decoder for Sun Raster (.RAS, .SUN) images.
Supports pretty much the full Sun Raster specification (all bit
depths, etc).  At the very least, it decodes all RAS images that
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
    /// Handles reading Sun Raster (.RAS) images
    /// </summary>
    public static class RasReader
    {

        /// <summary>
        /// Reads a Sun Raster (.RAS) image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName){
            Bitmap bmp;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp = Load(f);
            }
            return bmp;
        }

        /// <summary>
        /// Reads a Sun Raster (.RAS) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            BinaryReader reader = new BinaryReader(stream);
            UInt32 tempDword = BigEndian(reader.ReadUInt32());
            if (tempDword != 0x59a66a95)
                throw new ApplicationException("This is not a valid RAS file.");

            int imgWidth = (int)BigEndian(reader.ReadUInt32());
            int imgHeight = (int)BigEndian(reader.ReadUInt32());
            int imgBpp = (int)BigEndian(reader.ReadUInt32());
            UInt32 dataLength = BigEndian(reader.ReadUInt32());
            UInt32 rasType = BigEndian(reader.ReadUInt32());
            UInt32 mapType = BigEndian(reader.ReadUInt32());
            UInt32 mapLength = BigEndian(reader.ReadUInt32());

            RleReader rleReader = new RleReader(stream, rasType == 2);

            if ((imgWidth < 1) || (imgHeight < 1) || (imgWidth > 32767) || (imgHeight > 32767) || (mapLength > 32767))
                throw new ApplicationException("This RAS file appears to have invalid dimensions.");

            if ((imgBpp != 32) && (imgBpp != 24) && (imgBpp != 8) && (imgBpp != 4) && (imgBpp != 1))
                throw new ApplicationException("Only 1, 4, 8, 24, and 32 bit images are supported.");

            byte[] bmpData = new byte[imgWidth * 4 * imgHeight];

            byte[] colorPalette = null;
            if (mapType > 0)
            {
                colorPalette = new byte[mapLength];
                stream.Read(colorPalette, 0, (int)mapLength);
            }

            try
            {
                if (imgBpp == 1)
                {
                    int dx = 0, dy = 0;
                    int b, bytePtr = 0;
                    byte val;
                    while (dy < imgHeight)
                    {
                        b = rleReader.ReadByte();
                        if (b == -1) break;
                        for (int i = 7; i >= 0; i--)
                        {
                            if ((b & (1 << i)) != 0) val = 0; else val = 255;
                            bmpData[bytePtr++] = val;
                            bmpData[bytePtr++] = val;
                            bmpData[bytePtr++] = val;
                            bytePtr++;

                            dx++;
                            if (dx == imgWidth)
                            {
                                dx = 0; dy++;
                                break;
                            }
                        }
                    }
                }
                else if (imgBpp == 4)
                {
                    int bytePtr = 0;
                    byte[] scanline = new byte[imgWidth + 1];
                    for (int dy = 0; dy < imgHeight; dy++)
                    {
                        int tempByte;
                        for (int i = 0; i < imgWidth; i++)
                        {
                            tempByte = rleReader.ReadByte();
                            scanline[i++] = (byte)((tempByte >> 4) & 0xF);
                            scanline[i] = (byte)(tempByte & 0xF);
                        }

                        if (imgWidth % 2 == 1) rleReader.ReadByte();

                        if ((mapType > 0) && (mapLength == 48))
                        {
                            for (int dx = 0; dx < imgWidth; dx++)
                            {
                                bmpData[bytePtr++] = colorPalette[scanline[dx] + 32];
                                bmpData[bytePtr++] = colorPalette[scanline[dx] + 16];
                                bmpData[bytePtr++] = colorPalette[scanline[dx]];
                                bytePtr++;
                            }
                        }
                        else
                        {
                            for (int dx = 0; dx < imgWidth; dx++)
                            {
                                bmpData[bytePtr++] = scanline[dx];
                                bmpData[bytePtr++] = scanline[dx];
                                bmpData[bytePtr++] = scanline[dx];
                                bytePtr++;
                            }
                        }
                    }
                }
                else if (imgBpp == 8)
                {
                    int bytePtr = 0;
                    byte[] scanline = new byte[imgWidth];
                    for (int dy = 0; dy < imgHeight; dy++)
                    {
                        for (int i = 0; i < imgWidth; i++)
                            scanline[i] = (byte)rleReader.ReadByte();

                        if (imgWidth % 2 == 1) rleReader.ReadByte();

                        if ((mapType > 0) && (mapLength == 768))
                        {
                            for (int dx = 0; dx < imgWidth; dx++)
                            {
                                bmpData[bytePtr++] = colorPalette[scanline[dx] + 512];
                                bmpData[bytePtr++] = colorPalette[scanline[dx] + 256];
                                bmpData[bytePtr++] = colorPalette[scanline[dx]];
                                bytePtr++;
                            }
                        }
                        else
                        {
                            for (int dx = 0; dx < imgWidth; dx++)
                            {
                                bmpData[bytePtr++] = scanline[dx];
                                bmpData[bytePtr++] = scanline[dx];
                                bmpData[bytePtr++] = scanline[dx];
                                bytePtr++;
                            }
                        }
                    }
                }
                else if (imgBpp == 24)
                {
                    int bytePtr = 0;
                    byte[] scanline = new byte[imgWidth * 3];
                    for (int dy = 0; dy < imgHeight; dy++)
                    {
                        for (int i = 0; i < imgWidth * 3; i++)
                            scanline[i] = (byte)rleReader.ReadByte();

                        if ((imgWidth * 3) % 2 == 1) stream.ReadByte();

                        for (int dx = 0; dx < imgWidth; dx++)
                        {
                            bmpData[bytePtr++] = scanline[dx * 3];
                            bmpData[bytePtr++] = scanline[dx * 3 + 1];
                            bmpData[bytePtr++] = scanline[dx * 3 + 2];
                            bytePtr++;
                        }
                    }
                }
                else if (imgBpp == 32)
                {
                    int bytePtr = 0;
                    byte[] scanline = new byte[imgWidth * 4];
                    for (int dy = 0; dy < imgHeight; dy++)
                    {
                        for (int i = 0; i < imgWidth * 4; i++)
                            scanline[i] = (byte)rleReader.ReadByte();

                        for (int dx = 0; dx < imgWidth; dx++)
                        {
                            bmpData[bytePtr++] = scanline[dx * 4];
                            bmpData[bytePtr++] = scanline[dx * 4 + 1];
                            bmpData[bytePtr++] = scanline[dx * 4 + 2];
                            bmpData[bytePtr++] = scanline[dx * 4 + 3];
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing RAS file: " + e.Message);
            }

            Bitmap theBitmap = new Bitmap((int)imgWidth, (int)imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = theBitmap.LockBits(new Rectangle(0, 0, theBitmap.Width, theBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, bmpData.Length);
            theBitmap.UnlockBits(bmpBits);
            return theBitmap;
        }

        /// <summary>
        /// Helper class for reading a run-length encoded stream in a RAS file.
        /// </summary>
        private class RleReader
        {
            private int currentByte = 0;
            private int runLength = 0, runIndex = 0;
            private Stream stream;
            private bool isRle;

            public RleReader(Stream stream, bool isRle)
            {
                this.stream = stream;
                this.isRle = isRle;
            }

            public int ReadByte()
            {
                if (!isRle)
                    return stream.ReadByte();

                if (runLength > 0)
                {
                    runIndex++;
                    if (runIndex == (runLength - 1))
                        runLength = 0;
                }
                else
                {
                    currentByte = stream.ReadByte();
                    if (currentByte == 0x80)
                    {
                        currentByte = stream.ReadByte();
                        if (currentByte == 0)
                        {
                            currentByte = 0x80;
                        }
                        else
                        {
                            runLength = currentByte + 1;
                            runIndex = 0;
                            currentByte = stream.ReadByte();
                        }
                    }
                }
                return currentByte;
            }
        }


        private static UInt16 BigEndian(UInt16 val)
        {
            if (!BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        private static UInt32 BigEndian(UInt32 val)
        {
            if (!BitConverter.IsLittleEndian) return val;
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
