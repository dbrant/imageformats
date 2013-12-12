using System;
using System.Drawing;
using System.IO;

/*

Decoder for Dr. Halo CUT (.CUT) images.
Decodes all CUT images that I've found in the wild.  If you find
one that it fails to decode, let me know!

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
    /// Handles reading Dr. Halo (.CUT) images
    /// </summary>
    public static class CutReader
    {

        /// <summary>
        /// Reads a Dr. Halo (.CUT) image from a file.
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
        /// Reads a Dr. Halo (.CUT) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            BinaryReader reader = new BinaryReader(stream);

            int imgWidth = LittleEndian(reader.ReadUInt16());
            int imgHeight = LittleEndian(reader.ReadUInt16());
            LittleEndian(reader.ReadUInt16()); //reserved word

            if ((imgWidth < 1) || (imgHeight < 1) || (imgWidth > 32767) || (imgHeight > 32767))
                throw new ApplicationException("This CUT file appears to have invalid dimensions.");

            byte[] bmpData = new byte[imgWidth * 4 * imgHeight];

            //create a grayscale color palette, since CUT files don't contain a
            //palette of their own. Replace this if you like...
            byte[] colorPalette = new byte[256];
            for (int i = 0; i < colorPalette.Length; i++)
                colorPalette[i] = (byte)i;

            try
            {
                int x = 0, y = 0;
                int i, j, k, b;
                int lineLen;

                while (y < imgHeight && stream.Position < stream.Length)
                {
                    lineLen = reader.ReadUInt16();

                    while(stream.Position < stream.Length){
                        
                        i = stream.ReadByte();
                        j = i & 0x7F;

                        if (j == 0)
                        {
                            x = 0; y++;
                            break;
                        }
                        if (i > 127)
                        {
                            k = stream.ReadByte();
                            for (b = 0; b < j; b++)
                            {
                                bmpData[4 * (y * imgWidth + x)] = colorPalette[k];
                                bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[k];
                                bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[k];
                                x++;
                            }
                        }
                        else
                        {
                            for (b = 0; b < j; b++)
                            {
                                k = stream.ReadByte();
                                bmpData[4 * (y * imgWidth + x)] = colorPalette[k];
                                bmpData[4 * (y * imgWidth + x) + 1] = colorPalette[k];
                                bmpData[4 * (y * imgWidth + x) + 2] = colorPalette[k];
                                x++;
                            }
                        }
                    }

                }

            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing CUT file: " + e.Message);
            }

            Bitmap theBitmap = new Bitmap((int)imgWidth, (int)imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = theBitmap.LockBits(new Rectangle(0, 0, theBitmap.Width, theBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, bmpData.Length);
            theBitmap.UnlockBits(bmpBits);
            return theBitmap;
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
