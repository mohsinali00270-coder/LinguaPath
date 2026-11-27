# Operations runbook

## Topology

```
App (Android/iOS) ──HTTPS──► reverse proxy (TLS) ──► API  (:8080, SignalR /hubs/chat, /hubs/competition, workers)
Staff browser   ──HTTPS──► reverse proxy (TLS) ──► Admin (:8081, AI job runner)
                                    API + Admin ──► SQL Server · shared uploads volume · Redis (optional) · Seq
```

`deploy/docker-compose.yml` runs all of it on one server. For Azure: App Service (API, Admin), Azure SQL, Azure Cache for Redis,
Blob storage for uploads (swap `LocalFileStorage`), Application Insights or Seq.

## Background work

| Worker | Host | Interval | Safe on several servers? |
|---|---|---|---|
| Attempt expiry (exams, daily quizzes) | API | 1 min | Yes (idempotent) |
| Moderation AI scan queue | API | on demand | Per server queue |
| Competition ticker (live matches) | API | 1 s | **No** — see scale-out |
| Reminders (FR-L110) | API | 15 min (`Reminders:IntervalMinutes`) | Yes — dedup keys with a unique index |
| Maintenance / retention (NFR-08) | API | daily after `Retention:RunAtUtcHour` | Yes (idempotent) |
| AI job runner | Admin | 10 s | Run one admin instance, or set `Ai:Runner:Enabled=false` on extras |

## Scale-out

The API keeps some state in memory: SignalR connections, the presence tracker, the per-competition locks and the competition ticker.

- **One API instance** (recommended to start, handles the NFR-02 target of 1,000 concurrent learners on a 4-core box).
- **Several instances:** set `Redis:ConnectionString` so SignalR messages reach clients on every server, and use sticky sessions
  (ARR affinity) so a live match's players and its ticker stay on one server. Presence then shows per server; moving
  `PresenceTracker` and the competition locks to Redis is the follow-up if you need true active-active.

## Monitoring

- **Health:** `/health/live` (process up — use for liveness/restarts), `/health/ready` (database reachable — use for load balancer).
  Admin has `/health/live`.
- **Logs:** Serilog → console, `logs/*.log` (30 days) and Seq when `Serilog__WriteTo__0__Name=Seq` is set. Every request has a
  `CorrelationId` (also returned in the `X-Correlation-ID` header) — ask users for it when they report a problem.
- **Client crashes:** search Seq for `Client error` (properties `ExceptionType`, `AppVersion`, `Platform`, `Page`).
- **Alerts to set up in Seq or App Insights:** `/health/ready` failing for 2 minutes; error rate > 1% for 5 minutes;
  `Reminder run failed`; `Maintenance run failed`; AI budget warnings; SQL log file > 80%.
- `database/ops/health.sql` shows database state, last backups, biggest tables and queue sizes.

## Backups (NFR-07: RPO 1 hour, RTO 4 hours)

- `database/ops/backup.sql`: nightly `FULL`, every 6 hours `DIFF`, hourly `LOG`. Schedule with SQL Agent, cron
  (`docker compose exec sql sqlcmd … -v Kind=LOG -i …`) or use Azure SQL automatic backups (point-in-time restore built in).
- Copy backup files **and the uploads volume** off the server every night; keep 35 days.
- **Restore drill (monthly):** follow `database/ops/restore.sql` into `LinguaPath_Restore`, point a staging API at it, sign in,
  open a certificate and check its verification page.

## Deploying a new version

1. CI green on `main`. Tag the release (`v1.2.0`) and update `ApplicationDisplayVersion` / `ApplicationVersion` in the app.
2. Back up the database (`Kind=FULL`).
3. Add the EF migration for any schema change (see README) and review the generated SQL:
   `dotnet ef migrations script --idempotent -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o deploy/migrate.sql`.
4. `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build api admin` — the API applies migrations on start.
5. Watch `/health/ready` and Seq for 10 minutes. Roll back by redeploying the previous image tag; restore the backup only if a
   migration damaged data.
6. To force an app update, raise `App.MinVersion.Android` / `App.MinVersion.iOS` in admin Settings (the app shows "Update required").

## Incident quick reference

| Symptom | Look at |
|---|---|
| Learners can't sign in | `/health/ready`, Seq errors on `/api/v1/auth/*`, SQL lockouts (`AccessFailedCount`) |
| No reminders | `Reminders:Enabled`, `Reminder run failed` in Seq, `Push:*` config, `UserDevices.PushToken` present |
| Chat messages late | API CPU, Redis connection if configured, `ChatHub` warnings |
| AI generation stuck | Admin → AI generation (job status), AI budget in Settings, provider key |
| Disk filling | `logs/`, uploads volume, SQL log (`log_reuse_wait_desc` in `health.sql`) |
