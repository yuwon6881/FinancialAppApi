using System.Security.Cryptography;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public class RecoveryCodeService
{
    private const int CodeCount = 10;
    private readonly AppDbContext _context;
    private readonly PasswordHasher<string> _hasher = new();

    public RecoveryCodeService(AppDbContext context)
    {
        _context = context;
    }

    // Replaces any existing recovery codes for the user with a fresh set. The plaintext codes
    // are returned once for display -- only their hashes are persisted.
    public async Task<List<string>> RegenerateAsync(string username)
    {
        var existing = await _context.RecoveryCodes.Where(r => r.Username == username).ToListAsync();
        _context.RecoveryCodes.RemoveRange(existing);

        var codes = new List<string>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
        {
            var code = GenerateCode();
            codes.Add(code);
            _context.RecoveryCodes.Add(new RecoveryCode
            {
                Username = username,
                CodeHash = _hasher.HashPassword(username, code),
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
        return codes;
    }

    // Verifies and, on success, consumes (deletes) the matching unused code so it can't be
    // replayed. Returns true only on a genuine single-use match.
    public async Task<bool> TryConsumeAsync(string username, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var candidates = await _context.RecoveryCodes
            .Where(r => r.Username == username && !r.Used)
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            var result = _hasher.VerifyHashedPassword(username, candidate.CodeHash, code.Trim());
            if (result != PasswordVerificationResult.Failed)
            {
                if (_context.Database.IsRelational())
                {
                    var claimed = await _context.RecoveryCodes
                        .Where(item => item.Id == candidate.Id && !item.Used)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Used, true));
                    if (claimed == 1) return true;
                    continue;
                }

                candidate.Used = true;
                await _context.SaveChangesAsync();
                return true;
            }
        }

        return false;
    }

    public async Task DeleteAllAsync(string username)
    {
        var existing = await _context.RecoveryCodes.Where(r => r.Username == username).ToListAsync();
        _context.RecoveryCodes.RemoveRange(existing);
        await _context.SaveChangesAsync();
    }

    // Groups of 4 hex chars, e.g. "A1B2-C3D4-E5F6", so codes are easy to read and transcribe.
    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }
}
