imageformats
============
[![Build](https://github.com/dbrant/imageformats/actions/workflows/dotnet.yml/badge.svg)](https://github.com/dbrant/imageformats/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/DmitryBrant.ImageFormats)](https://www.nuget.org/packages/DmitryBrant.ImageFormats/)
[![Downloads](https://img.shields.io/nuget/dt/DmitryBrant.ImageFormats)](https://www.nuget.org/packages/DmitryBrant.ImageFormats/)

.NET modules for decoding obscure graphics formats:

- .PPM, .PGM, .PBM (Netpbm images)
- .TGA (Targa)
- .PCX (ZSoft Paintbrush)
- .IFF (Amiga ILBM images)
- .SGI, .RGB, .BW (SGI images)
- .RAS (Sun raster images)
- .MAC (MacPaint)
- .CUT (Dr. Halo)
- .XPM (X Window PixMap)
- .DEEP (TVPaint IFF DEEP images)
- .FITS (experimental support)
- .DICOM (experimental support)
- .ART (AOL Johnson-Grace images)
- .PSP, .PSPIMAGE (Paint Shop Pro images)

Usage
-----

Every decoder returns an `ImageData` object: a plain buffer of raw pixels, with no
dependency on any particular imaging library. Hand the buffer to whichever library
you prefer.

```csharp
using DmitryBrant.ImageFormats;

// Detects the format automatically, and returns null if it isn't one we know.
ImageData image = Picture.Load("picture.tga");
```

The pixels in `image.Data` are stored top-down, one row after another, four bytes per
pixel in blue, green, red, alpha order. (Read as a 32-bit little-endian integer, that's
the usual packed ARGB layout.) This is the same layout as GDI+ `Format32bppArgb`,
WIC/Direct2D `B8G8R8A8`, Avalonia's `Bgra8888`, and ImageSharp's `Bgra32`, so it can go
straight into any of them:

```csharp
// Avalonia
var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height),
    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
using (var buffer = bitmap.Lock())
    for (int y = 0; y < image.Height; y++)
        Marshal.Copy(image.Data, y * image.Stride, buffer.Address + (y * buffer.RowBytes), image.Stride);

// ImageSharp
var bitmap = Image.LoadPixelData<Bgra32>(image.Data, image.Width, image.Height);

// System.Drawing
var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
for (int y = 0; y < image.Height; y++)
    Marshal.Copy(image.Data, y * image.Stride, bits.Scan0 + (y * bits.Stride), image.Stride);
bitmap.UnlockBits(bits);
```

A few source formats store their pixel data as an embedded image in some other
well-known encoding, most notably DICOM files that wrap a JPEG. Rather than pull in a
JPEG decoder, the library passes those bytes through untouched: `image.IsEncoded` is
true, `image.Data` is null, and the encoded bytes are in `image.EncodedData` (with
`image.EncodedFormat` naming the encoding) for you to decode yourself.

The `ImageViewer` project in this repository is a small Avalonia app that does all of
the above; see its `Interop.cs` for a complete example.

Copyright 2013+ Dmitry Brant

https://dmitrybrant.com

License: MIT
