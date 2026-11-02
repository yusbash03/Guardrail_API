using Guardrail.Domain.Common;

namespace Guardrail.Domain.Agent;

public sealed class AgentConversation
{
    private AgentConversation()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public ConversationStatus Status { get; private set; }

    public DateTimeOffset? LastMessageAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AgentConversation Start(Guid userId, string? title, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
        Status = ConversationStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Touch(DateTimeOffset now)
    {
        LastMessageAt = now;
        UpdatedAt = now;
    }

    /// <summary>User-initiated delete. Soft: carts, purchases and approvals the conversation produced keep their history.</summary>
    public void Archive(DateTimeOffset now)
    {
        Status = ConversationStatus.Archived;
        UpdatedAt = now;
    }
}

public sealed class AgentMessage
{
    private AgentMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid UserId { get; private set; }

    public int Sequence { get; private set; }

    public AgentMessageRole Role { get; private set; }

    public string Content { get; private set; } = string.Empty;

    public string? ToolCallId { get; private set; }

    /// <summary>jsonb CardRef[] — references only; hydrated from the database at read time.</summary>
    public string Cards { get; private set; } = "[]";

    public string? TokenUsage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AgentMessage Create(
        Guid conversationId, Guid userId, int sequence, AgentMessageRole role, string content, DateTimeOffset now,
        string? toolCallId = null, string cards = "[]", string? tokenUsage = null) => new()
    {
        Id = Ids.New(),
        ConversationId = conversationId,
        UserId = userId,
        Sequence = sequence,
        Role = role,
        Content = content,
        ToolCallId = toolCallId,
        Cards = cards,
        TokenUsage = tokenUsage,
        CreatedAt = now,
    };
}

public sealed class AgentExecution
{
    private AgentExecution()
    {
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid? MessageId { get; private set; }

    public Guid UserId { get; private set; }

    public string ToolName { get; private set; } = string.Empty;

    public string ToolCallId { get; private set; } = string.Empty;

    public string Arguments { get; private set; } = "{}";

    public string? Result { get; private set; }

    public AgentExecutionStatus Status { get; private set; }

    public string? ErrorCode { get; private set; }

    public int DurationMs { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AgentExecution Record(
        Guid id, Guid conversationId, Guid userId, string toolName, string toolCallId, string argumentsJson, string? resultJson,
        AgentExecutionStatus status, string? errorCode, int durationMs, DateTimeOffset now) => new()
    {
        Id = id,
        ConversationId = conversationId,
        UserId = userId,
        ToolName = toolName,
        ToolCallId = toolCallId,
        Arguments = argumentsJson,
        Result = resultJson,
        Status = status,
        ErrorCode = errorCode,
        DurationMs = durationMs,
        CreatedAt = now,
    };
}
