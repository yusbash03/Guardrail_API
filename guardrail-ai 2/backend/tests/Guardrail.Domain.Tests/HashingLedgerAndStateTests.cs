using Guardrail.Domain.Authorization;
using Guardrail.Domain.Common;
using Guardrail.Domain.Ledger;
using Guardrail.Domain.Policy;
using Guardrail.Domain.Shopping;
using static Guardrail.Domain.Tests.PolicyFixtures;

namespace Guardrail.Domain.Tests;

public class SnapshotHashTests
{
    private static CartSnapshot GoldenCart() => new(
        Guid.Parse("0193f0a2-0000-7000-8000-000000000001"), 3,
        Guid.Parse("0193f0a2-0000-7000-8000-0000000000aa"), "FreshMart", "USD",
        [
            new CartSnapshotItem(Guid.Parse("0193f0a2-0000-7000-8000-000000000102"), "FM-EGG-12", "Large Eggs", "GROCERIES", [], 3.99m, 1, 3.99m, true, 10),
            new CartSnapshotItem(Guid.Parse("0193f0a2-0000-7000-8000-000000000101"), "FM-MLK-1G", "Whole Milk", "GROCERIES", ["dairy"], 4.29m, 1, 4.29m, true, 10),
        ],
        8.28m);

    [Fact]
    public void CanonicalJson_MatchesDocumentedShape()
    {
        GoldenCart().ToCanonicalJson().ShouldBe(
            "{\"cartId\":\"0193f0a2-0000-7000-8000-000000000001\",\"cartVersion\":3,\"currency\":\"USD\",\"items\":[" +
            "{\"categoryCode\":\"GROCERIES\",\"lineTotal\":\"4.29\",\"productId\":\"0193f0a2-0000-7000-8000-000000000101\",\"quantity\":1,\"sku\":\"FM-MLK-1G\",\"unitPrice\":\"4.29\"}," +
            "{\"categoryCode\":\"GROCERIES\",\"lineTotal\":\"3.99\",\"productId\":\"0193f0a2-0000-7000-8000-000000000102\",\"quantity\":1,\"sku\":\"FM-EGG-12\",\"unitPrice\":\"3.99\"}]," +
            "\"merchantId\":\"0193f0a2-0000-7000-8000-0000000000aa\",\"total\":\"8.28\"}");
    }

    [Fact]
    public void Hash_GoldenValue_IsPinned()
    {
        // Golden file: if this changes, frontend/backend/test fixtures have drifted (APPROVAL_WORKFLOW.md §2).
        var hash = GoldenCart().ComputeHash();
        hash.Length.ShouldBe(64);
        hash.ShouldBe(CanonicalJson.Sha256Hex(GoldenCart().ToCanonicalJson()));
        hash.ShouldBe("707da64df332f8f3e62df457d2e924f960a1c758bbbd55099195bc956c26099b");
    }

    [Fact]
    public void Hash_ItemOrderPermutation_SameHash()
    {
        var cart = GoldenCart();
        var reversed = cart with { Items = cart.Items.Reverse().ToList() };
        reversed.ComputeHash().ShouldBe(cart.ComputeHash());
    }

    [Fact]
    public void Hash_QuantityChange_DifferentHash()
    {
        var cart = GoldenCart();
        var changed = cart with { Items = [cart.Items[0] with { Quantity = 2, LineTotal = 7.98m }, cart.Items[1]], Total = 12.27m };
        changed.ComputeHash().ShouldNotBe(cart.ComputeHash());
    }

    [Fact]
    public void Hash_VersionChange_DifferentHash()
    {
        var cart = GoldenCart();
        (cart with { CartVersion = 4 }).ComputeHash().ShouldNotBe(cart.ComputeHash());
    }
}

