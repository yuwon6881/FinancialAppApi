using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public sealed class FileSignatureInspectorTests
{
    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0x00 })]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 })]
    [InlineData("image/heic", new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 })]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 })]
    public void DetectMimeType_RecognisesSupportedSignatures(string expected, byte[] data)
    {
        Assert.Equal(expected, FileSignatureInspector.DetectMimeType(data));
    }

    [Fact]
    public void DetectMimeType_RecognisesXmlAfterBomAndWhitespace()
    {
        var data = "\uFEFF \r\n<?xml version=\"1.0\"?><receipt />"u8.ToArray();

        Assert.Equal("application/xml", FileSignatureInspector.DetectMimeType(data));
    }

    [Fact]
    public void DetectMimeType_RejectsGifPolyglotInsteadOfTreatingItAsAnImage()
    {
        var data = "GIF89a<script>document.body.innerHTML='pwned'</script>"u8.ToArray();

        Assert.Null(FileSignatureInspector.DetectMimeType(data));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script /></svg>")]
    [InlineData("<html><script>unsafe()</script></html>")]
    public void DetectMimeType_RecognisesMarkupAsExcludedXml(string markup)
    {
        Assert.Equal("application/xml", FileSignatureInspector.DetectMimeType(System.Text.Encoding.UTF8.GetBytes(markup)));
        Assert.False(FileSignatureInspector.IsImage("image/svg+xml"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\x01\x02\x03")]
    [InlineData("not a document")]
    public void DetectMimeType_RejectsTruncatedOrUnknownBytes(string text)
    {
        Assert.Null(FileSignatureInspector.DetectMimeType(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void IsImage_AllowsOnlyBrowserSafeStoredImageTypes()
    {
        Assert.True(FileSignatureInspector.IsImage("image/jpeg"));
        Assert.True(FileSignatureInspector.IsImage("IMAGE/PNG"));
        Assert.False(FileSignatureInspector.IsImage("image/gif"));
        Assert.False(FileSignatureInspector.IsImage("image/svg+xml"));
        Assert.False(FileSignatureInspector.IsImage("application/xml"));
    }
}
