namespace FinancialAppApi.Models;

/// <summary>
/// Marks a row whose lifecycle and visibility belong to one application user.
/// </summary>
public interface IUserOwnedEntity
{
    [System.Text.Json.Serialization.JsonIgnore]
    string UserId { get; set; }
}
