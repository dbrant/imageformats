using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

/*

Decoder for DICOM images. May not decode all variations of DICOM
images, since the specification is very broad.

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
    /// Handles reading DICOM images.
    /// </summary>
    public static class DicomReader
    {

        /// <summary>
        /// Reads a DICOM image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp = Load(f);
            }
            return bmp;
        }

        /// <summary>
        /// Reads a DICOM image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        /// 
        public static Bitmap Load(Stream stream)
        {
            Bitmap theBitmap = null;
            BinaryReader reader = new BinaryReader(stream);
            byte[] tempBytes = new byte[256];

            stream.Seek(0x80, SeekOrigin.Current);
            stream.Read(tempBytes, 0, 0x10);

            //check signature...
            string signature = System.Text.Encoding.ASCII.GetString(tempBytes, 0, 4);
            if (!signature.Equals("DICM"))
                throw new ApplicationException("Not a valid DICOM file.");

            int imgWidth = 0, imgHeight = 0;
            int samplesPerPixel = 0, numFrames = 0, bitsPerSample = 0, bitsStored = 0;
            int dataLength = 0;

            bool bigEndian = false, explicitVR = true;
            bool isJPEG = false, isRLE = false;

            //read the meta-group, and determine stuff from it
            int metaGroupLen = (int)LittleEndian(BitConverter.ToUInt32(tempBytes, 0xC));
            if(metaGroupLen > 10000)
                throw new ApplicationException("Meta group is a bit too long. May not be a valid DICOM file.");

            tempBytes = new byte[metaGroupLen];
            stream.Read(tempBytes, 0, metaGroupLen);

            //convert the whole thing to a string, and search it for clues
            string metaGroupStr = System.Text.Encoding.ASCII.GetString(tempBytes);

            if (metaGroupStr.Contains("1.2.840.10008.1.2\0"))
                explicitVR = false;
            if (metaGroupStr.Contains("1.2.840.10008.1.2.2\0"))
                bigEndian = true;
            if (metaGroupStr.Contains("1.2.840.10008.1.2.5\0"))
                isRLE = true;
            if (metaGroupStr.Contains("1.2.840.10008.1.2.4."))
                isJPEG = true;


            if(isRLE)
                throw new ApplicationException("RLE-encoded DICOM images are not supported.");
            if (isJPEG)
                throw new ApplicationException("JPEG-encoded DICOM images are not supported.");


            //get header information:
            bool reachedData = false;
            int groupNumber, elementNumber;
            while (!reachedData && (stream.Position < stream.Length))
            {

                groupNumber = getGroupNumber(reader, bigEndian);
                elementNumber = getShort(reader, groupNumber, bigEndian);

                if (groupNumber == 0x28)
                {
                    if (elementNumber == 0x2)
                    {
                        samplesPerPixel = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else if (elementNumber == 0x8)
                    {
                        numFrames = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else if (elementNumber == 0x10)
                    {
                        imgHeight = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else if (elementNumber == 0x11)
                    {
                        imgWidth = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else if (elementNumber == 0x100)
                    {
                        bitsPerSample = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else if (elementNumber == 0x101)
                    {
                        bitsStored = (int)getNumeric(reader, groupNumber, bigEndian, explicitVR);
                    }
                    else
                    {
                        skipElement(reader, groupNumber, elementNumber, bigEndian, explicitVR);
                    }
                }
                else if (groupNumber == 0x7FE0)
                {
                    if (elementNumber == 0x10)
                    {
                        //we've reached the data!
                        if (explicitVR)
                        {
                            int v1 = stream.ReadByte();
                            int v2 = stream.ReadByte();

                            getShort(reader, groupNumber, false);
                            dataLength = (int)getInt(reader, groupNumber, bigEndian);
                        }
                        else
                        {
                            dataLength = (int)getInt(reader, groupNumber, bigEndian);
                        }

                        reachedData = true;
                    }
                    else
                    {
                        skipElement(reader, groupNumber, elementNumber, bigEndian, explicitVR);
                    }
                }
                else
                {
                    skipElement(reader, groupNumber, elementNumber, bigEndian, explicitVR);
                }
            }


            byte[] data = null;

            if (dataLength > 0)
            {
                data = new byte[dataLength];
                stream.Read(data, 0, dataLength);
            }
            else if (dataLength == -1)
            {

                //we'll have to read the data by sequential packets

                List<byte[]> dataSegments = new List<byte[]>();
                UInt16 tempShort;
                int segmentLen = 0;

                while (stream.Position < stream.Length)
                {
                    tempShort = getShort(reader, 0, bigEndian);
                    if (tempShort != 0xFFFE)
                        break;

                    tempShort = getShort(reader, 0, bigEndian);
                    if ((tempShort != 0xE000) && (tempShort != 0xE00D) && (tempShort != 0xE0DD))
                        break;

                    segmentLen = (int)getInt(reader, 0, bigEndian);

                    if (segmentLen < 0 || segmentLen > 100000000)
                        break;

                    if (segmentLen > 0)
                    {
                        byte[] segment = new byte[segmentLen];
                        stream.Read(segment, 0, segmentLen);

                        dataSegments.Add(segment);
                    }
                }

                dataLength = 0;
                foreach (var i in dataSegments)
                    dataLength += i.Length;

                data = new byte[dataLength];

                int dataPtr = 0;
                for (int i = 0; i < dataSegments.Count; i++)
                {
                    Array.Copy(dataSegments[i], 0, data, dataPtr, dataSegments[i].Length);
                    dataPtr += dataSegments[i].Length;
                }

            }


            if (dataLength == 0)
                throw new ApplicationException("DICOM file does not appear to have any image data.");


            MemoryStream dataStream = new MemoryStream(data);
            reader = new BinaryReader(dataStream);

            //detect whether the data is really a JPG image
            if ((data[0] == 0xFF) && (data[1] == 0xD8) && (data[2] == 0xFF))
            {
                theBitmap = (Bitmap)Image.FromStream(dataStream);
                return theBitmap;
            }


            if (numFrames == 0)
                numFrames = 1;

            if (samplesPerPixel > 4)
                throw new ApplicationException("Do not support greater than 4 samples per pixel.");

            if ((bitsPerSample != 8) && (bitsPerSample != 16) && (bitsPerSample != 32))
                throw new ApplicationException("Invalid bits per sample.");

            byte[] bmpData = new byte[imgWidth * 4 * imgHeight];

            try
            {

                if (samplesPerPixel == 1)
                {
                    if (bitsPerSample == 8)
                    {
                        byte b;
                        for (int y = 0; y < imgHeight; y++)
                        {
                            for (int x = 0; x < imgWidth; x++)
                            {
                                b = (byte)dataStream.ReadByte();
                                bmpData[4 * (y * imgWidth + x)] = b;
                                bmpData[4 * (y * imgWidth + x) + 1] = b;
                                bmpData[4 * (y * imgWidth + x) + 2] = b;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                        }
                    }
                    else if (bitsPerSample == 16)
                    {

                        //pre-read all the samples, so we can normalize
                        UInt16[] samples = new UInt16[imgHeight * imgWidth];
                        try
                        {
                            for (int i = 0; i < samples.Length; i++)
                                samples[i] = getShort(reader, 0, bigEndian);
                        }
                        catch { }

                        //normalize
                        UInt16 maxVal = 0;
                        for (int i = 0; i < samples.Length; i++)
                            if (samples[i] > maxVal)
                                maxVal = samples[i];
                        int multiplier = maxVal == 0 ? 1 : 65536 / maxVal;

                        byte b;
                        int sampPtr = 0;
                        for (int y = 0; y < imgHeight; y++)
                        {
                            for (int x = 0; x < imgWidth; x++)
                            {
                                b = (byte)((samples[sampPtr++] * multiplier) >> 8);
                                //b = (byte)(getShort(reader, 0, bigEndian) & 0xFF);
                                bmpData[4 * (y * imgWidth + x)] = b;
                                bmpData[4 * (y * imgWidth + x) + 1] = b;
                                bmpData[4 * (y * imgWidth + x) + 2] = b;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                        }
                    }
                    else if (bitsPerSample == 32)
                    {
                        byte b;
                        for (int y = 0; y < imgHeight; y++)
                        {
                            for (int x = 0; x < imgWidth; x++)
                            {
                                b = (byte)(getFloat(reader, 0, bigEndian) * 255);
                                bmpData[4 * (y * imgWidth + x)] = b;
                                bmpData[4 * (y * imgWidth + x) + 1] = b;
                                bmpData[4 * (y * imgWidth + x) + 2] = b;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                        }
                    }
                }
                else if (samplesPerPixel == 3)
                {
                    if (bitsPerSample == 8)
                    {
                        for (int y = 0; y < imgHeight; y++)
                        {
                            for (int x = 0; x < imgWidth; x++)
                            {
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)dataStream.ReadByte();
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)dataStream.ReadByte();
                                bmpData[4 * (y * imgWidth + x)] = (byte)dataStream.ReadByte();
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                        }
                    }
                    else if (bitsPerSample == 16)
                    {

                    }
                    else if (bitsPerSample == 32)
                    {

                    }
                }



            }
            catch (Exception e)
            {
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing DICOM file: " + e.Message);
            }

            theBitmap = new Bitmap((int)imgWidth, (int)imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Drawing.Imaging.BitmapData bmpBits = theBitmap.LockBits(new Rectangle(0, 0, theBitmap.Width, theBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, imgWidth * 4 * imgHeight);
            theBitmap.UnlockBits(bmpBits);

            return theBitmap;
        }



        private static UInt16 getGroupNumber(BinaryReader reader, bool bigEndian)
        {
            UInt16 ret = 0;
            ret = LittleEndian(reader.ReadUInt16());
            if (ret != 0x2)
                if (bigEndian)
                    ret = BigEndian((UInt16)ret);
            return ret;
        }

        private static UInt16 getShort(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt16 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = LittleEndian(reader.ReadUInt16());
            }
            else
            {
                if (bigEndian) ret = BigEndian(reader.ReadUInt16());
                else ret = LittleEndian(reader.ReadUInt16());
            }
            return ret;
        }

        private static UInt32 getInt(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt32 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = LittleEndian(reader.ReadUInt32());
            }
            else
            {
                if (bigEndian) ret = BigEndian(reader.ReadUInt32());
                else ret = LittleEndian(reader.ReadUInt32());
            }
            return ret;
        }

        private static float getFloat(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt32 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = LittleEndian(reader.ReadUInt32());
            }
            else
            {
                if (bigEndian) ret = BigEndian(reader.ReadUInt32());
                else ret = LittleEndian(reader.ReadUInt32());
            }
            return BitConverter.ToSingle(BitConverter.GetBytes(ret), 0);
        }

        private static UInt32 getNumeric(BinaryReader reader, int groupNumber, bool bigEndian, bool explicitVR)
        {
            UInt32 ret = 0;
            if (explicitVR)
            {
                int v1 = reader.ReadByte(), v2 = reader.ReadByte();
                int len = getShort(reader, groupNumber, bigEndian);
                if (v1 == 'U' && v2 == 'S')
                {
                    if (len != 2)
                        throw new ApplicationException("Incorrect size for a US field.");
                    ret = getShort(reader, groupNumber, bigEndian);
                }
                else if (v1 == 'U' && v2 == 'L')
                {
                    if (len != 4)
                        throw new ApplicationException("Incorrect size for a UL field.");
                    ret = getInt(reader, groupNumber, bigEndian);
                }
                else if (v1 == 'S' && v2 == 'S')
                {
                    if (len != 2)
                        throw new ApplicationException("Incorrect size for a SS field.");
                    ret = getShort(reader, groupNumber, bigEndian);
                }
                else if (v1 == 'S' && v2 == 'L')
                {
                    if (len != 4)
                        throw new ApplicationException("Incorrect size for a SL field.");
                    ret = getInt(reader, groupNumber, bigEndian);
                }
                else if (v1 == 'I' && v2 == 'S' && len < 16)
                {
                    string retStr = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(len));
                    try { ret = Convert.ToUInt32(retStr.Trim()); }
                    catch { }
                }
                else
                {
                    reader.BaseStream.Seek(len, SeekOrigin.Current);
                }
            }
            else
            {
                int len = (int)getInt(reader, groupNumber, bigEndian);
                if (len == 2)
                    ret = getShort(reader, groupNumber, bigEndian);
                else if (len == 4)
                    ret = getInt(reader, groupNumber, bigEndian);
                else
                    reader.BaseStream.Seek(len, SeekOrigin.Current);
            }
            return ret;
        }

        private static void skipElement(BinaryReader reader, int groupNumber, int elementNumber, bool bigEndian, bool explicitVR)
        {
            int len;
            string str = "";

            if (groupNumber == 0xFFFE)
            {
                len = (int)getInt(reader, groupNumber, bigEndian);
                System.Diagnostics.Debug.WriteLine("@" + reader.BaseStream.Position.ToString("X2") + " Skipping FFFE chunk.");

                if(len > 0)
                    reader.BaseStream.Seek(len, SeekOrigin.Current);

            }
            else
            {
                if (explicitVR)
                {

                    int v1 = reader.ReadByte(), v2 = reader.ReadByte();
                    if ((v1 == 'O' && v2 == 'B') || (v1 == 'O' && v2 == 'W') || (v1 == 'O' && v2 == 'F') ||
                        (v1 == 'S' && v2 == 'Q') || (v1 == 'U' && v2 == 'T') || (v1 == 'U' && v2 == 'N'))
                    {
                        getShort(reader, groupNumber, false);
                        len = (int)getInt(reader, groupNumber, bigEndian);

                        if (v1 == 'S' && v2 == 'Q')
                        {
                            UInt16 tempShort = getShort(reader, groupNumber, bigEndian);
                            if (tempShort != (UInt16)0xFFFE)
                                System.Diagnostics.Debug.WriteLine("Warning: incorrect signature for SQ field.");
                            tempShort = getShort(reader, groupNumber, bigEndian);
                            if (tempShort != (UInt16)0xE000)
                                System.Diagnostics.Debug.WriteLine("Warning: incorrect signature for SQ field.");

                            len = (int)getInt(reader, groupNumber, bigEndian);

                            System.Diagnostics.Debug.WriteLine("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);

                        }
                        else
                        {
                            if (elementNumber != 0)
                            {
                                reader.BaseStream.Seek(len, SeekOrigin.Current);
                                System.Diagnostics.Debug.WriteLine("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);
                            }
                        }

                    }
                    else
                    {
                        len = getShort(reader, groupNumber, bigEndian);
                        reader.BaseStream.Seek(len, SeekOrigin.Current);
                        System.Diagnostics.Debug.WriteLine("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);
                    }

                }
                else
                {
                    len = (int)getInt(reader, groupNumber, bigEndian);
                    if (len == -1)
                        len = 0;

                    reader.BaseStream.Seek(len, SeekOrigin.Current);

                    System.Diagnostics.Debug.WriteLine("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2"));

                }
            }
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
