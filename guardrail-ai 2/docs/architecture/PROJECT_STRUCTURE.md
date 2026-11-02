# Project Structure

```
guardrail-ai/
├── README.md
├── .gitignore
├── .editorconfig
├── render.yaml                    ← Render Blueprint (ADR-020)
├── apimatic/                      ← APIMatic build config for SDK + API reference (ADR-018)
├── docs/                          ← this documentation
├── backend/
│   ├── Guardrail.sln
│   ├── Directory.Build.props      ← nullable, warnings-as-errors, LangVersion latest
│   ├── Directory.Packages.props   ← central package versions
│   ├── Dockerfile
│   ├── src/
│   │   ├── Guardrail.Domain/
│   │   │   ├── Common/            ← Entity, Money, Currency, IAuditable
│   │   │   ├── Users/
│   │   │   ├── Spending/          ← SpendingProfile, PolicyVersion, SpendingPolicy, rule parameter records
│   │   │   ├── Policy/            ← PolicyEngine, PolicySnapshot, CartSnapshot, SpendingSnapshot, PolicyDecision, ReasonCodes
│   │   │   ├── Catalogue/         ← Merchant, Product, Category
│   │   │   ├── Shopping/          ← ShoppingSession, ShoppingCart, CartItem, Purchase
│   │   │   ├── Authorization/     ← ApprovalRequest
│   │   │   ├── Payments/          ← Payment, PayPalWebhookEvent, PaymentStatus
│   │   │   ├── Ledger/            ← SpendingLedgerEntry, LedgerBucket, LedgerTransition
│   │   │   ├── Agent/             ← AgentConversation, AgentMessage, AgentExecution
│   │   │   └── Audit/             ← AuditLog
│   │   ├── Guardrail.Application/
│   │   │   ├── Abstractions/      ← ICurrentUser, IClock (TimeProvider), IAppDbContext, IPayPalClient, ILlmClient, ICatalogueProvider, ISnapshotHasher
│   │   │   ├── Auth/
│   │   │   ├── Policies/          ← GetCurrentPolicy, UpdatePolicy, ListPolicyVersions
│   │   │   ├── Merchants/
│   │   │   ├── Catalogue/
│   │   │   ├── Carts/
│   │   │   ├── Evaluation/        ← EvaluateCartUseCase
│   │   │   ├── Purchases/         ← CreatePurchase, Checkout, CompletePayment (verify→authorize→final check→capture/void), FinalGuardrailCheck, Cancel
│   │   │   ├── Approvals/         ← Approve (password step-up), Reject, Expire
│   │   │   ├── Payments/          ← ProcessWebhookEvent, Reconcile
│   │   │   ├── Ledger/
│   │   │   ├── Agent/             ← AgentOrchestrator, ToolRegistry, Tools/*, SystemPrompt
│   │   │   └── Dashboard/
│   │   ├── Guardrail.Infrastructure/
│   │   │   ├── Persistence/       ← AppDbContext, Configurations/*, Migrations/, Seed/
│   │   │   ├── PayPal/            ← PayPalOptions, PayPalTokenProvider, PayPalOrdersClient (create/get/authorize/capture), PayPalAuthorizationsClient (capture/void/get), PayPalWebhookVerifier
│   │   │   ├── Llm/               ← OpenAiCompatibleLlmClient, RateLimitedLlmRouter (Gemini primary → Groq fallback), LlmOptions
│   │   │   ├── Catalogue/         ← SeedCatalogueProvider, Channel3MarketReferenceProvider (P2, ADR-019)
│   │   │   ├── Security/          ← JwtTokenService, PasswordHasher (ASP.NET Identity hasher), RefreshTokenStore
│   │   │   ├── Jobs/              ← ApprovalExpirySweeper, CheckoutExpirySweeper, WebhookProcessor, PaymentReconciler
│   │   │   └── Hashing/           ← CanonicalJson, Sha256SnapshotHasher
│   │   └── Guardrail.Api/
│   │       ├── Program.cs
│   │       ├── Endpoints/         ← one file per module (minimal APIs grouped by MapGroup)
│   │       ├── Middleware/        ← CorrelationId, ExceptionToProblemDetails
│   │       ├── RateLimiting/
│   │       └── appsettings*.json  ← non-secret defaults only
│   └── tests/
│       ├── Guardrail.Domain.Tests/        ← policy engine, ledger math, hashing
│       ├── Guardrail.Application.Tests/   ← use cases with in-memory fakes
│       ├── Guardrail.Integration.Tests/   ← Testcontainers PostgreSQL + WebApplicationFactory + PayPal/LLM fakes
│       └── Guardrail.Architecture.Tests/  ← NetArchTest dependency rules
└── frontend/
    ├── package.json
    ├── vite.config.ts             ← vite-plugin-pwa
    ├── vercel.json                ← SPA fallback + /api rewrite
    ├── index.html
    ├── public/                    ← icons, manifest assets
    └── src/
        ├── main.tsx
        ├── app/                   ← router, providers, layout shell
        ├── lib/                   ← auth, money formatting, query keys, api facade
        ├── lib/sdk/               ← APIMatic-generated TypeScript SDK (generated, never hand-edited)
        ├── components/ui/         ← shadcn/ui generated components
        ├── components/            ← shared composites (MoneyText, DecisionBadge, BudgetRing, DataGrid (AG Grid), StepUpDialog…)
        ├── features/
        │   ├── auth/
        │   ├── overview/
        │   ├── shopping/          ← chat, cards, cart drawer
        │   ├── rules/
        │   ├── merchants/
        │   ├── approvals/
        │   ├── transactions/
        │   ├── checkout/          ← return/cancel pages
        │   └── settings/
        ├── schemas/               ← Zod schemas mirroring API_CONTRACT.md
        └── styles/
```

## Conventions

- One use case = one class with `Handle` method (`CreatePurchaseHandler`), plus `…Command`/`…Query` record and `…Validator`. No MediatR required (plain DI); allowed if preferred.
- Endpoints are thin: bind → call handler → map result.
- Each EF entity has an `IEntityTypeConfiguration<T>` in `Persistence/Configurations`.
- Frontend features own their routes, API hooks (`useXxxQuery`) and Zod schemas.
