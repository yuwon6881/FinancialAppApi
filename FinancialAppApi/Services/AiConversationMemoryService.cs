using System.Text.Json;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed class AiConversationMemoryService
{
    internal const int MaxPromptHistoryCharacters = 12_000;
    private const int MaxHydratedTurns = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex KeywordPattern = new(@"[\p{L}\p{N}]{3,}", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "been", "before", "could", "from", "have", "into",
        "just", "last", "much", "please", "show", "that", "the", "their", "then", "there", "these",
        "this", "those", "what", "when", "where", "which", "with", "would", "your"
    };

    private readonly AppDbContext _context;

    public AiConversationMemoryService(AppDbContext context)
    {
        _context = context;
    }

    internal sealed record PreparedConversation(
        AiConversation? Conversation,
        string ClientTurnId,
        IReadOnlyList<AiChatMessage> History,
        AiConversationState? State,
        bool SensitiveMode,
        bool Conflict = false,
        AiChatResponse? Replay = null);

    public async Task<AiConversationResponse> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var conversation = await _context.AiConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation == null)
        {
            return new AiConversationResponse(null, 0, [], null);
        }

        var sensitiveMode = await IsSensitiveModeAsync(cancellationToken);
        var historyRedacted = sensitiveMode && await _context.AiConversationTurns
            .AsNoTracking()
            .AnyAsync(turn => turn.ConversationId == conversation.Id && !turn.SensitiveMode, cancellationToken);
        var turnsQuery = _context.AiConversationTurns
            .AsNoTracking()
            .Where(turn => turn.ConversationId == conversation.Id);
        if (sensitiveMode) turnsQuery = turnsQuery.Where(turn => turn.SensitiveMode);
        var turns = await turnsQuery
            .OrderByDescending(turn => turn.CreatedAt)
            .ThenByDescending(turn => turn.Id)
            .Take(MaxHydratedTurns)
            .OrderBy(turn => turn.CreatedAt)
            .ThenBy(turn => turn.Id)
            .ToListAsync(cancellationToken);
        var messages = turns
            .SelectMany(turn => new[]
            {
                new AiChatMessage("user", turn.UserMessage),
                new AiChatMessage("assistant", turn.AssistantReply)
            })
            .ToList();
        return new AiConversationResponse(
            conversation.Id,
            conversation.Version,
            messages,
            DeserializeState(conversation.StateJson),
            historyRedacted);
    }

    public async Task DeleteActiveAsync(CancellationToken cancellationToken = default)
    {
        var conversation = await _context.AiConversations.SingleOrDefaultAsync(cancellationToken);
        if (conversation == null) return;
        _context.AiConversations.Remove(conversation);
        await _context.SaveChangesAsync(cancellationToken);
    }

    internal async Task<PreparedConversation> PrepareAsync(
        AiChatRequest request,
        CancellationToken cancellationToken)
    {
        var clientTurnId = NormalizeClientTurnId(request.ClientTurnId);
        if (clientTurnId == null)
        {
            return new PreparedConversation(null, string.Empty, [], null, true, Conflict: true);
        }

        var conversation = await _context.AiConversations.SingleOrDefaultAsync(cancellationToken);
        if (conversation == null)
        {
            if (request.ConversationId != null)
            {
                return new PreparedConversation(null, clientTurnId, [], null, true, Conflict: true);
            }

            conversation = new AiConversation();
            _context.AiConversations.Add(conversation);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A second device can create the one-per-user row at the same time. Reload the
                // winner rather than manufacturing a parallel conversation.
                _context.ChangeTracker.Clear();
                conversation = await _context.AiConversations.SingleAsync(cancellationToken);
            }
        }

        var duplicate = await _context.AiConversationTurns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                turn => turn.ConversationId == conversation.Id && turn.ClientTurnId == clientTurnId,
                cancellationToken);
        if (duplicate != null)
        {
            var replaySensitiveMode = await IsSensitiveModeAsync(cancellationToken);
            return new PreparedConversation(
                conversation,
                clientTurnId,
                [],
                DeserializeState(conversation.StateJson),
                replaySensitiveMode,
                Replay: ToReplay(conversation, duplicate, replaySensitiveMode));
        }

        if (request.ConversationId != null && request.ConversationId != conversation.Id)
        {
            return new PreparedConversation(
                conversation, clientTurnId, [], DeserializeState(conversation.StateJson), true, Conflict: true);
        }
        if (request.ConversationVersion != null && request.ConversationVersion != conversation.Version)
        {
            return new PreparedConversation(
                conversation, clientTurnId, [], DeserializeState(conversation.StateJson), true, Conflict: true);
        }

        var sensitiveMode = await IsSensitiveModeAsync(cancellationToken);
        var history = await SelectPromptHistoryAsync(
            conversation.Id,
            request.Message,
            sensitiveMode,
            cancellationToken);
        return new PreparedConversation(
            conversation,
            clientTurnId,
            history,
            DeserializeState(conversation.StateJson),
            sensitiveMode);
    }

    internal async Task<AiChatResponse?> CompleteAsync(
        PreparedConversation prepared,
        string message,
        AiChatResponse response,
        CancellationToken cancellationToken)
    {
        var conversation = prepared.Conversation!;
        var nextVersion = checked(conversation.Version + 1);
        var state = AiAssistantService.SanitizeConversationState(response.State);
        conversation.StateJson = state == null ? null : JsonSerializer.Serialize(state, JsonOptions);
        conversation.Version = nextVersion;
        conversation.UpdatedAt = DateTime.UtcNow;

        var metadata = BuildMetadata(message, state);
        _context.AiConversationTurns.Add(new AiConversationTurn
        {
            ConversationId = conversation.Id,
            ClientTurnId = prepared.ClientTurnId,
            UserMessage = message.Trim(),
            AssistantReply = response.Reply,
            ActionsJson = JsonSerializer.Serialize(response.Actions, JsonOptions),
            CloseChat = response.CloseChat,
            Intent = metadata.Intent,
            Topic = metadata.Topic,
            FacetsJson = JsonSerializer.Serialize(metadata.Facets, JsonOptions),
            KeywordsJson = JsonSerializer.Serialize(metadata.Keywords, JsonOptions),
            SensitiveMode = prepared.SensitiveMode,
            ConversationVersion = nextVersion
        });

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return null;
        }

        return response with
        {
            State = state,
            ConversationId = conversation.Id,
            ConversationVersion = nextVersion
        };
    }

    private async Task<IReadOnlyList<AiChatMessage>> SelectPromptHistoryAsync(
        Guid conversationId,
        string currentMessage,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        var query = _context.AiConversationTurns
            .AsNoTracking()
            .Where(turn => turn.ConversationId == conversationId);
        if (sensitiveMode)
        {
            query = query.Where(turn => turn.SensitiveMode);
        }

        var turns = await query
            .OrderByDescending(turn => turn.CreatedAt)
            .ThenByDescending(turn => turn.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        var latest = turns.Take(3).ToList();
        var current = BuildMetadata(currentMessage, null);
        var older = turns.Skip(3)
            .Select(turn => new { Turn = turn, Score = Score(turn, current) })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Turn.CreatedAt)
            .Take(3)
            .Select(candidate => candidate.Turn);
        var selected = latest.Concat(older)
            .DistinctBy(turn => turn.Id)
            .OrderBy(turn => turn.CreatedAt)
            .ThenBy(turn => turn.Id)
            .ToList();

        var messages = new List<AiChatMessage>();
        var characters = 0;
        // Prefer recent dialogue if the combined selection reaches the hard prompt budget.
        foreach (var turn in selected.AsEnumerable().Reverse())
        {
            var turnCharacters = turn.UserMessage.Length + turn.AssistantReply.Length;
            if (characters + turnCharacters > MaxPromptHistoryCharacters) continue;
            characters += turnCharacters;
            messages.Add(new AiChatMessage("assistant", turn.AssistantReply));
            messages.Add(new AiChatMessage("user", turn.UserMessage));
        }
        messages.Reverse();
        return messages;
    }

    private async Task<bool> IsSensitiveModeAsync(CancellationToken cancellationToken) =>
        await _context.FinancialSettings
            .AsNoTracking()
            .Select(setting => (bool?)setting.HideSensitive)
            .SingleOrDefaultAsync(cancellationToken) ?? true;

    private static AiChatResponse ToReplay(AiConversation conversation, AiConversationTurn turn, bool sensitiveMode)
    {
        if (sensitiveMode && !turn.SensitiveMode)
        {
            return new AiChatResponse(
                "Earlier replies are hidden while sensitive mode is active. Unhide balances to replay this explanation.",
                [],
                State: null,
                ConversationId: conversation.Id,
                ConversationVersion: conversation.Version,
                HistoryRedacted: true);
        }
        IReadOnlyList<AiUiAction> actions;
        try
        {
            actions = JsonSerializer.Deserialize<List<AiUiAction>>(turn.ActionsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            actions = [];
        }
        return new AiChatResponse(
            turn.AssistantReply,
            actions,
            turn.CloseChat,
            DeserializeState(conversation.StateJson),
            conversation.Id,
            conversation.Version,
            turn.SensitiveMode == false && sensitiveMode);
    }

    private static AiConversationState? DeserializeState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return AiAssistantService.SanitizeConversationState(
                JsonSerializer.Deserialize<AiConversationState>(json, JsonOptions));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeClientTurnId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= 64 ? normalized : null;
    }

    private sealed record TurnMetadata(
        string? Intent,
        string? Topic,
        IReadOnlyList<string> Facets,
        IReadOnlyList<string> Keywords);

    private static TurnMetadata BuildMetadata(string message, AiConversationState? state)
    {
        var keywords = KeywordPattern.Matches(message.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        var topic = state?.LastTopic
            ?? (Regex.IsMatch(message, @"\b(wishlist|wish list|saving for|afford)\b", RegexOptions.IgnoreCase)
                ? "wishlist"
                : Regex.IsMatch(message, @"\b(recurring|subscription|bill|renewal)\b", RegexOptions.IgnoreCase)
                    ? "recurring"
                    : Regex.IsMatch(message, @"\b(ledger|transaction|spend|income|cycle|category)\b", RegexOptions.IgnoreCase)
                        ? "transactional"
                        : null);
        return new TurnMetadata(
            state?.LastIntent,
            topic,
            state?.LastQueryFacets ?? [],
            keywords);
    }

    private static int Score(AiConversationTurn turn, TurnMetadata current)
    {
        var score = 0;
        if (current.Topic != null && string.Equals(turn.Topic, current.Topic, StringComparison.Ordinal)) score += 30;
        if (current.Intent != null && string.Equals(turn.Intent, current.Intent, StringComparison.OrdinalIgnoreCase)) score += 25;
        var facets = DeserializeStringSet(turn.FacetsJson);
        score += current.Facets.Count(facets.Contains) * 8;
        var keywords = DeserializeStringSet(turn.KeywordsJson);
        score += current.Keywords.Count(keywords.Contains) * 5;
        return score;
    }

    private static HashSet<string> DeserializeStringSet(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]", JsonOptions)?
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
