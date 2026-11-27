# Load tests (k6)

NFR-01/NFR-02 targets: p95 under 500 ms for normal API calls, 1,000 concurrent learners, chat delivery under 1 s.

```bash
# 1. Seed test learners on a staging API (never production): one account per virtual user.
k6 run -e BASE_URL=https://staging-api.example.com -e USERS=200 tests/load/seed-users.js
# 2. Browse-and-learn mix (Home, roadmap, lecture, daily quiz, inbox)
k6 run -e BASE_URL=https://staging-api.example.com -e USERS=200 tests/load/api-smoke.js
# 3. Chat over SignalR WebSockets (message round trip)
k6 run -e BASE_URL=https://staging-api.example.com -e USERS=200 tests/load/chat.js
```

Raise `RateLimiting__GlobalPerMinute` and `RateLimiting__AuthPerMinute` on the staging API first: the default limits are
per user / per IP and every k6 VU runs from the same machine.
The scripts fail the run (non-zero exit) when the thresholds are missed, so they can gate a release in CI.
