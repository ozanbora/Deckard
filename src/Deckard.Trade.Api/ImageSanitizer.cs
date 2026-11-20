using System.Buffers.Binary;

namespace Deckard.Trade.Api;

/// <summary>
/// Strips metadata from a screenshot without decoding it (the server has no image library): a PNG keeps only the chunks
/// that draw the picture, a JPEG loses its APP1-15 and comment segments (EXIF, XMP, ICC profiles, camera and location
/// data). Anything that isn't a well-formed PNG or JPEG is refused.
/// </summary>
public static class ImageSanitizer
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly HashSet<string> PngKept = ["IHDR", "PLTE", "tRNS", "IDAT", "IEND", "sRGB", "gAMA"];

    /// <summary>The cleaned image and its extension ("png" or "jpg"), or null when it isn't a whole PNG or JPEG.</summary>
    public static (byte[] Image, string Extension)? Clean(byte[] data) =>
        data.AsSpan().StartsWith(PngSignature) ? Png(data) is { } png ? (png, "png") : null
        : data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8 ? Jpeg(data) is { } jpg ? (jpg, "jpg") : null
        : null;

    private static byte[]? Png(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);
        var at = PngSignature.Length;
        var first = true;
        while (at + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            if (length > int.MaxValue || at + 12 + (long)length > data.Length)
                return null; // a chunk that runs past the end
            var type = System.Text.Encoding.ASCII.GetString(data, at + 4, 4);
            if (first && type != "IHDR")
                return null;
            first = false;
            var size = 12 + (int)length;
            if (PngKept.Contains(type))
                output.Write(data, at, size);
            at += size;
            if (type == "IEND")
                return output.ToArray(); // whatever follows IEND is dropped too
        }
        return null; // no IEND: cut off
    }

    private static byte[]? Jpeg(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write(data, 0, 2); // SOI
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
                return null;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
            if (length < 2 || at + 2 + length > data.Length)
                return null;
            if (marker == 0xDA)
            {
                // Start of scan: the picture data follows, to the end of the image; it carries no metadata segments.
                output.Write(data, at, data.Length - at);
                return data[^2] == 0xFF && data[^1] == 0xD9 ? output.ToArray() : null;
            }
            var metadata = marker is >= 0xE1 and <= 0xEF or 0xFE; // APP1-APP15 (EXIF, XMP, ICC...) and comments
            if (!metadata)
                output.Write(data, at, 2 + length);
            at += 2 + length;
        }
        return null;
    }
}