public class LedgerTests
{
    private static SpendingLedgerEntry Entry(LedgerTransition t, decimal amount = 43m, LedgerBucket? from = null, int offsetSeconds = 0) =>
        SpendingLedgerEntry.Create(Guid.NewGuid(), Guid.NewGuid(), null, CategoryCodes.Groceries, amount, "USD", t,
            new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), LedgerSource.System, string.Empty, Now.AddSeconds(offsetSeconds), from);

    [Theory]
    [InlineData(LedgerTransition.Propose, LedgerBucket.None, LedgerBucket.Proposed)]
    [InlineData(LedgerTransition.Reserve, LedgerBucket.None, LedgerBucket.Reserved)]
    [InlineData(LedgerTransition.ConfirmReservation, LedgerBucket.Proposed, LedgerBucket.Reserved)]
    [InlineData(LedgerTransition.Authorize, LedgerBucket.Reserved, LedgerBucket.Authorized)]
    [InlineData(LedgerTransition.Capture, LedgerBucket.Authorized, LedgerBucket.Captured)]
    [InlineData(LedgerTransition.Void, LedgerBucket.Authorized, LedgerBucket.Released)]
    [InlineData(LedgerTransition.Fail, LedgerBucket.Authorized, LedgerBucket.Failed)]
    [InlineData(LedgerTransition.Refund, LedgerBucket.Captured, LedgerBucket.Refunded)]
    public void TransitionTable_MatchesDomainModel(LedgerTransition transition, LedgerBucket from, LedgerBucket to)
    {
        var entry = Entry(transition);
        entry.FromBucket.ShouldBe(from);
        entry.ToBucket.ShouldBe(to);
    }

    [Theory]
    [InlineData(LedgerBucket.Proposed)]
    [InlineData(LedgerBucket.Reserved)]
    public void Release_FromProposedOrReserved(LedgerBucket from) =>
        Entry(LedgerTransition.Release, from: from).ToBucket.ShouldBe(LedgerBucket.Released);

    [Fact]
    public void Release_FromAuthorized_Rejected() =>
        Should.Throw<DomainException>(() => Entry(LedgerTransition.Release, from: LedgerBucket.Authorized))
            .Error.Code.ShouldBe(ErrorCodes.LedgerInvalidTransition);

    [Fact]
    public void Committed_FollowsLifecycle()
    {
        var propose = Entry(LedgerTransition.Propose, offsetSeconds: 0);
        LedgerRules.CommittedOf([propose]).ShouldBe(43m);

        var confirm = Entry(LedgerTransition.ConfirmReservation, offsetSeconds: 1);
        var authorize = Entry(LedgerTransition.Authorize, offsetSeconds: 2);
        var capture = Entry(LedgerTransition.Capture, offsetSeconds: 3);
        LedgerRules.CommittedOf([propose, confirm, authorize, capture]).ShouldBe(43m);
        LedgerRules.Balance([propose, confirm, authorize, capture], LedgerBucket.Captured).ShouldBe(43m);
        LedgerRules.CurrentBucket([capture, propose, authorize, confirm]).ShouldBe(LedgerBucket.Captured);

        var released = Entry(LedgerTransition.Release, from: LedgerBucket.Proposed, offsetSeconds: 1);
        LedgerRules.CommittedOf([propose, released]).ShouldBe(0m); // reject after PROPOSE
    }

    [Fact]
    public void NonPositiveAmount_Throws() => Should.Throw<ArgumentOutOfRangeException>(() => Entry(LedgerTransition.Reserve, 0m));
}

public class StateMachineTests
{
    private static readonly (PurchaseStatus From, PurchaseStatus To)[] Legal =
    [
        (PurchaseStatus.AwaitingApproval, PurchaseStatus.ReadyForCheckout),
        (PurchaseStatus.AwaitingApproval, PurchaseStatus.Rejected),
        (PurchaseStatus.AwaitingApproval, PurchaseStatus.Expired),
        (PurchaseStatus.AwaitingApproval, PurchaseStatus.Cancelled),
        (PurchaseStatus.ReadyForCheckout, PurchaseStatus.CheckoutPending),
        (PurchaseStatus.ReadyForCheckout, PurchaseStatus.Cancelled),
        (PurchaseStatus.ReadyForCheckout, PurchaseStatus.Expired),
        (PurchaseStatus.CheckoutPending, PurchaseStatus.PayerApproved),
        (PurchaseStatus.CheckoutPending, PurchaseStatus.Expired),
        (PurchaseStatus.CheckoutPending, PurchaseStatus.Cancelled),
        (PurchaseStatus.PayerApproved, PurchaseStatus.PaymentAuthorized),
        (PurchaseStatus.PayerApproved, PurchaseStatus.Failed),
        (PurchaseStatus.PaymentAuthorized, PurchaseStatus.Completed),
        (PurchaseStatus.PaymentAuthorized, PurchaseStatus.CapturePending),
        (PurchaseStatus.PaymentAuthorized, PurchaseStatus.Failed),
        (PurchaseStatus.CapturePending, PurchaseStatus.Completed),
        (PurchaseStatus.CapturePending, PurchaseStatus.Failed),
        (PurchaseStatus.Completed, PurchaseStatus.PartiallyRefunded),
        (PurchaseStatus.Completed, PurchaseStatus.Refunded),
    ];

    [Fact]
    public void Purchase_DocumentedTransitionsAreLegal()
    {
        foreach (var (from, to) in Legal)
        {
            Purchase.CanTransition(from, to).ShouldBeTrue($"{from} → {to}");
        }
    }

    [Fact]
    public void Purchase_TerminalStatesHaveNoExits()
    {
        foreach (var from in new[] { PurchaseStatus.Rejected, PurchaseStatus.Cancelled, PurchaseStatus.Expired, PurchaseStatus.Failed, PurchaseStatus.Refunded })
        {
            foreach (var to in Enum.GetValues<PurchaseStatus>())
            {
                Purchase.CanTransition(from, to).ShouldBeFalse($"{from} → {to}");
            }
        }
    }

    [Fact]
    public void Purchase_IllegalPairs_Rejected()
    {
        Purchase.CanTransition(PurchaseStatus.AwaitingApproval, PurchaseStatus.CheckoutPending).ShouldBeFalse();
        Purchase.CanTransition(PurchaseStatus.ReadyForCheckout, PurchaseStatus.Completed).ShouldBeFalse();
        Purchase.CanTransition(PurchaseStatus.PaymentAuthorized, PurchaseStatus.Cancelled).ShouldBeFalse();
        Purchase.CanTransition(PurchaseStatus.Completed, PurchaseStatus.Failed).ShouldBeFalse();
    }

