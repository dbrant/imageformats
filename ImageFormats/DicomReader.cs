using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using System.IO;

/*

Decoder for DICOM images. May not decode all variations of DICOM
images, since the specification is very broad.

Copyright 2013-2023 Dmitry Brant
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
    /// Handles reading DICOM images.
    /// </summary>
    public static class DicomReader
    {

        /// <summary>
        /// Reads a DICOM image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Image Load(string fileName)
        {
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Load(f);
            }
        }

        /// <summary>
        /// Reads a DICOM image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        /// 
        public static Image Load(Stream stream)
        {
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
            int metaGroupLen = (int)Util.LittleEndian(BitConverter.ToUInt32(tempBytes, 0xC));
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
                return Image.Load(dataStream);
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
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing DICOM file: " + e.Message);
            }

            return Util.LoadRgb(imgWidth, imgHeight, bmpData);
        }



        private static UInt16 getGroupNumber(BinaryReader reader, bool bigEndian)
        {
            UInt16 ret = Util.LittleEndian(reader.ReadUInt16());
            if (ret != 0x2)
                if (bigEndian)
                    ret = Util.BigEndian((UInt16)ret);
            return ret;
        }

        private static UInt16 getShort(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt16 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = Util.LittleEndian(reader.ReadUInt16());
            }
            else
            {
                if (bigEndian) ret = Util.BigEndian(reader.ReadUInt16());
                else ret = Util.LittleEndian(reader.ReadUInt16());
            }
            return ret;
        }

        private static UInt32 getInt(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt32 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = Util.LittleEndian(reader.ReadUInt32());
            }
            else
            {
                if (bigEndian) ret = Util.BigEndian(reader.ReadUInt32());
                else ret = Util.LittleEndian(reader.ReadUInt32());
            }
            return ret;
        }

        private static float getFloat(BinaryReader reader, int groupNumber, bool bigEndian)
        {
            UInt32 ret = 0;
            if (groupNumber == 0x2)
            {
                ret = Util.LittleEndian(reader.ReadUInt32());
            }
            else
            {
                if (bigEndian) ret = Util.BigEndian(reader.ReadUInt32());
                else ret = Util.LittleEndian(reader.ReadUInt32());
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
                Util.log("@" + reader.BaseStream.Position.ToString("X2") + " Skipping FFFE chunk.");

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
                                Util.log("Warning: incorrect signature for SQ field.");
                            tempShort = getShort(reader, groupNumber, bigEndian);
                            if (tempShort != (UInt16)0xE000)
                                Util.log("Warning: incorrect signature for SQ field.");

                            len = (int)getInt(reader, groupNumber, bigEndian);

                            Util.log("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);

                        }
                        else
                        {
                            if (elementNumber != 0)
                            {
                                reader.BaseStream.Seek(len, SeekOrigin.Current);
                                Util.log("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);
                            }
                        }

                    }
                    else
                    {
                        len = getShort(reader, groupNumber, bigEndian);
                        reader.BaseStream.Seek(len, SeekOrigin.Current);
                        Util.log("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2") + ": " + (char)v1 + (char)v2 + " - " + str);
                    }

                }
                else
                {
                    len = (int)getInt(reader, groupNumber, bigEndian);
                    if (len == -1)
                        len = 0;

                    reader.BaseStream.Seek(len, SeekOrigin.Current);

                    Util.log("@" + reader.BaseStream.Position.ToString("X2") + " Group " + groupNumber.ToString("X2") + ", Element " + elementNumber.ToString("X2"));

                }
            }
        }
    }
}
