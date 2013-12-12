using System;
using System.Drawing;
using System.IO;
using System.Text;

/*

This is a class library that contains image decoders for old and/or
obscure image formats (.TGA, .PCX, .PPM, RAS, etc.). Refer to the
individual source code files for each image type for more information.

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

    public static class Picture
    {

        /// <summary>
        /// Load a file into a standard Bitmap object. Will automatically
        /// detect the format of the image.
        /// </summary>
        /// <param name="fileName">Name of the file to load.</param>
        /// <returns>Bitmap that contains the decoded image, or null if it could
        /// not be decoded by any of the formats known to this library.</returns>
        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (FileStream f = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                bmp = Load(f);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("tga"))
                    bmp = TgaReader.Load(fileName);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("cut"))
                    bmp = CutReader.Load(fileName);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("sgi") || Path.GetExtension(fileName).ToLower().Contains("rgb") || Path.GetExtension(fileName).ToLower().Contains("bw"))
                    bmp = SgiReader.Load(fileName);
            }

            return bmp;
        }

        /// <summary>
        /// Create a standard Bitmap object from a Stream. Will automatically
        /// detect the format of the image.
        /// </summary>
        /// <param name="stream">Stream from which the image will be read.</param>
        /// <returns>Bitmap that contains the decoded image, or null if it could
        /// not be decoded by any of the formats known to this library.</returns>
        public static Bitmap Load(Stream stream)
        {
            Bitmap bmp = null;

            //read the first few bytes of the file to determine what format it is...
            byte[] header = new byte[256];
            stream.Read(header, 0, header.Length);
            stream.Seek(0, SeekOrigin.Begin);

            if ((header[0] == 0xA) && (header[1] == 0x5) && (header[2] == 0x1) && (header[4] == 0) && (header[5] == 0))
            {
                bmp = PcxReader.Load(stream);
            }
            else if ((header[0] == 'P') && ((header[1] >= '1') && (header[1] <= '6')) && ((header[2] == 0xA) || (header[2] == 0xD)))
            {
                bmp = PnmReader.Load(stream);
            }
            else if ((header[0] == 0x59) && (header[1] == 0xa6) && (header[2] == 0x6a) && (header[3] == 0x95))
            {
                bmp = RasReader.Load(stream);
            }
            else if ((header[0x80] == 'D') && (header[0x81] == 'I') && (header[0x82] == 'C') && (header[0x83] == 'M'))
            {
                bmp = DicomReader.Load(stream);
            }

            return bmp;
        }

    }
}
