using FluentValidation;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Carts;
using Guardrail.Application.Evaluation;
using Guardrail.Application.Purchases;
using Guardrail.Domain.Common;
using Guardrail.Domain.Shopping;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Application.Agent.Tools;

// ---------------------------------------------------------------- 7. CreateShoppingCart

public sealed record ToolCartItem(Guid? ProductId = null, int? Quantity = null);

public sealed record CreateShoppingCartArgs(Guid? MerchantId = null, IReadOnlyList<ToolCartItem>? Items = null, Guid? SupersedesCartId = null);

public sealed class CreateShoppingCartValidator : AbstractValidator<CreateShoppingCartArgs>
{
    public CreateShoppingCartValidator()
    {
        RuleFor(a => a.MerchantId).NotEmpty();
        RuleFor(a => a.Items).NotEmpty().Must(i => i!.Count <= ShoppingCart.MaxDistinctItems).WithMessage("1–30 items.");
        RuleForEach(a => a.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.Quantity).NotNull().InclusiveBetween(1, ShoppingCart.MaxQuantity);
        });
    }
}

/// <summary>
/// Creates an OPEN cart at one merchant. Prices are set by the server; there is no price field (T5).
/// Merchant status is not checked here — the policy engine decides.
/// </summary>
public sealed class CreateShoppingCartTool(CartService carts, IAppDbContext db, TimeProvider clock, CreateShoppingCartValidator validator)
    : AgentTool<CreateShoppingCartArgs>(validator)
{
    public override string Name => "CreateShoppingCart";

    public override string Description => "Create a cart at ONE merchant with catalogue products. Prices are set by the server.";

    protected override string SchemaJson => """
        {"type":"object","additionalProperties":false,"required":["merchantId","items"],"properties":{
          "merchantId":{"type":"string","format":"uuid"},
          "items":{"type":"array","minItems":1,"maxItems":30,"items":{
            "type":"object","additionalProperties":false,"required":["productId","quantity"],
            "properties":{"productId":{"type":"string","format":"uuid"},"quantity":{"type":"integer","minimum":1,"maximum":99}}}},
          "supersedesCartId":{"type":"string","format":"uuid"}}}
        """;

    protected override async Task<ToolOutcome> HandleAsync(CreateShoppingCartArgs args, AgentToolContext context, CancellationToken cancellationToken)
    {
        var sessionId = await SessionIdAsync(context, cancellationToken);
        var result = await carts.CreateAsync(context.UserId,
            new CreateCartCommand(args.MerchantId!.Value, args.Items!.Select(i => new CartLine(i.ProductId!.Value, i.Quantity!.Value)).ToList(),
                args.SupersedesCartId),
            ActorType.Agent, sessionId, cancellationToken);
        if (!result.IsSuccess)
        {
            return ToolOutcome.From(result.Error!);
        }

        var cart = result.Value!;
        return ToolOutcome.Success(new
        {
            cartId = cart.Id,
            cartVersion = cart.Version,
            merchantId = cart.MerchantId,
            merchantName = cart.MerchantName,
            status = cart.Status,
            currency = cart.Currency,
            items = cart.Items.Select(i => new
            {
                productId = i.ProductId, name = i.Name, unitPrice = i.UnitPrice, quantity = i.Quantity, lineTotal = i.LineTotal, categoryCode = i.CategoryCode,
            }),
            total = cart.Total,
        }, new CardRef(CardTypes.Cart, cart.Id));
    }

    private async Task<Guid> SessionIdAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        var existing = await db.ShoppingSessions
            .Where(s => s.UserId == context.UserId && s.ConversationId == context.ConversationId && s.Status == ShoppingSessionStatus.Active)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            return id;
        }

        var session = ShoppingSession.Start(context.UserId, context.ConversationId, clock.GetUtcNow());
        db.ShoppingSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }
}

// ---------------------------------------------------------------- 8. EvaluatePurchase

public sealed record CartIdArgs(Guid? CartId = null);

public sealed class CartIdValidator : AbstractValidator<CartIdArgs>
{
    public CartIdValidator() => RuleFor(a => a.CartId).NotEmpty();
}

public sealed class EvaluatePurchaseTool(EvaluationService evaluations, CartIdValidator validator) : AgentTool<CartIdArgs>(validator)
{
    public override string Name => "EvaluatePurchase";

    public override string Description =>
        "Ask the deterministic Spending Policy Engine to evaluate a cart. Its decision is final; explain it, never override it.";

    protected override string SchemaJson => """
        {"type":"object","additionalProperties":false,"required":["cartId"],"properties":{"cartId":{"type":"string","format":"uuid"}}}
        """;

    protected override async Task<ToolOutcome> HandleAsync(CartIdArgs args, AgentToolContext context, CancellationToken cancellationToken)
    {
        var result = await evaluations.EvaluateCartAsync(context.UserId, args.CartId!.Value, ActorType.Agent, cancellationToken);
        return result.IsSuccess
            ? ToolOutcome.Success(result.Value!, new CardRef(CardTypes.Evaluation, result.Value!.EvaluationId))
            : ToolOutcome.From(result.Error!);
    }
}

// ---------------------------------------------------------------- 9. RequestPurchaseApproval

public sealed record RequestApprovalArgs(Guid? CartId = null, Guid? EvaluationId = null, string? Note = null);

public sealed class RequestApprovalValidator : AbstractValidator<RequestApprovalArgs>
{
    public RequestApprovalValidator()
    {
        RuleFor(a => a.CartId).NotEmpty();
        RuleFor(a => a.EvaluationId).NotEmpty();
        RuleFor(a => a.Note).MaximumLength(200);
    }
}

/// <summary>Invokes the same CreatePurchase use case as POST /purchases (BR-40/41). It cannot approve anything.</summary>
public sealed class RequestPurchaseApprovalTool(PurchaseService purchases, RequestApprovalValidator validator) : AgentTool<RequestApprovalArgs>(validator)
{
    public override string Name => "RequestPurchaseApproval";

    public override string Description =>
        "Create a purchase from an evaluated cart. If approval is required, the USER must approve it in the app; you cannot.";

    protected override string SchemaJson => """
        {"type":"object","additionalProperties":false,"required":["cartId","evaluationId"],"properties":{
          "cartId":{"type":"string","format":"uuid"},"evaluationId":{"type":"string","format":"uuid"},"note":{"type":"string","maxLength":200}}}
        """;

    protected override async Task<ToolOutcome> HandleAsync(RequestApprovalArgs args, AgentToolContext context, CancellationToken cancellationToken)
    {
        var result = await purchases.CreateAsync(context.UserId, new CreatePurchaseCommand(args.CartId!.Value, args.EvaluationId!.Value),
            ActorType.Agent, cancellationToken);
        if (!result.IsSuccess)
        {
            return ToolOutcome.From(result.Error!);
        }

        var created = result.Value!;
        var awaiting = created.ApprovalRequestId is not null;
        return ToolOutcome.Success(new
        {
            purchaseId = created.Purchase.Id,
            purchaseStatus = created.Purchase.Status,
            approvalRequestId = created.ApprovalRequestId,
            approvalExpiresAt = created.ApprovalExpiresAt,
            amount = created.Purchase.Amount,
            currency = created.Purchase.Currency,
            nextStep = awaiting ? "USER_MUST_APPROVE_IN_APP" : "USER_MUST_CHECKOUT_WITH_PAYPAL",
        }, awaiting ? new CardRef(CardTypes.Approval, created.ApprovalRequestId) : new CardRef(CardTypes.Purchase, created.Purchase.Id));
    }
}