    [Fact]
    public void Payment_RankIsForwardOnly()
    {
        Payments.Payment.Rank(PaymentStatus.Completed).ShouldBeGreaterThan(Payments.Payment.Rank(PaymentStatus.CapturePending));
        Payments.Payment.Rank(PaymentStatus.Authorized).ShouldBeGreaterThan(Payments.Payment.Rank(PaymentStatus.Approved));
        Payments.Payment.Rank(PaymentStatus.Created).ShouldBe(Payments.Payment.Rank(PaymentStatus.PayerActionRequired));
    }

    [Fact]
    public void EnumText_UsesUpperSnakeCase()
    {
        EnumText.ToText(PurchaseStatus.PaymentAuthorized).ShouldBe("PAYMENT_AUTHORIZED");
        EnumText.ToText(LedgerSource.PaypalWebhook).ShouldBe("PAYPAL_WEBHOOK");
        EnumText.ToText(ActorType.Paypal).ShouldBe("PAYPAL");
        EnumText.Parse<PaymentStatus>("PAYER_ACTION_REQUIRED").ShouldBe(PaymentStatus.PayerActionRequired);
        EnumText.TryParse<PaymentStatus>("NOPE", out _).ShouldBeFalse();
    }
}

public class FinalGuardrailCheckTests
{
    [Fact]
    public void Passes_WhenNothingChanged()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 43m));
        var result = FinalGuardrailCheck.Run(DemoPolicy(), cart, cart.ComputeHash(), 43m, "USD", 43m, "USD", Now);
        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Fails_WhenMerchantBlockedAfterApproval()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 43m));
        var policy = DemoPolicy(blocked: [new MerchantRule(Rule(20), FreshMart)]);
        var result = FinalGuardrailCheck.Run(policy, cart, cart.ComputeHash(), 43m, "USD", 43m, "USD", Now);
        result.Passed.ShouldBeFalse();
        result.Reasons.ShouldContain(r => r.Code == ReasonCodes.MerchantBlocked);
    }

    [Fact]
    public void Fails_OnAmountMismatchOrChangedCart()
    {
        var cart = Cart(FreshMart, "FreshMart", (CategoryCodes.Groceries, 43m));
        var result = FinalGuardrailCheck.Run(DemoPolicy(), cart, "deadbeef", 43m, "USD", 42m, "USD", Now);
        result.Reasons.Select(r => r.Code).ShouldBe(["CART_CHANGED", ErrorCodes.AmountMismatch]);
    }
}

public class ApprovalTests
{
    [Fact]
    public void Consume_IsSingleUse()
    {
        var approval = NewApproval();
        approval.Approve(Now);
        approval.TryConsume(Now, approval.CartSnapshotHash, approval.Amount).ShouldBeTrue();
        approval.TryConsume(Now, approval.CartSnapshotHash, approval.Amount).ShouldBeFalse();
    }

    [Fact]
    public void CheckCanApprove_WrongHash_Mismatch()
    {
        var approval = NewApproval();
        approval.CheckCanApprove(approval.UserId, "other", Now, approval.PolicyVersionId, approval.CartSnapshotHash)!
            .Code.ShouldBe(ErrorCodes.ApprovalSnapshotMismatch);
    }

    [Fact]
    public void CheckCanApprove_PolicyChanged_Invalidated()
    {
        var approval = NewApproval();
        var error = approval.CheckCanApprove(approval.UserId, approval.CartSnapshotHash, Now, Guid.NewGuid(), approval.CartSnapshotHash)!;
        error.Code.ShouldBe(ErrorCodes.ApprovalInvalidated);
        error.Data!["reason"].ShouldBe(InvalidationReasons.PolicyVersionChanged);
    }

    [Fact]
    public void CheckCanApprove_Expired()
    {
        var approval = NewApproval();
        approval.CheckCanApprove(approval.UserId, approval.CartSnapshotHash, Now.AddMinutes(31), approval.PolicyVersionId, approval.CartSnapshotHash)!
            .Code.ShouldBe(ErrorCodes.ApprovalExpired);
    }

    private static ApprovalRequest NewApproval()
    {
        var merchant = Catalogue.Merchant.Create(FreshMart, "freshmart", "FreshMart", null, null, [CategoryCodes.Groceries], Now);
        var cart = ShoppingCart.Create(Guid.NewGuid(), merchant, ActorType.User, null, Now);
        var evaluation = PolicyEvaluation.Record(cart.UserId, cart.Id, 1, "hash", "{}", Guid.NewGuid(), PolicyDecisionType.RequiresApproval,
            "[]", 43m, "USD", "[]", ActorType.User, Now);
        var purchase = Purchase.Create(cart, evaluation, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), Now, TimeSpan.FromMinutes(30));
        return ApprovalRequest.CreateFor(purchase, evaluation, ActorType.Agent, Now, TimeSpan.FromMinutes(30));
    }
}
