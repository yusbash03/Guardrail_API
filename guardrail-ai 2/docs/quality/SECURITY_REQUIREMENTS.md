# Security Requirements

IDs `SEC-xx` are referenced by tests and review checklists.

## Authentication & sessions

- **SEC-01** Passwords hashed with ASP.NET Core Identity `PasswordHasher<T>` (PBKDF2, current default iterations). Min length 10; reject top-10k common passwords.
- **SEC-02** Access JWT: HS256, 15 min, claims `sub`, `email`, `role`, `jti`, `iat`, `exp`, `iss`, `aud`. Validate issuer, audience, lifetime (clock skew 30 s), signing key.
- **SEC-03** Refresh token: 256-bit random, stored as SHA-256 hash, rotated on every use, family revoked on reuse, httpOnly/Secure/SameSite=Strict cookie scoped to `/api/v1/auth`.
- **SEC-04** `/auth/refresh` requires `X-Guardrail-Client: web` header and same-origin `Origin` (CSRF defence for cookie-authenticated endpoint).
- **SEC-05** Login lockout: 5 failures → 5-minute lockout per account + IP rate limit. Generic error messages ("Invalid email or password").
- **SEC-06** Approving a purchase requires password re-entry (step-up, ADR-015); verified server-side before any state change; failures audited and rate-limited (5 / 15 min); the password field is redacted from all logs and never persisted.

## Authorization & isolation

- **SEC-10** Every endpoint except register/login/refresh/health/webhook requires authentication (fallback policy = authenticated).
- **SEC-11** All user-owned reads/writes filtered by `user_id` from the token; cross-user ids return 404.
- **SEC-12** Admin endpoints require `role=ADMIN` and are disabled unless `Demo:ResetEnabled`.
- **SEC-13** The frontend hides actions by state, but **every** state rule is re-checked server-side.

## AI-specific

- **SEC-20** Agent tool registry is a closed allowlist (AI_GUARDRAILS.md); architecture tests enforce no payment/approval/policy-write dependencies.
- **SEC-21** No secrets, tokens, raw PayPal payloads or other users' data in LLM context.
- **SEC-22** Untrusted catalogue text sanitized and length-capped.
- **SEC-23** LLM API key only in backend env; never sent to the browser.

## Payments

- **SEC-30** PayPal client id/secret and webhook id only in backend env; never logged.
- **SEC-31** Webhooks verified via PayPal verify-webhook-signature before processing; unverified events never change state.
- **SEC-32** Webhook idempotency by `webhook_event.id` unique constraint.
- **SEC-33** Order amount/currency/custom_id verified on GET before authorization, on the authorization, and on every capture/webhook result.
- **SEC-36** Orders use intent `AUTHORIZE`; capture only after the final guardrail check passes; failing check → void. Open authorizations are voided within 30 min.
- **SEC-37** The LLM has no PayPal toolkit/MCP connection (ADR-016).
- **SEC-34** PayPal POSTs carry `PayPal-Request-Id`; retries reuse it.
- **SEC-35** Live PayPal host rejected at startup.

## Approvals & ledger

- **SEC-40** Approval bound to cart snapshot hash, amount, currency, merchant, policy version, expiry; single-use atomic consume.
- **SEC-41** Budget reservation serialized per user (`FOR UPDATE` on profile) with re-evaluation inside the lock.
- **SEC-42** Ledger append-only; unique transition constraint.

## Input validation & output encoding

- **SEC-50** FluentValidation on every command; request body max 64 KB (1 MB global cap); strings length-bounded.
- **SEC-51** EF Core parameterized queries only; no raw SQL string concatenation (raw SQL via `FromSql` interpolated parameters only).
- **SEC-52** React escapes by default; assistant text rendered as plain text/markdown-lite without raw HTML (`dangerouslySetInnerHTML` banned by lint rule).

## Transport & headers

- **SEC-60** HTTPS only (Render/Vercel TLS); HSTS on API responses.
- **SEC-61** CSP on the SPA: `default-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://www.sandbox.paypal.com` **[VERIFY]** PayPal redirect host for form-action if needed (we use `location.assign`, so likely unnecessary).
- **SEC-62** CORS: production same-origin; dev allows `http://localhost:5173` only.

## Rate limiting & abuse

- **SEC-70** ASP.NET Core rate limiter policies from API_CONTRACT.md; 429 with `Retry-After`.
- **SEC-71** One concurrent agent turn per user.

## Logging, audit, privacy

- **SEC-80** Audit log entries for all actions in BR-90; audit table append-only.
- **SEC-81** Logs exclude passwords, tokens, secrets, full card/PayPal payloads, LLM prompts (Info+). Emails masked (`a***@example.com`).
- **SEC-82** Correlation id on every request and propagated to PayPal/LLM call logs.

## Supply chain & secrets

- **SEC-90** Central package management; `dotnet list package --vulnerable` and `npm audit --omit=dev` in CI (fail on high).
- **SEC-91** Secret scanning (gitleaks) in CI; `.gitignore` covers `.env*`, `appsettings.*.local.json`, user-secrets.
- **SEC-92** Dependabot/Renovate for weekly updates (post-hackathon).

## Security test checklist (pre-demo)

- [ ] Cross-user access tests pass for all `{id}` endpoints.
- [ ] Forged webhook (bad signature) → 400, stored REJECTED, no state change.
- [ ] Replayed valid webhook → single ledger effect.
- [ ] Approval reuse / expiry / hash mismatch / wrong-password tests pass.
- [ ] Final-check failure voids the authorization (Sandbox run).
- [ ] Concurrency test: two parallel purchases cannot exceed budget.
- [ ] Red-team agent suite passes.
- [ ] No `/api` responses in Cache Storage.
- [ ] gitleaks clean.
