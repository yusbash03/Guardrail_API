# Coding Standards

## General

- English identifiers; no abbreviations except well-known (`Id`, `Url`, `Dto`).
- Small, intention-revealing functions; no "Manager"/"Helper" grab-bag classes.
- Every money value travels with its currency (`Money` value object backend; `{ amount: string, currency: string }` on the wire).
- No business rule in controllers, React components, or prompts.

## C# / .NET

- .NET 10, C# latest, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- `decimal` for money, never `double`/`float`. Round with `MidpointRounding.ToEven` only at defined points (line total, cart total); USD = 2 dp.
- Records for DTOs, commands, snapshots. Entities expose behaviour methods; setters private.
- Async all the way; every async method takes a `CancellationToken`.
- Inject `TimeProvider`; never call `DateTime.Now`/`UtcNow` directly. Store `timestamptz` in UTC.
- Return `Result<T>` (success / `DomainError(code, message, details)`) from use cases; throw only for programmer errors and infrastructure failures.
- EF Core: `AsNoTracking()` for queries; explicit `Include`s; no lazy loading; every user-owned query starts from a `UserScoped()` extension that applies `Where(x => x.UserId == currentUser.Id)`.
- Logging: structured templates (`_log.LogInformation("Captured {PurchaseId}", id)`); never log tokens, passwords, full PayPal responses, or prompt contents containing PII at Information level.
- Naming: `PascalCase` types/methods, `_camelCase` private fields, `Async` suffix on async methods.
- Tests: `MethodOrScenario_Condition_ExpectedResult`; Arrange/Act/Assert; FluentAssertions-style assertions (or Shouldly).

## Database

- `snake_case` table and column names (EFCore.NamingConventions).
- UUID v7 primary keys generated in application (`Guid.CreateVersion7()`), stored as `uuid`.
- Every table: `created_at timestamptz not null`, mutable tables also `updated_at`.
- Money: `numeric(14,2)`; currency `char(3)`.
- Enums stored as `text` with `CHECK` constraints (readable in SQL, safe across reorders).
- Migrations are reviewed; no destructive migration without a data plan.

## TypeScript / React

- `strict: true`, `noUncheckedIndexedAccess: true`.
- No `any`; parse every API response with Zod at the boundary.
- Money on the wire is a decimal **string**; format with `Intl.NumberFormat`. Never do arithmetic on floats for display totals — the server provides totals.
- Server state via TanStack Query only; local UI state via `useState`/`useReducer`. No global store unless needed.
- Forms: React Hook Form + `zodResolver`.
- Components: function components, named exports, colocated `*.test.tsx`.
- Tailwind classes ordered by `prettier-plugin-tailwindcss`. Design tokens via CSS variables (DESIGN_SYSTEM.md).
- Accessibility: every interactive element is keyboard reachable and labelled.

## Git

- Trunk-based; short-lived branches `feat/…`, `fix/…`, `docs/…`.
- Conventional Commits (`feat(policy): add duplicate purchase rule`).
- CI must pass: build, tests, lint, format check.

## Review checklist (financial code)

- [ ] Is user ownership checked server-side?
- [ ] Is the operation idempotent or guarded by a unique constraint?
- [ ] Does it run inside the right transaction / lock?
- [ ] Are amounts and currency taken from the database, not the request?
- [ ] Is there an audit log entry?
- [ ] Is there a test for the failure path?
