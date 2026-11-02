using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrail.Domain.Common;
using Guardrail.Domain.Spending;

namespace Guardrail.Domain.Policy;

/// <summary>Maps between spending_policies rows, the engine's PolicySnapshot and the wire PolicyDocument.</summary>
public static class PolicyMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly string[] DayCodes = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static string DayCode(DayOfWeek day) => DayCodes[(int)day];

    public static bool TryParseDay(string code, out DayOfWeek day)
    {
        var index = Array.IndexOf(DayCodes, code);
        day = (DayOfWeek)Math.Max(index, 0);
        return index >= 0;
    }

    /// <summary>BR-12: defaults for policy v1.</summary>
    public static PolicyDocument Defaults() => new()
    {
        ApprovalThreshold = new AmountDocument("0.00"),
        MerchantRules = new MerchantRulesDocument([], [], EnumText.ToText(RuleAction.Block)),
        ProductRestrictions =
        [
            new ProductRestrictionDocument(null, new ProductMatchDocument("CATEGORY", CategoryCodes.Alcohol), "BLOCK", "Never buy alcohol"),
        ],
        UnbudgetedCategoryAction = EnumText.ToText(RuleAction.RequireApproval),
    };

    // ------------------------------------------------------------------ rows → snapshot

    public static PolicySnapshot ToSnapshot(PolicyVersion version, string currency, string timeZone)
    {
        AmountRule? threshold = null, max = null;
        ActionRule? newMerchant = null, unbudgeted = null;
        DuplicatePurchaseRule? duplicate = null;
        var limits = new List<SpendingLimitRule>();
        var budgets = new List<CategoryBudgetRule>();
        var approved = new List<MerchantRule>();
        var blocked = new List<MerchantRule>();
        var restrictions = new List<ProductRestrictionRule>();
        var times = new List<TimeRestrictionRule>();

        foreach (var rule in version.Rules.Where(r => r.IsEnabled).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id))
        {
            var p = JsonNode.Parse(rule.Parameters)?.AsObject() ?? [];
            switch (rule.RuleType)
            {
                case PolicyRuleType.ApprovalThreshold:
                    threshold = new AmountRule(rule.Id, Decimal(p, "amount"));
                    break;
                case PolicyRuleType.MaxTransactionAmount:
                    max = new AmountRule(rule.Id, Decimal(p, "amount"));
                    break;
                case PolicyRuleType.SpendingLimit:
                    limits.Add(new SpendingLimitRule(rule.Id, Enum<BudgetPeriodType>(p, "period"), Decimal(p, "limit")));
                    break;
                case PolicyRuleType.CategoryBudget:
                    budgets.Add(new CategoryBudgetRule(rule.Id, rule.CategoryCode!, Enum<BudgetPeriodType>(p, "period"), Decimal(p, "limit")));
                    break;
                case PolicyRuleType.MerchantAllow:
                    approved.Add(new MerchantRule(rule.Id, rule.MerchantId!.Value));
                    break;
                case PolicyRuleType.MerchantBlock:
                    blocked.Add(new MerchantRule(rule.Id, rule.MerchantId!.Value));
                    break;
                case PolicyRuleType.NewMerchantPolicy:
                    newMerchant = new ActionRule(rule.Id, Enum<RuleAction>(p, "action"));
                    break;
                case PolicyRuleType.UnbudgetedCategoryPolicy:
                    unbudgeted = new ActionRule(rule.Id, Enum<RuleAction>(p, "action"));
                    break;
                case PolicyRuleType.ProductRestriction:
                    restrictions.Add(new ProductRestrictionRule(rule.Id, Enum<ProductMatchType>(p, "matchType"),
                        p["matchValue"]!.GetValue<string>(), Enum<RuleAction>(p, "action"), p["note"]?.GetValue<string>()));
                    break;
                case PolicyRuleType.DuplicatePurchase:
                    duplicate = new DuplicatePurchaseRule(rule.Id, p["windowHours"]!.GetValue<int>(),
                        Enum<DuplicateScope>(p, "scope"), Enum<RuleAction>(p, "action"));
                    break;
                case PolicyRuleType.TimeRestriction:
                    times.Add(new TimeRestrictionRule(rule.Id,
                        p["daysOfWeek"]!.AsArray().Select(d => TryParseDay(d!.GetValue<string>(), out var day) ? day : DayOfWeek.Monday).ToList(),
                        TimeOnly.ParseExact(p["startLocalTime"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
                        TimeOnly.ParseExact(p["endLocalTime"]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture),
                        Enum<RuleAction>(p, "action")));
                    break;
            }
        }

        return new PolicySnapshot(version.Id, version.VersionNumber, currency, timeZone, threshold, max, limits, budgets,
            approved, blocked, newMerchant, restrictions, duplicate, times, unbudgeted);
    }

    // ------------------------------------------------------------------ snapshot → document

    public static PolicyDocument ToDocument(PolicySnapshot s) => new()
    {
        PolicyVersionId = s.PolicyVersionId,
        VersionNumber = s.VersionNumber,
        Currency = s.Currency,
        TimeZone = s.TimeZone,
        ApprovalThreshold = s.ApprovalThreshold is null ? null : new AmountDocument(MoneyFormat.Format(s.ApprovalThreshold.Amount)),
        MaxTransactionAmount = s.MaxTransactionAmount is null ? null : new AmountDocument(MoneyFormat.Format(s.MaxTransactionAmount.Amount)),
        SpendingLimits = s.SpendingLimits.Select(l => new SpendingLimitDocument(EnumText.ToText(l.Period), MoneyFormat.Format(l.Limit))).ToList(),
        CategoryBudgets = s.CategoryBudgets
            .Select(b => new CategoryBudgetDocument(b.CategoryCode, EnumText.ToText(b.Period), MoneyFormat.Format(b.Limit))).ToList(),
        MerchantRules = new MerchantRulesDocument(
            s.ApprovedMerchants.Select(m => m.MerchantId).ToList(),
            s.BlockedMerchants.Select(m => m.MerchantId).ToList(),
            EnumText.ToText(s.NewMerchantPolicy?.Action ?? RuleAction.Block)),
        ProductRestrictions = s.ProductRestrictions.Select(r => new ProductRestrictionDocument(
            r.RuleId.ToString("D"), new ProductMatchDocument(EnumText.ToText(r.MatchType), r.MatchValue), EnumText.ToText(r.Action), r.Note)).ToList(),
        DuplicatePurchase = s.DuplicatePurchase is null
            ? null
            : new DuplicatePurchaseDocument(true, s.DuplicatePurchase.WindowHours, EnumText.ToText(s.DuplicatePurchase.Scope),
                EnumText.ToText(s.DuplicatePurchase.Action)),
        TimeRestrictions = s.TimeRestrictions.Select(t => new TimeRestrictionDocument(
            t.RuleId.ToString("D"), t.DaysOfWeek.Select(DayCode).ToList(), t.StartLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            t.EndLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture), EnumText.ToText(t.Action))).ToList(),
        UnbudgetedCategoryAction = EnumText.ToText(s.UnbudgetedCategoryPolicy?.Action ?? RuleAction.Allow),
    };

    // ------------------------------------------------------------------ document → rows (document must be validated)

    public static IEnumerable<SpendingPolicy> ToRules(PolicyDocument doc, Guid versionId, Guid userId, DateTimeOffset now)
    {
        // Rows are created with increasing timestamps so the snapshot keeps document order.
        var tick = 0;
        DateTimeOffset Next() => now.AddTicks(tick++);

        if (doc.ApprovalThreshold is { } threshold)
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.ApprovalThreshold, Params(("amount", threshold.Amount)), Next());
        }

        if (doc.MaxTransactionAmount is { } max)
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.MaxTransactionAmount, Params(("amount", max.Amount)), Next());
        }

        foreach (var limit in doc.SpendingLimits)
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.SpendingLimit,
                Params(("period", limit.Period), ("limit", limit.Limit)), Next());
        }

        foreach (var budget in doc.CategoryBudgets)
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.CategoryBudget,
                Params(("period", budget.Period), ("limit", budget.Limit)), Next(), categoryCode: budget.CategoryCode);
        }

        foreach (var merchantId in doc.MerchantRules.ApprovedMerchantIds.Distinct())
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.MerchantAllow, "{}", Next(), merchantId: merchantId);
        }

        foreach (var merchantId in doc.MerchantRules.BlockedMerchantIds.Distinct())
        {
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.MerchantBlock, "{}", Next(), merchantId: merchantId);
        }

        yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.NewMerchantPolicy,
            Params(("action", doc.MerchantRules.NewMerchantAction)), Next());

        foreach (var restriction in doc.ProductRestrictions)
        {
            var type = EnumText.Parse<ProductMatchType>(restriction.Match.Type);
            var parameters = new JsonObject
            {
                ["matchType"] = restriction.Match.Type,
                ["matchValue"] = restriction.Match.Value,
                ["action"] = restriction.Action,
                ["note"] = restriction.Note,
            }.ToJsonString();
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.ProductRestriction, parameters, Next(),
                categoryCode: type == ProductMatchType.Category ? restriction.Match.Value : null,
                productId: type == ProductMatchType.Product ? Guid.Parse(restriction.Match.Value) : null);
        }

        if (doc.DuplicatePurchase is { Enabled: true } duplicate)
        {
            var parameters = new JsonObject
            {
                ["windowHours"] = duplicate.WindowHours,
                ["scope"] = duplicate.Scope,
                ["action"] = duplicate.Action,
            }.ToJsonString();
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.DuplicatePurchase, parameters, Next());
        }

        foreach (var time in doc.TimeRestrictions)
        {
            var parameters = new JsonObject
            {
                ["daysOfWeek"] = new JsonArray(time.DaysOfWeek.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["startLocalTime"] = time.StartLocalTime,
                ["endLocalTime"] = time.EndLocalTime,
                ["action"] = time.Action,
            }.ToJsonString();
            yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.TimeRestriction, parameters, Next());
        }

        yield return SpendingPolicy.Create(versionId, userId, PolicyRuleType.UnbudgetedCategoryPolicy,
            Params(("action", doc.UnbudgetedCategoryAction)), Next());
    }

    /// <summary>
    /// Canonical content of a policy (no ids, no version metadata) and its SHA-256.
    /// Used for policy_versions.snapshot_json/hash and audit before/after hashes (BR-10).
    /// </summary>
    public static (string Json, string Hash) ContentSnapshot(PolicyDocument doc)
    {
        var normalized = doc with
        {
            PolicyVersionId = null,
            VersionNumber = null,
            TimeZone = null,
            MerchantRules = doc.MerchantRules with
            {
                ApprovedMerchantIds = doc.MerchantRules.ApprovedMerchantIds.Distinct().Order().ToList(),
                BlockedMerchantIds = doc.MerchantRules.BlockedMerchantIds.Distinct().Order().ToList(),
            },
            ProductRestrictions = doc.ProductRestrictions.Select(r => r with { Id = null }).ToList(),
            TimeRestrictions = doc.TimeRestrictions.Select(t => t with { Id = null }).ToList(),
            DuplicatePurchase = doc.DuplicatePurchase is { Enabled: true } ? doc.DuplicatePurchase : null,
        };
        var json = CanonicalJson.Serialize(JsonSerializer.SerializeToNode(normalized, JsonOptions));
        return (json, CanonicalJson.Sha256Hex(json));
    }

    private static string Params(params (string Key, string Value)[] values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj.ToJsonString();
    }

    private static decimal Decimal(JsonObject p, string key) =>
        decimal.Parse(p[key]!.GetValue<string>(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static TEnum Enum<TEnum>(JsonObject p, string key)
        where TEnum : struct, Enum => EnumText.Parse<TEnum>(p[key]!.GetValue<string>());
}
