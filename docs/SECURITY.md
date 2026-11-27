# Security review (Phase 10)

Checked against the OWASP Top 10 (2021) and OWASP ASVS L1 for the API, the admin panel and the app.
"Done" means implemented and covered by a test or a config check; "Action" means something to do at deployment.

| Area | Status | Where |
|---|---|---|
| **A01 Broken access control** | Done. Every API controller has `[Authorize(Policy = …)]`; the admin panel has a fallback policy (admin role required unless `[AllowAnonymous]`). Learner data is always filtered by the caller's user id in the service, never by an id from the request alone. Analytics reports and exports are Super Admin only and audited. | `Program.cs`, `AdminControllerBase`, `AnalyticsReportsController` |
| **A02 Cryptographic failures** | Done. Passwords: ASP.NET Identity PBKDF2. Refresh tokens and OTPs stored as SHA-256 hashes. JWT signing key from configuration only. HSTS outside development; admin and antiforgery cookies `Secure=Always` outside development. | `JwtTokenService`, `OtpService`, admin `Program.cs` |
| **A03 Injection** | Done. EF Core parameterised queries only (no raw SQL in the app). Rich text sanitised on save (`HtmlSanitizerService`). Chat text checked by the content filter. | |
| **A04 Insecure design** | Done. Server-side timing for exams and live matches; correct answers never sent before a question closes; points ledger instead of editable totals; three-strike moderation with identity block list. | Phases 4, 7, 8, 9 |
| **A05 Security misconfiguration** | Done. Security headers on every response (CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, COOP), `Server` header removed, Swagger off in production, detailed SignalR errors only in development, problem-details errors without stack traces. | `SecurityHeadersMiddleware`, `AdminSecurityHeadersMiddleware` |
| **A06 Vulnerable components** | Done in CI: `dotnet list package --vulnerable --include-transitive` runs on every build. **Action:** review its output before each release. | `.github/workflows/ci.yml` |
| **A07 Identification and authentication** | Done. Lockout after 5 failures for 10 minutes; email OTP with attempt limit; refresh-token rotation with reuse detection (revokes the family); security stamp re-checked on every request; sign-out everywhere; rate limit 10/min/IP on auth endpoints. | Phase 1 tests |
| **A08 Software and data integrity** | Done. Build and tests in CI; containers built from the repo; no remote code at runtime. Admin uses jsDelivr for Bootstrap and Quill. **Action:** add `integrity="sha384-…"` attributes to the two CDN tags when you pin versions, or self-host them. | `_Layout.cshtml` |
| **A09 Logging and monitoring** | Done. Serilog with correlation ids to files and Seq; audit log of admin actions (including report exports); moderation events; client crash reports; `/health/live` and `/health/ready`. | `docs/OPERATIONS.md` |
| **A10 SSRF** | Done. The server only calls fixed provider hosts from configuration (AI, Translator, Speech, FCM). Uploaded-file reads go through `IFileStorage`, which only reads its own folder. | |

## Specific checks

- **Rate limits:** global 120 requests/min per user (or IP), auth 10/min/IP, certificate verification 30/min/IP, crash reports 20/min/IP, chat 30 messages/min (BR-31), slow mode per group. All configurable under `RateLimiting`.
- **Request size:** Kestrel `MaxRequestBodySize` 25 MB; photo uploads 10 MB and audio limits are checked again in the services (NFR-17); SignalR messages 32 KB; crash reports 64 KB.
- **Secrets:** API keys (DeepSeek, Azure, FCM, SMTP, JWT) only in user-secrets, environment variables or Key Vault — never in the database or the repo. `deploy/.env` and `deploy/secrets/` are git-ignored.
- **CSP in the admin panel** still allows `'unsafe-inline'` for scripts because a few views use inline `onclick`/`onchange` handlers and small `<script>` blocks. Moving those into `wwwroot/js` and switching to nonces is the next hardening step; until then the admin panel must only be reachable by staff (VPN, IP allow-list or Azure AD Application Proxy).
- **Response compression** is on for HTTPS. Responses that mix secrets with attacker-controlled text are not compressed differently, but if you are worried about BREACH-style attacks on the token endpoints, exclude `/api/v1/auth` from compression in `Program.cs`.
- **BR-32:** under 13 refused at sign-up (server and app); under 18 can only message or challenge friends and can't post in public groups. The date of birth can be set once from the app.
- **Personal data (NFR-08):** account deletion anonymises at once; the daily maintenance job deletes practice recordings after 30 days, read notifications after 90, and deleted-account leftovers after 30 days. The export (`GET /api/v1/me/export`) covers profile, devices, courses, progress and points.

## Penetration test checklist (before go-live)

1. Run OWASP ZAP baseline against staging API and admin (`zap-baseline.py -t https://staging-api…`); fix anything Medium or above.
2. Try IDOR on every `{id}` route with a second learner's token (the integration tests cover the main ones).
3. Try exam answer tampering: answer after expiry, answer twice, read answers before submit (covered in Phase 4 and 9 tests).
4. Replay an old refresh token: the session family must be revoked.
5. Chat: links in disguise (`g o o g l e dot com`), phone numbers in words, mass messages (spam rule).
6. Check headers with securityheaders.com and TLS with SSL Labs (target A).
