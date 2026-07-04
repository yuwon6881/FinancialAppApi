using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

// Short-lived server-side record of a challenge handed to the client between
// the "options" and "verify" steps of a WebAuthn ceremony. The API is
// stateless (bearer tokens, no cookie session), so this stands in for the
// ASP.NET Session that the Fido2NetLib samples normally rely on.
public class WebAuthnChallenge
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Purpose { get; set; } = string.Empty; // "register" or "login"

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string OptionsJson { get; set; } = string.Empty;

    [Required]
    public DateTime ExpiresAt { get; set; }
}
