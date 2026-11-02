namespace Guardrail.Application.Abstractions;

public sealed class ApprovalOptions
{
    public const string Section = "Approvals";

    public int TtlMinutes { get; set; } = 30;

    public int StepUpMaxFailures { get; set; } = 5;

    public int StepUpWindowMinutes { get; set; } = 15;
}

public sealed class EvaluationOptions
{
    public const string Section = "Evaluations";

    public int MaxAgeMinutes { get; set; } = 15;
}

public sealed class CheckoutOptions
{
    public const string Section = "Checkout";

    public int AllowedPurchaseCheckoutMinutes { get; set; } = 30;

    public int PayPalApprovalTimeoutHours { get; set; } = 3;

    public int AuthorizationMaxOpenMinutes { get; set; } = 30;

    public int MaxFailedOrderCreates { get; set; } = 3;

    /// <summary>AUTHORIZE (default, ADR-005) or CAPTURE fallback.</summary>
    public string Intent { get; set; } = "AUTHORIZE";

    public string ReturnUrl { get; set; } = "http://localhost:5173/checkout/return";

    public string CancelUrl { get; set; } = "http://localhost:5173/checkout/cancel";

    public string BrandName { get; set; } = "Guardrail AI";
}

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public int MaxLlmRequestsPerTurn { get; set; } = 6;

    public int MaxToolCallsPerTurn { get; set; } = 16;

    public int TurnTimeoutSeconds { get; set; } = 60;

    public int HistoryMessages { get; set; } = 20;

    public int MaxToolResultBytes { get; set; } = 8 * 1024;

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 1024;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public int MaxFailedLogins { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 5;
}

public sealed class SeedOptions
{
    public const string Section = "Seed";

    public bool Catalogue { get; set; } = true;

    public bool CreateDemoUser { get; set; }

    public string DemoUserEmail { get; set; } = "demo@guardrail.ai";

    public string? DemoUserPassword { get; set; }

    public string DemoUserTimeZone { get; set; } = "America/New_York";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}

public sealed class DemoOptions
{
    public const string Section = "Demo";

    public bool ResetEnabled { get; set; }
}
