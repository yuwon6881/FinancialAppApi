namespace FinancialAppApi.Services.Documents;

/// <summary>
/// Configuration for the Google Cloud Storage document vault.
/// </summary>
public sealed class DocumentVaultOptions
{
    public bool Enabled { get; set; } = true;
    public string Bucket { get; set; } = string.Empty;
    public long MaxDocumentBytes { get; set; } = 20 * 1024 * 1024; // 20 MiB
    public long MaxTotalBytesPerUser { get; set; } = 2L * 1024 * 1024 * 1024; // 2 GiB
    public int MaxBulkDocuments { get; set; } = 10;
}
