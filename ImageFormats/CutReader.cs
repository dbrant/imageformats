using System;
using System.IO;

/*

Decoder for Dr. Halo CUT (.CUT) images.
Decodes all CUT images that I've found in the wild.  If you find
one that it fails to decode, let me know!

Copyright 2013+ Dmitry Brant
https://dmitrybrant.com

License: MIT
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
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName){
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a Dr. Halo (.CUT) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            var reader = new BinaryReader(stream);

            int imgWidth = Util.LittleEndian(reader.ReadUInt16());
            int imgHeight = Util.LittleEndian(reader.ReadUInt16());
            Util.LittleEndian(reader.ReadUInt16()); //reserved word

            if ((imgWidth < 1) || (imgHeight < 1) || (imgWidth > 32767) || (imgHeight > 32767))
                throw new ImageDecodeException("This CUT file appears to have invalid dimensions.");

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
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing CUT file: " + e.Message);
            }

            return Util.LoadRgb(imgWidth, imgHeight, bmpData);
        }
    }
}
