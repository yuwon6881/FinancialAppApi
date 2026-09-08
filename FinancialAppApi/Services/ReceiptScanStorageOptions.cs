namespace FinancialAppApi.Services;

/// <summary>
/// Configuration for the Google Cloud Storage bucket that holds in-flight OCR receipt images.
///
/// This is deliberately a different bucket from the document vault. Receipt images are scratch
/// data deleted as soon as OCR reaches a terminal state, so the bucket carries a short lifecycle
/// rule; vault documents are user-retained and must never be swept by one.
/// </summary>
public sealed class ReceiptScanStorageOptions
{
    public string Bucket { get; set; } = string.Empty;
}
