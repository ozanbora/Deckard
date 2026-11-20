using System.Runtime.InteropServices.WindowsRuntime;
using Deckard.Core.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Deckard.Perception.Imaging;

/// <summary>Loads and saves <see cref="Frame"/>s as image files, for snapshots and offline testing.</summary>
public static class FrameFile
{
    public static async Task<Frame> LoadAsync(string path)
    {
        await using var file = File.OpenRead(path);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var pixels = data.DetachPixelData();

        var frame = Frame.Create((int)decoder.PixelWidth, (int)decoder.PixelHeight, TimeSpan.Zero);
        pixels.AsSpan(0, frame.Pixels.Length).CopyTo(frame.Pixels);
        return frame;
    }

    public static async Task SavePngAsync(Frame frame, string path) => await File.WriteAllBytesAsync(path, await EncodePngAsync(frame));

    /// <summary>The frame as PNG file bytes.</summary>
    public static async Task<byte[]> EncodePngAsync(Frame frame)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels.ToArray());
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    internal static SoftwareBitmap ToSoftwareBitmap(Frame frame) =>
        SoftwareBitmap.CreateCopyFromBuffer(frame.Pixels.ToArray().AsBuffer(), BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
}
