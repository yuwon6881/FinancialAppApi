using System.Text.Json;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed partial class AiConversationMemoryService
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
        AiChatResponse? Replay = null,
        AiConversationTurn? PendingTurn = null,
        int ClientContractVersion = 1);

    public async Task<AiConversationResponse> GetActiveAsync(
        bool forceSensitiveMode = false,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _context.AiConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation == null)
        {
            return new AiConversationResponse(null, 0, [], null);
        }

        var sensitiveMode = await ResolveEffectiveSensitiveModeAsync(forceSensitiveMode, cancellationToken);
        var historyRedacted = sensitiveMode && await _context.AiConversationTurns
            .AsNoTracking()
            .AnyAsync(turn => turn.ConversationId == conversation.Id && !turn.SensitiveMode, cancellationToken);
        var turnsQuery = _context.AiConversationTurns
            .AsNoTracking()
            .Where(turn => turn.ConversationId == conversation.Id && turn.Status == "Completed");
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
        var pendingBatches = await LoadPendingActionBatchesAsync(
            conversation.Id,
            sensitiveMode,
            cancellationToken);
        return new AiConversationResponse(
            conversation.Id,
            conversation.Version,
            messages,
            DeserializeState(conversation.StateJson),
            historyRedacted,
            pendingBatches);
    }

    public async Task<bool> DeleteActiveAsync(
        Guid? conversationId = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _context.AiConversations.SingleOrDefaultAsync(cancellationToken);
        if (conversation == null) return true;
        if (conversationId != null && conversation.Id != conversationId) return false;
        if (expectedVersion != null && conversation.Version != expectedVersion) return false;
        _context.AiConversations.Remove(conversation);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
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
            var replaySensitiveMode = await ResolveEffectiveSensitiveModeAsync(request.ForceSensitiveMode, cancellationToken);
            if (!duplicate.Status.Equals("Completed", StringComparison.Ordinal))
            {
                return new PreparedConversation(
                    conversation,
                    clientTurnId,
                    [],
                    DeserializeState(conversation.StateJson),
                    replaySensitiveMode,
                    Replay: new AiChatResponse(
                        "That request is still being processed. Retry in a moment to collect its result.",
                        [],
                        ConversationId: conversation.Id,
                        ConversationVersion: conversation.Version),
                    ClientContractVersion: request.ClientContractVersion);
            }
            return new PreparedConversation(
                conversation,
                clientTurnId,
                [],
                DeserializeState(conversation.StateJson),
                replaySensitiveMode,
                Replay: ToReplay(conversation, duplicate, replaySensitiveMode),
                ClientContractVersion: request.ClientContractVersion);
        }

        if (request.ConversationId != null && request.ConversationId != conversation.Id)
        {
            return new PreparedConversation(
                conversation, clientTurnId, [], DeserializeState(conversation.StateJson), true, Conflict: true);
        }
        // Only a client that already holds this conversation's id can make a claim about its
        // version. A client without one has never been told a version, so its default 0 is not a
        // claim -- and treating it as one turned every turn the client never received (a stopped
        // request that still committed server-side) into a bogus "changed on another device".
        if (request.ConversationId != null &&
            request.ConversationVersion != null &&
            request.ConversationVersion != conversation.Version)
        {
            return new PreparedConversation(
                conversation, clientTurnId, [], DeserializeState(conversation.StateJson), true, Conflict: true);
        }

        var sensitiveMode = await ResolveEffectiveSensitiveModeAsync(request.ForceSensitiveMode, cancellationToken);
        var history = await SelectPromptHistoryAsync(
            conversation.Id,
            request.Message,
            sensitiveMode,
            cancellationToken);
        var pendingTurn = new AiConversationTurn
        {
            ConversationId = conversation.Id,
            ClientTurnId = clientTurnId,
            UserMessage = sensitiveMode ? "[Hidden request]" : request.Message.Trim(),
            AssistantReply = string.Empty,
            ActionsJson = "[]",
            Status = "Pending",
            SensitiveMode = sensitiveMode,
            ConversationVersion = conversation.Version,
            CreatedAt = DateTime.UtcNow
        };
        _context.AiConversationTurns.Add(pendingTurn);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            conversation = await _context.AiConversations.SingleAsync(cancellationToken);
            var winner = await _context.AiConversationTurns
                .AsNoTracking()
                .SingleAsync(
                    turn => turn.ConversationId == conversation.Id && turn.ClientTurnId == clientTurnId,
                    cancellationToken);
            var replaySensitiveMode = await ResolveEffectiveSensitiveModeAsync(request.ForceSensitiveMode, cancellationToken);
            return new PreparedConversation(
                conversation,
                clientTurnId,
                [],
                DeserializeState(conversation.StateJson),
                replaySensitiveMode,
                Replay: winner.Status == "Completed"
                    ? ToReplay(conversation, winner, replaySensitiveMode)
                    : new AiChatResponse(
                        "That request is still being processed. Retry in a moment to collect its result.",
                        [],
                        ConversationId: conversation.Id,
                        ConversationVersion: conversation.Version),
                ClientContractVersion: request.ClientContractVersion);
        }
        return new PreparedConversation(
            conversation,
            clientTurnId,
            history,
            DeserializeState(conversation.StateJson),
            sensitiveMode,
            PendingTurn: pendingTurn,
            ClientContractVersion: request.ClientContractVersion);
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
        var actions = response.Actions
            .Select(action => action.ActionId == null ? action with { ActionId = Guid.NewGuid() } : action)
            .ToList();
        var turn = prepared.PendingTurn ?? throw new InvalidOperationException("The AI turn was not reserved.");
        turn.UserMessage = prepared.SensitiveMode ? "[Hidden request]" : message.Trim();
        turn.AssistantReply = response.Reply;
        turn.ActionsJson = JsonSerializer.Serialize(actions, JsonOptions);
        turn.CloseChat = response.CloseChat;
        turn.Intent = metadata.Intent;
        turn.Topic = metadata.Topic;
        turn.FacetsJson = JsonSerializer.Serialize(metadata.Facets, JsonOptions);
        turn.KeywordsJson = JsonSerializer.Serialize(metadata.Keywords, JsonOptions);
        turn.SensitiveMode = prepared.SensitiveMode;
        turn.ConversationVersion = nextVersion;
        turn.Status = "Completed";
        turn.CompletedAt = DateTime.UtcNow;
        if (prepared.ClientContractVersion < 2 || actions.Count == 0) turn.ActionsResolvedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return null;
        }

        var actionBatch = prepared.ClientContractVersion >= 2 && actions.Count > 0
            ? new AiActionBatchResponse(turn.Id, actions)
            : null;
        return response with
        {
            Actions = actions,
            State = state,
            ConversationId = conversation.Id,
            ConversationVersion = nextVersion,
            ActionBatch = actionBatch
        };
    }

    internal async Task FailAsync(PreparedConversation prepared, CancellationToken cancellationToken)
    {
        if (prepared.PendingTurn == null) return;
        _context.ChangeTracker.Clear();
        var pending = await _context.AiConversationTurns
            .SingleOrDefaultAsync(turn => turn.Id == prepared.PendingTurn.Id && turn.Status == "Pending", cancellationToken);
        if (pending == null) return;
        _context.AiConversationTurns.Remove(pending);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ResolveActionBatchAsync(
        Guid batchId,
        bool dismissed,
        CancellationToken cancellationToken)
    {
        var turn = await _context.AiConversationTurns
            .SingleOrDefaultAsync(candidate => candidate.Id == batchId && candidate.Status == "Completed", cancellationToken);
        if (turn == null) return false;
        if (turn.ActionsResolvedAt != null) return true;
        turn.ActionsResolvedAt = DateTime.UtcNow;
        turn.ActionsDismissedAt = dismissed ? DateTime.UtcNow : null;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<AiChatMessage>> SelectPromptHistoryAsync(
        Guid conversationId,
        string currentMessage,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        var query = _context.AiConversationTurns
            .AsNoTracking()
            .Where(turn => turn.ConversationId == conversationId && turn.Status == "Completed");
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

    internal async Task<bool> ResolveEffectiveSensitiveModeAsync(
        bool forceSensitiveMode,
        CancellationToken cancellationToken) =>
        forceSensitiveMode || await IsSensitiveModeAsync(cancellationToken);

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
