using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Documents;

internal static class TransactionVaultLinks
{
    public static async Task DetachAsync(AppDbContext context, string transactionId, CancellationToken cancellationToken)
    {
        var documents = await context.VaultDocuments
            .Where(document => document.TransactionId == transactionId)
            .ToListAsync(cancellationToken);
        foreach (var document in documents)
        {
            document.TransactionId = null;
            document.DetachedFromTransactionId = transactionId;
        }
    }
}
