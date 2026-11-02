# PWA Requirements

## 1. Goals

Installable, fast-loading app shell on mobile and desktop. **No financial data available offline.**

## 2. Manifest

```json
{
  "name": "Guardrail AI",
  "short_name": "Guardrail",
  "description": "Your money. Your rules. Your AI.",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#0B0F1A",
  "theme_color": "#4F46E5",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "/icons/maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

## 3. Service worker (vite-plugin-pwa, Workbox `generateSW`)

| Request | Strategy |
|---|---|
| App shell (`index.html`, JS, CSS, fonts, icons) | Precache (revisioned) |
| Product images `/images/**` | `StaleWhileRevalidate`, max 100 entries, 7 days (non-sensitive catalogue art) |
| `/api/**` | **`NetworkOnly`** — never cached |
| PayPal domains | not intercepted |
| Navigation | `NavigationRoute` to `index.html`, **denylist** `/api/` |

`registerType: 'prompt'` — show "Update available" toast; no silent reloads during checkout.

## 4. Data-at-rest rules

- No `persistQueryClient`; no IndexedDB/localStorage of API responses.
- `localStorage`: theme preference only.
- `sessionStorage`: only opaque idempotency keys per purchase during checkout return.
- Access token in memory; refresh token in httpOnly cookie (inaccessible to JS and SW).
- On logout: `queryClient.clear()`, clear sessionStorage, `caches.delete` for any runtime cache except precache.

## 5. Offline UX

- Offline banner: "You're offline. Guardrail needs a connection to show your money."
- Pages render the shell + skeletons; all money widgets show "Unavailable offline".
- Money-moving buttons disabled offline.

## 6. Acceptance criteria

- [ ] Lighthouse: installable, has SW, manifest valid.
- [ ] DevTools → Application → Cache Storage contains no `/api/` responses after a full demo run.
- [ ] Airplane mode after login shows no balances, budgets, purchases or messages.
- [ ] Update prompt appears after deploying a new version.
