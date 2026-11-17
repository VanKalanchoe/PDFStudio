using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace PdfStudio.App.Rendering;

internal static class SkiaBitmapAdapter
{
    public static unsafe WriteableBitmap ToWriteableBitmap(SKBitmap skBitmap)
    {
        ArgumentNullException.ThrowIfNull(skBitmap);

        var writeable = new WriteableBitmap(
            new PixelSize(skBitmap.Width, skBitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var frameBuffer = writeable.Lock();
        var srcPtr = (byte*)skBitmap.GetPixels().ToPointer();
        var dstPtr = (byte*)frameBuffer.Address.ToPointer();

        int rowBytesToCopy = Math.Min(skBitmap.RowBytes, frameBuffer.RowBytes);
        for (int y = 0; y < skBitmap.Height; y++)
        {
            Buffer.MemoryCopy(
                srcPtr + (y * skBitmap.RowBytes),
                dstPtr + (y * frameBuffer.RowBytes),
                frameBuffer.RowBytes,
                rowBytesToCopy);
        }

        return writeable;
    }
}
