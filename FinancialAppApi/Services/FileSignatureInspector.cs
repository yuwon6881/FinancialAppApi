
namespace FinancialAppApi.Services;

/// <summary>
/// Detects MIME types from file magic bytes and maps them to file extensions.
/// </summary>
public static class FileSignatureInspector
{
    private static readonly HashSet<string> SafeImageMimeTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
    };

    /// <summary>
    /// Inspects the leading bytes of a file and returns its MIME type, or null if unrecognised.
    /// </summary>
    public static string? DetectMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.StartsWith(pngSignature))
        {
            return "image/png";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = data.Slice(8, 4);
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
                brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8))
            {
                return "image/heic";
            }
            if (brand.SequenceEqual("heif"u8) || brand.SequenceEqual("mif1"u8) ||
                brand.SequenceEqual("msf1"u8))
            {
                return "image/heif";
            }
        }

        if (data.Length >= 5 && data[..5].SequenceEqual("%PDF-"u8))
        {
            return "application/pdf";
        }

        var firstContentIndex = 0;
        if (data.Length >= 3 && data[..3].SequenceEqual("\uFEFF"u8))
        {
            firstContentIndex = 3;
        }
        while (firstContentIndex < data.Length && data[firstContentIndex] is 0x20 or 0x09 or 0x0A or 0x0D)
        {
            firstContentIndex++;
        }

        if (data[firstContentIndex..].StartsWith("<?xml"u8) ||
            (data.Length > firstContentIndex && data[firstContentIndex] == (byte)'<'))
        {
            return "application/xml";
        }

        if (firstContentIndex < data.Length)
        {
            var b = data[firstContentIndex];
            if (b == '{' || b == '[') return "application/json";
        }

        return null;
    }

    /// <summary>
    /// Returns the canonical file extension (including the leading dot) for the given MIME type.
    /// </summary>
    public static string ExtensionForMimeType(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "application/pdf" => ".pdf",
        "application/xml" => ".xml",
        "application/json" => ".json",
        _ => ".bin"
    };

    /// <summary>
    /// Returns true when the MIME type belongs to the image/* family.
    /// </summary>
    public static bool IsImage(string mimeType) =>
        SafeImageMimeTypes.Contains(mimeType.Trim().ToLowerInvariant());
}
