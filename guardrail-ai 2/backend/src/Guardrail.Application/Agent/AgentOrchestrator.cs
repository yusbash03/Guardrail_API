using System.Text.RegularExpressions;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Application.Policies;
using Guardrail.Domain.Agent;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Guardrail.Domain.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Application.Agent;

public sealed record AgentMessageDto(Guid Id, int Sequence, AgentMessageRole Role, string Content, IReadOnlyList<HydratedCard>? Cards, DateTimeOffset CreatedAt);

public sealed record ToolCallSummaryDto(string Name, AgentExecutionStatus Status, int DurationMs, string? ErrorCode);

public sealed record TurnLimitsDto(int LlmRequestsUsed, int LlmRequestsMax, int ToolCallsUsed, int ToolCallsMax, string? Provider);

public sealed record AgentTurnResponse(
    Guid ConversationId,
    AgentMessageDto UserMessage,
    AgentMessageDto AssistantMessage,
    IReadOnlyList<ToolCallSummaryDto> ToolCalls,
    TurnLimitsDto Limits,
    string? Code);

public sealed record ConversationDto(Guid Id, string Title, ConversationStatus Status, DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt);

/// <summary>Runs one agent turn (AI_AGENT_SPECIFICATION.md §3). The model proposes; it never decides or pays.</summary>
public sealed partial class AgentOrchestrator(
    IAppDbContext db,
    ILlmClient llm,
    ToolRegistry registry,
    ToolDispatcher dispatcher,
    CardHydrator hydrator,
    PolicyLoader policies,
    ICurrentUser currentUser,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    ILogger<AgentOrchestrator> log)
{
    public const string StepLimitText = "I've hit my step limit for this request. Here's what I have so far — you can ask me to continue.";
    public const string ClaimNote = "Status shown in the card below is authoritative.";

    public async Task<Result<AgentTurnResponse>> RunTurnAsync(Guid userId, Guid conversationId, string? content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            return DomainError.Validation("Message must be 1–2000 characters.",
                new Dictionary<string, string[]> { ["content"] = ["Must be between 1 and 2000 characters."] });
        }

        var conversation = await db.Conversations.SingleOrDefaultAsync(
            c => c.Id == conversationId && c.UserId == userId && c.Status == ConversationStatus.Active, cancellationToken);
        if (conversation is null)
        {
            return DomainError.NotFound("Conversation");
        }

        // 2. Persist USER message.
        var now = clock.GetUtcNow();
        var history = await db.AgentMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role != AgentMessageRole.Tool)
            .OrderByDescending(m => m.Sequence).Take(settings.HistoryMessages).ToListAsync(cancellationToken);
        var nextSequence = (await db.AgentMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (int?)m.Sequence, cancellationToken) ?? 0) + 1;
        var userMessage = AgentMessage.Create(conversationId, userId, nextSequence, AgentMessageRole.User, content.Trim(), now);
        db.AgentMessages.Add(userMessage);
        conversation.Touch(now);
        await db.SaveChangesAsync(cancellationToken);

        // 3. Build context.
        var policy = await policies.LoadCurrentAsync(userId, cancellationToken);
        var timeZone = policy?.Profile.TimeZone ?? "UTC";
        var messages = new List<LlmMessage>
        {
            new(LlmRole.System, SystemPrompt.Text),
            new(LlmRole.System, SystemPrompt.ContextBlock(BudgetPeriods.LocalDate(now, timeZone), timeZone,
                policy?.Snapshot.Currency ?? MoneyFormat.DefaultCurrency, policy?.Version.VersionNumber ?? 0)),
        };
        foreach (var m in history.OrderBy(m => m.Sequence))
        {
            messages.Add(m.Role == AgentMessageRole.User ? new LlmMessage(LlmRole.User, m.Content) : new LlmMessage(LlmRole.Assistant, WithCardContext(m)));
        }

        messages.Add(new LlmMessage(LlmRole.User, userMessage.Content));

        // 4. Loop.
        var context = new AgentToolContext(userId, conversationId, currentUser.CorrelationId);
        var tools = registry.Definitions();
        var cards = new List<CardRef>();
        var toolCalls = new List<ToolCallSummaryDto>();
        var successfulOutcomes = new List<ToolOutcome>();
        var llmRequests = 0;
        string? provider = null;
        string? finalText = null;
        string? code = null;
        LlmUsage? usage = null;

        using var turnTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TurnTimeoutSeconds));

        try
        {
            while (llmRequests < settings.MaxLlmRequestsPerTurn)
            {
                LlmResponse response;
                try
                {
                    response = await llm.CompleteAsync(messages, tools, new LlmOptions(settings.Temperature, settings.MaxOutputTokens), turnTimeout.Token);
                }
                catch (LlmUnavailableException ex) when (toolCalls.Count == 0)
                {
                    log.LogWarning(ex, "Agent unavailable for conversation {ConversationId}", conversationId);
                    return new DomainError(ErrorCodes.AgentUnavailable, "The assistant is busy right now. You can still build a cart manually.",
                        new Dictionary<string, object?> { ["retryAfterSeconds"] = (int)(ex.RetryAfter?.TotalSeconds ?? 30) });
                }

                llmRequests++;
                provider = response.Provider;
                usage = response.Usage;

                if (response.ToolCalls.Count == 0)
                {
                    finalText = response.Text;
                    break;
                }

                messages.Add(new LlmMessage(LlmRole.Assistant, response.Text, response.ToolCalls));
                foreach (var call in response.ToolCalls)
                {
                    if (toolCalls.Count >= settings.MaxToolCallsPerTurn)
                    {
                        messages.Add(new LlmMessage(LlmRole.Tool,
                            JsonDefaults.Serialize(ToolOutcome.Failure(ErrorCodes.AgentStepLimit, "Tool-call limit reached for this turn.").Envelope()),
                            ToolCallId: call.Id));
                        code = ErrorCodes.AgentStepLimit;
                        continue;
                    }

                    var result = await dispatcher.ExecuteAsync(call, context, turnTimeout.Token);
                    toolCalls.Add(new ToolCallSummaryDto(result.ToolName, result.Status, result.DurationMs, result.Outcome.ErrorCode));
                    if (result.Outcome is { Ok: true } ok)
                    {
                        successfulOutcomes.Add(ok);
                        if (ok.Card is { } card)
                        {
                            cards.Add(card);
                        }
                    }

                    messages.Add(new LlmMessage(LlmRole.Tool, result.ModelContent, ToolCallId: call.Id));
                }

                if (code == ErrorCodes.AgentStepLimit)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            finalText = "I ran out of time for this request. Here's what I have so far.";
            code = ErrorCodes.AgentStepLimit;
        }
        catch (LlmUnavailableException ex)
        {
            log.LogWarning(ex, "LLM became unavailable mid-turn for conversation {ConversationId}", conversationId);
            finalText = "The assistant became unavailable partway through. The cards below show what was done.";
        }

        if (finalText is null)
        {
            finalText = StepLimitText;
            code = ErrorCodes.AgentStepLimit;
        }

        finalText = ClaimsCheck(StripIds(finalText), successfulOutcomes);

        // 5. Persist ASSISTANT message with de-duplicated card refs (last wins per type+id).
        var deduped = cards
            .Select((c, i) => (c, i))
            .GroupBy(x => (x.c.Type, x.c.Id, Ids: x.c.Ids is null ? string.Empty : string.Join(',', x.c.Ids)))
            .Select(g => g.Last())
            .OrderBy(x => x.i)
            .Select(x => x.c)
            .ToList();

        var assistant = AgentMessage.Create(conversationId, userId, nextSequence + 1, AgentMessageRole.Assistant, finalText, clock.GetUtcNow(),
            cards: JsonDefaults.Serialize(deduped),
            tokenUsage: usage is null ? null : JsonDefaults.Serialize(new { usage.InputTokens, usage.OutputTokens, provider }));
        db.AgentMessages.Add(assistant);
        conversation.Touch(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        log.LogInformation(
            "Agent turn {ConversationId}/{Sequence} provider={Provider} llmRequests={LlmRequests} toolCalls={ToolCalls} intent={Intent}",
            conversationId, assistant.Sequence, provider, llmRequests, toolCalls.Count, PrimaryIntent(toolCalls));

        var hydrated = await hydrator.HydrateAsync(userId, deduped, cancellationToken);
        return new AgentTurnResponse(
            conversationId,
            ToDto(userMessage, null),
            ToDto(assistant, hydrated),
            toolCalls,
            new TurnLimitsDto(llmRequests, settings.MaxLlmRequestsPerTurn, toolCalls.Count, settings.MaxToolCallsPerTurn, provider),
            code);
    }

    /// <summary>
    /// AI_GUARDRAILS.md §4: if the text claims approval/payment without a matching tool result this turn, append a server note.
    /// Never edits model text silently.
    /// </summary>
    public string ClaimsCheck(string text, IReadOnlyList<ToolOutcome> outcomes)
    {
        if (!ClaimPattern().IsMatch(text))
        {
            return text;
        }

        var serialized = string.Join("\n", outcomes.Select(o => JsonDefaults.Serialize(o.Data)));
        var supported = serialized.Contains("\"COMPLETED\"", StringComparison.Ordinal) || serialized.Contains("\"APPROVED\"", StringComparison.Ordinal);
        if (supported)
        {
            return text;
        }

        log.LogWarning("AGENT_CLAIM_UNVERIFIED");
        return $"{text}\n\n_{ClaimNote}_";
    }

    /// <summary>
    /// Ids are for tool arguments, not people: the cards carry them. Removes "Product ID: …" lines and any other raw UUIDs.
    /// The ids stay available to the model on later turns through <see cref="WithCardContext"/>.
    /// </summary>
    public static string StripIds(string text)
    {
        var withoutLabels = IdLabelLinePattern().Replace(text, string.Empty);
        var withoutIds = InlineIdPattern().Replace(withoutLabels, string.Empty);
        return withoutIds == text ? text : ExtraSpacesPattern().Replace(withoutIds, " ").Trim();
    }

    private static string WithCardContext(AgentMessage message)
    {
        var refs = CardHydrator.Parse(message.Cards)
            .Select(c => (c.Type, Ids: c.Id is { } id ? [id] : c.Ids ?? []))
            .Where(c => c.Ids.Count > 0)
            .ToList();
        return refs.Count == 0
            ? message.Content
            : $"{message.Content}\n[server context: {string.Join("; ", refs.Select(c => $"{c.Type} {string.Join(",", c.Ids)}"))}]";
    }

    private static string PrimaryIntent(IReadOnlyList<ToolCallSummaryDto> calls) => calls.FirstOrDefault()?.Name switch
    {
        null => "CHAT",
        "SearchProducts" or "GetProductDetails" or "CreateShoppingCart" or "EvaluatePurchase" or "RequestPurchaseApproval" => "SHOP",
        "SearchMerchants" or "GetApprovedMerchants" => "COMPARE",
        "GetRemainingBudget" or "GetSpendingSummary" => "BUDGET_QUERY",
        "GetPurchaseStatus" => "STATUS_QUERY",
        "GetSpendingPolicies" => "POLICY_QUERY",
        _ => "CHAT",
    };

    private static AgentMessageDto ToDto(AgentMessage m, IReadOnlyList<HydratedCard>? cards) =>
        new(m.Id, m.Sequence, m.Role, m.Content, cards, m.CreatedAt);

    [GeneratedRegex(@"\b(approved|paid|purchase complete|charged|payment (is )?complete|captured)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClaimPattern();

    // A whole list/label line whose value is just an id, e.g. "   * **Product ID:** `7bf5…`".
    [GeneratedRegex(@"^[ \t]*(?:[-*][ \t]+)?\**[\w ]*\bID\**:?\**:?[ \t]*`?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}`?[ \t]*(?:\r?\n|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IdLabelLinePattern();

    [GeneratedRegex(@"[ \t]*\(?`?\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b`?\)?", RegexOptions.IgnoreCase)]
    private static partial Regex InlineIdPattern();

    [GeneratedRegex(@"(?<=\S)[ \t]{2,}")]
    private static partial Regex ExtraSpacesPattern();
}

public sealed class ConversationService(IAppDbContext db, CardHydrator hydrator, AuditWriter audit, TimeProvider clock)
{
    public async Task<ConversationDto> CreateAsync(Guid userId, string? title, CancellationToken cancellationToken)
    {
        var conversation = AgentConversation.Start(userId, title is { Length: > 120 } ? title[..120] : title, clock.GetUtcNow());
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(conversation);
    }

    public async Task<IReadOnlyList<ConversationDto>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Conversations.AsNoTracking().Where(c => c.UserId == userId && c.Status == ConversationStatus.Active)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt).Take(100).ToListAsync(cancellationToken))
        .Select(ToDto).ToList();

    public async Task<Result<IReadOnlyList<AgentMessageDto>>> MessagesAsync(Guid userId, Guid conversationId, int? afterSequence, CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId && c.UserId == userId && c.Status == ConversationStatus.Active, cancellationToken))
        {
            return DomainError.NotFound("Conversation");
        }

        var messages = await db.AgentMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.UserId == userId && m.Role != AgentMessageRole.Tool && m.Sequence > (afterSequence ?? 0))
            .OrderBy(m => m.Sequence).Take(200).ToListAsync(cancellationToken);

        var result = new List<AgentMessageDto>(messages.Count);
        foreach (var m in messages)
        {
            var cards = m.Role == AgentMessageRole.Assistant ? await hydrator.HydrateAsync(userId, CardHydrator.Parse(m.Cards), cancellationToken) : null;
            result.Add(new AgentMessageDto(m.Id, m.Sequence, m.Role, m.Content, cards, m.CreatedAt));
        }

        return result;
    }

    /// <summary>
    /// DELETE /agent/conversations/{id}: archives (soft-deletes) the conversation. It disappears from the list and its
    /// messages can no longer be read or appended to. Not found, not owned and already archived are all 404 (SEC-11).
    /// </summary>
    public async Task<Result<Unit>> ArchiveAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            c => c.Id == conversationId && c.UserId == userId && c.Status == ConversationStatus.Active, cancellationToken);
        if (conversation is null)
        {
            return DomainError.NotFound("Conversation");
        }

        conversation.Archive(clock.GetUtcNow());
        audit.Write(AuditActions.ConversationArchived, "AgentConversation", conversation.Id, userId, ActorType.User);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }

    private static ConversationDto ToDto(AgentConversation c) => new(c.Id, c.Title, c.Status, c.LastMessageAt, c.CreatedAt);
}
