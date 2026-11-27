# LinguaPath — Phase 10: Hardening and release

Language-learning platform: **.NET MAUI** app + **ASP.NET Core Web API** (.NET 10) + **ASP.NET Core MVC admin panel** + **SQL Server**.
This zip contains all 10 phases from the SRS: the complete solution, ready for the release work in `docs/`.

## What Phase 10 adds

| Area | Done in Phase 10 | SRS |
|---|---|---|
| Reminders | Worker in the API every 15 min: study time (per course, else profile, else 19:00), daily quiz ready (from 17:00), monthly exam in 3 days, streak at risk (from 20:00), new post in my language; certificates too. Learner's time zone, study days and switches respected; no reminders 22:00–08:00; each reminder sent once (dedup key + unique index, safe on several servers). | FR-L110 |
| Inbox | `GET/PUT/DELETE /api/v1/notifications…`; 🔔 with unread badge on Home; inbox page (pull to refresh, swipe to delete, tap opens the screen). Push taps use the same routing. | FR-L112 |
| Push in the app | Firebase Cloud Messaging wired behind `UseFirebase` (on when `google-services.json` / `GoogleService-Info.plist` are present); Android 13 notification permission, iOS APNs entitlement. | FR-L110, FR-C09 |
| Analytics reports | Admin → Analytics reports: completion by course, learner progress, quiz/exam performance, drop-off per lecture, translation and AI usage, certificates; date/language/course filters; Excel (ClosedXML) and PDF (QuestPDF) export; exports audited. | FR-A100 |
| Urdu UI | All 372 app strings in `AppResources.resx` / `AppResources.ur.resx`; `{l:Tr Key}` in XAML; right-to-left layout for Urdu; language from Edit profile (or the phone on first run), applied by rebuilding the shell. | NFR-10 |
| Accessibility | Screen-reader descriptions on icon buttons and profile photos, decorative images hidden, page titles and section headers are headings. | NFR-12 |
| Offline sync | Lecture completions and flashcard reviews made offline are queued and sent when the phone is back online. | NFR-09 |
| Age rules | Optional date of birth on sign-up and profile (set once); under 13 refused; under 18 can only message/challenge friends and can't post in public groups. | BR-32 |
| Retention | Daily job: practice recordings deleted after 30 days (scores kept), old inbox entries, expired tokens and OTPs, deleted-account leftovers after 30 days. | NFR-08 |
| Security | Security headers and CSP (API and admin), HTTPS-only cookies in production, request size limits, crash-report rate limit, `docs/SECURITY.md` OWASP review. | NFR-05, NFR-06 |
| Monitoring | `/health/live`, `/health/ready` (database), Seq sink, crash reports from the app to `/api/v1/client-errors`, `database/ops/health.sql`. | NFR-14 |
| Performance | Response compression (Brotli/Gzip); k6 load tests for the learner mix and chat round trip in `tests/load`. | NFR-01, NFR-02 |
| Scale-out | Optional Redis backplane for SignalR (`Redis:ConnectionString`); limits of in-memory state documented. | |
| Deployment | GitHub Actions CI (server build + tests + vulnerable-package check, Android and iOS builds, Docker images), Dockerfiles, `deploy/docker-compose.yml` (SQL Server, Redis, Seq, API, admin), backup/restore scripts, runbook. | |
| Store | Release signing steps, privacy policy, terms and account-deletion pages served by the API (`/privacy`, `/terms`, `/delete-account`), data-safety table. | |
| Acceptance | `docs/ACCEPTANCE.md` maps every SRS acceptance criterion to an automated test or a manual check. | 10.3, 11.9 |

### Docs

- `docs/OPERATIONS.md` — topology, workers, scale-out, monitoring, backups, deploying, incidents
- `docs/SECURITY.md` — OWASP review and pen-test checklist
- `docs/RELEASE.md` — Firebase, Google Play and App Store steps
- `docs/ACCEPTANCE.md` — acceptance run against the SRS

### Phase 10 configuration

| Section | What it controls |
|---|---|
| `Reminders` | `Enabled`, `IntervalMinutes`, `StudyWindowMinutes`, `DefaultStudyTime`, `QuizReadyFrom`, `StreakAlertFrom`, `ExamAlertFrom`, `ExamDaysBefore`, `QuietFrom`/`QuietUntil`, `NewPostMaxAgeDays` |
| `Retention` | `PracticeRecordingDays` (30), `ReadNotificationDays` (90), `UnreadNotificationDays` (180), `DeletedAccountPurgeDays` (30), `ExpiredTokenDays`, `RunAtUtcHour` |
| `Redis:ConnectionString` | Empty = single server. Set it to share SignalR across API servers. |
| `Kestrel:Limits` | Request body 25 MB, headers 32 KB |
| `Serilog:WriteTo` | Add Seq: `Serilog__WriteTo__0__Name=Seq`, `Serilog__WriteTo__0__Args__serverUrl=http://seq:5341` |

## What Phase 9 adds

| Area | Done in Phase 9 | SRS |
| --- | --- | --- |
| Database | Competitions, CompetitionPlayers, CompetitionQuestions, CompetitionAnswers, CompetitionRatings, Tournaments, TournamentEntries, Leagues, LeagueMembers; `Messages.CompetitionId` for cards | 11.7 |
| Settings per match | Language, level, optional module, 5 / 10 / 15 questions, 10–20 s each; questions only from the published bank (multiple choice, true/false, fill-blank, listen-and-choose), widening to ± 1 level then the whole language when short; the same questions in the same order for everyone | FR-Q01, FR-Q02 |
| 1 v 1 challenge | Challenge a friend or chat contact ("who can challenge me" and blocks apply); 24 h to accept; **live** (both together) or **own time** (each plays the same questions when they like; the other's score stays hidden until both are done) | 11.4 |
| Quick match | Same language at ± 1 CEFR level; nobody free within 30 s → offer a **practice bot** (marked 🤖, unranked, practice points only) | 11.4 |
| Group battle | A group admin opens a lobby for 3–50 members; a card is posted in the group; members join, the admin can start early; live leaderboard after every question; top 3 get 30 / 20 / 10 (rule *GroupBattleBonus*), everyone else 5 | FR-Q10, FR-C28 |
| Tournaments | Admin-scheduled: entry window, 1–5 rounds (top half go through, the last round is the final), question pool (language, level, module), prizes for the top 3 (not capped by the daily limit); disqualify → all their tournament points revoked and the prizes move up | FR-Q11 |
| Live engine | The server keeps time and checks answers (FR-Q05): a question closes when everyone has answered or its time is up (+1.5 s network allowance), then the result (correct answer, everyone's score) and the next question after a 3 s pause; late, early and duplicate answers are refused; correct answers are never sent before a question closes. A background clock (every 0.5 s) moves matches on; any request also moves its match on, so nothing hangs if the clock is busy | FR-Q03 – FR-Q05 |
| Scoring | 100 for a correct answer + up to 50 for speed (linear in time left); ties broken by total answer time (unanswered = full time); exact ties draw; leaving or disconnecting stops your answers (unanswered = 0) and leaving early is always a loss | FR-Q04, FR-Q06 |
| Points and limits | Win 30 / draw 15 / loss 5 (*Points rules*); max 20 ranked matches a day and win points against the same opponent at most 3 times a day (*Settings* → Competition) | BR-29, FR-Q09 |
| Stats | History, played / won / win rate / best streak, Elo rating per language (start 1,000, K = 32, human 1 v 1 only) | FR-Q08 |
| Sharing | Result screen with per-question review, rematch, **share to chat** as a card (not a link); tapping a card opens the lobby or result | FR-Q07 |
| Weekly leagues | Groups of up to 30 in Bronze → Silver → Gold → Platinum → Diamond, ranked by points earned this week (Monday 00:00 UTC, admin adjustments don't count); top 5 promoted, bottom 5 relegated (not on ties, not in small leagues); joined on first visit each week; closed hourly by the clock | FR-S08 |
| Moderation | A chat suspension also stops competitions; a permanent block removes the learner from running matches and revokes their competition and tournament points | FR-M12, FR-M14 |
| Real time | SignalR hub `/hubs/competition`: `JoinMatch`, `SubmitAnswer`; events `QuestionStarted`, `OpponentAnswered`, `QuestionResult`, `MatchUpdated`, `MatchEnded`, `ChallengeReceived`. Every action also exists over REST, and the app polls when the socket is down | 11.8 |
| Admin panel | **Tournaments**: create/edit (UTC times), publish (checks the pool has enough questions), start now, cancel with a reason, rounds, entrants with places and prizes, disqualify with a reason (audited) | FR-Q11 |
| App | ⚔️ on Home and *Compete* in Profile: quick match, challenge a friend (also from a profile), active matches and challenges, history and stats; live match screen (countdown, timer, opponents' progress, answer feedback, leaderboard), own-time play, bot offer, group lobby; result with review, rematch and share; tournaments (join, play my round, standings); my league; *Start a group battle* in group info; competition cards in chat | FR-Q, FR-S08 |
| Tests | Scoring, speed bonus, ranking and ties, draw/leaver rules, Elo, levels, streaks, group bonus, bot answers, tournament advancement, prizes with disqualification, league weeks/zones/ranks, seeded picks (unit); live challenge with the same questions, faster-wins, points and rating; own-time challenge with hidden scores; late/duplicate answers; quick match pairing ± 1 level and leaving = loss; bot = practice only; 3 win points a day vs the same opponent; challenge privacy; group battle with card, join, start, live leaderboard and 30/20/10/5; a 2-round tournament with prizes and disqualification moving prizes up; share to chat; league points (integration) | NFR-15 |

**Not yet in Phase 9:** match-making by rating (quick match pairs by language and level, oldest search first); spectators; a bot in group battles.
Tournaments started from the **admin panel** begin in the database at once; players' apps pick the round up on their next refresh (push + the
tournament screen), and from then the API's clock drives it live.

## What Phase 8 adds

| Area | Done in Phase 8 | SRS |
| --- | --- | --- |
| Database | Friendships, UserBlocks, UserReports, Conversations, ConversationMembers, Messages, MessageRecipientStates, MessageReactions, AbusiveWords, BlockedLinkPatterns, ModerationEvents, ModerationStrikes | 11.7 |
| People | Public profile (name, photo, bio, languages and levels, points tier, badges, online/last seen if shown — never email or phone); search by display name or language; friend requests (sending one back accepts theirs); block (ends the friendship, hides you from them, stops messages and challenges); report a learner or a message | FR-S20 – FR-S23 |
| Privacy | Who can message me / challenge me (Everyone, Friends only, Nobody), show on leaderboards, show online, read receipts (off = you don't see others' either); blocked list | FR-S24 |
| Chat | One direct chat per pair; text messages up to `Chat.MaxMessageLength`; replies, 8 reactions, Sent / Delivered / Seen, typing, online presence; delete for me, or for everyone within `Chat.DeleteForEveryoneHours` (group admins any time) — deleted text never leaves the server; mute, archive, clear history; translate a message into my language; search my messages; retries are safe (client id) | FR-C01 – FR-C14, FR-C30 |
| Groups | Owner / admins / members; built-in icons; name and description moderated; announcement mode, slow mode, public groups with search and optional join approval; add/remove members, roles, hand over ownership; system lines ("Ali joined"); @mentions (notify even when muted); `Chat.GroupMaxMembers`, `Chat.MaxGroupsPerUser`; owner leaving → longest-serving admin (or member) takes over | FR-C20 – FR-C29 |
| Real time | SignalR hub `/hubs/chat` (JWT in `access_token`): `SendMessage`, `Typing`, `MarkSeen`; events `MessageReceived`, `MessageDeleted`, `ConversationChanged`, `Seen`, `Typing`, `PresenceChanged`, `ModerationWarning`. REST does the same, so the app works when the socket is down | FR-C01 – FR-C05 |
| Link/contact filter | URLs, domains (also "example dot com"), emails (also "name at gmail"), phone numbers (also written as words, but not years or counting practice), "add me on snapchat" / "my insta is …" and t.me-style links are never delivered. Admins add blocked regex patterns and **exceptions**. Every 5th attempt in a day = a strike | FR-M05 – FR-M09, FR-M25, BR-25 |
| Word filter | Per-language (or all-language) list with severities. Matching ignores case, accents, repeated letters, spacing ("s t u p i d"), dots and leetspeak ("5tup1d"), and finds phrases. **Mild** → masked (d***) and delivered; **Abusive** → refused + strike; **Severe** → account blocked. Also applied to display names, bios, group names and descriptions | FR-M01, FR-M10, FR-M11 |
| AI moderation | Every delivered message is scanned after delivery through the AI pipeline (DeepSeek; offline heuristic in dev/tests) with the editable `moderation` prompt. Score ≥ 70 → message removed for everyone + strike; threats/hate/sexual ≥ 90 → block; 50–69 → moderation queue. A background worker processes the queue and sweeps anything missed | FR-M02, FR-M03 |
| Spam | More than `Chat.MessagesPerMinute` → slowed down; the same text in 5+ chats within 10 minutes → refused + strike | FR-M04 |
| Strike ladder | Strikes last 90 days. 1st = warning the learner must accept before chatting again; 2nd = chat suspended 24 h (learning still works); 3rd = permanent block. Block = sign-in blocked, all sessions revoked at once, email + Google/Apple id + devices added to blocked identities (no new account), live chat connections closed, left out of leaderboards | FR-M12 – FR-M15, BR-26 – BR-28 |
| Push | `IPushSender`: FCM HTTP v1 (service account, tokens FCM rejects are cleared) or a log line when not configured. New messages for learners who are offline (muted chats only for @mentions); certificates | FR-C09 |
| Admin panel | **Moderation queue** (reports + borderline AI, or all events; filters), item page with 5 messages before/after (original text), reports and strike history; actions: dismiss, add strike, remove strike, suspend N days, block, remove message; **Unblock** (Super Admin only, reason, audited). **Abusive words** (CRUD, severity, language, Excel import/export, test box), **Link rules** (patterns, exceptions, test), **Moderation report** (strikes per day, top words, blocked accounts, false-positive rate, events by source). Learner page shows strikes, suspension, reports and recent events | FR-M20 – FR-M26 |
| App | 💬 Chats on Home and Profile: chat list (unread, online dot, mute/archive/clear/leave), chat screen (live, typing, ticks, replies, reactions, translate, delete, report, "load earlier"), People (search, requests, friends), public profile (add/accept/remove friend, message, block, report), new group, group info (settings for admins, members and roles, join requests), public groups, Privacy; strike warning / suspension / block dialogs; leaderboard "Friends" scope | FR-S, FR-C |
| Tests | Word filter normalisation and false positives, contact detection incl. disguised forms and things that must pass, admin patterns/exceptions, strike ladder, link-strike rule, spam, AI thresholds, slow mode, mentions (unit); friends/blocks/profiles, privacy, direct chat with seen state, delete for me/everyone, reactions, masking, link strikes and the warning gate, the full strike ladder to a block (token rejected at once), severe block, AI scan removal, reports, groups with roles, announcement mode, mentions, public join and owner hand-over (integration) | NFR-15 |

**Not yet in Phase 8:** images, voice notes and files in chat (text only by design for now); competition cards in chat (Phase 9);
the **app side of push** — the API sends FCM, but the MAUI app returns no token until Firebase is added (see *Push notifications* below);
BR-32 (under-18 rules) needs a date of birth, which the profile doesn't collect yet.

### Push notifications

1. Create a Firebase project, add the Android app (package id) and iOS app, download `google-services.json` / `GoogleService-Info.plist`.
2. API: Firebase → Project settings → Service accounts → *Generate new private key*. Keep the JSON out of the repo:
   ```powershell
   cd src/LinguaPath.Api
   dotnet user-secrets set "Push:FcmProjectId" "your-project-id"
   dotnet user-secrets set "Push:FcmServiceAccount" "C:\secrets\linguapath-firebase.json"   # a path, or the JSON itself
   ```
   (In production use Key Vault / environment variables `Push__FcmProjectId`, `Push__FcmServiceAccount`.)
3. App: add `Plugin.Firebase.CloudMessaging`, the two Firebase files, and return the token from `MauiPushTokenProvider.GetTokenAsync()`.
   `SocialBootstrap` sends it to `/api/v1/devices` after sign-in.

Without these, pushes are written to the API log ("Push (not configured) …").

### Scaling the chat

Presence and live connections are kept in memory per API instance. With more than one API server, add a SignalR backplane
(Azure SignalR Service or Redis: `builder.Services.AddSignalR().AddStackExchangeRedis(...)`). Blocks done in the **admin panel** take effect
in the API within 60 seconds (the cached session state); blocks done by the API itself (strikes, AI) apply on the next request and close
the learner's live connection at once.

## What Phase 7 adds

| Area | Done in Phase 7 | SRS |
| --- | --- | --- |
| Database | PointTransactions (ledger), UserPointsSummary, PointRules, Streaks, UserBadges, UserPrivacySettings, CertificateTemplates, Certificates | 7.5, 11 data model |
| Points wallet | One balance across all languages, written only as ledger rows (never by editing a total), each traceable to what earned it. Awards are **idempotent** (unique learner + action + reference), so a retried request or a regraded exam never pays twice | FR-S01, FR-S02, BR-20 |
| Earning | Lecture 10 (once) + study-ahead bonus 5/day early up to 15; daily quiz 2 per correct answer + 10 for 100%; monthly exam passed 100 + Merit 50 / Distinction 100 (once per exam); practice 5, four times a day; streak +5/day, +50 at 7 days, +200 at 30; certificate 500. All editable in **Points rules** | FR-S03, BR-21 |
| Limits | Daily cap 500 from the learner's local midnight, excluding exams, certificates, tournament prizes and admin changes; per-action daily limits; lecture points only after the minimum reading time | FR-S05, BR-22 |
| Tiers and badges | Bronze → Silver 1,000 → Gold 5,000 → Platinum 15,000 → Diamond 40,000; badges for first lecture, 7- and 30-day streaks, first exam passed, first certificate, 25 practice exercises, and each tier | FR-S06, FR-L111 |
| Streak | A day counts when the learner completes a lecture or a daily quiz, in their time zone; shown on Home, "at risk" the next day, broken after a missed day | BR-09, FR-L21 |
| Leaderboards | All languages or one; weekly (from Monday 00:00 UTC), monthly, all-time; top 50 with shared ranks for ties plus your own rank; opting out shows you as "Anonymous learner" to others; blocked learners are left out | FR-S07, FR-S24 |
| Certificate engine | Checked after every lecture, exam and grading change: all core lectures done, every published monthly exam passed, course grade ≥ 60% (BR-05 weighting), account in good standing. Band: Distinction ≥ 90, Merit ≥ 75, Pass. Level = the learner's target level. Number `LP-{LANG}-{YYYY}-{000001}` per language and year, never reused. Course marked Completed; 500 points; email | FR-L80, BR-06–BR-08, BR-12 |
| Certificate PDF | QuestPDF, A4 landscape: template heading and text with placeholders, accent colour, logo, signature, background, the facts, number, verification code and a QR code. Rendered on request, so it always matches the record | FR-L81, FR-L82 |
| Verification | Public page `/verify/{number}` on the API host (what the QR code opens) and `GET /api/v1/verify/{number}`: says Valid / Revoked / Not found to anyone; name, course, level and grade only with the verification code printed on the certificate; PDF download with the code; rate-limited | FR-L82 |
| Learner API | `/points`, `/points/history`, `/leaderboard`, `/me/leaderboard-visibility`, `/certificates`, `/certificates/{id}/pdf`, `/learner-courses/{id}/certificate-progress` | 8.1 |
| Admin panel | **Certificates** (search, PDF, verification page, revoke with optional points take-back, reissue with the same number, BR-06 checklist and "issue now"); **Certificate templates** (per-language override, images, live PDF preview); **Points rules**; learner page shows wallet, streak, badges, history, certificates, **add/deduct points with a reason** and revoke entries; dashboard counts certificates this month | FR-A01, FR-A82, FR-A83, FR-S10 |
| App | Home shows the real streak and points; lecture completion says "+15 points · 🔥 4-day streak" and new badges; Profile → **Points and badges** (tier progress, per-language, badges, history, leaderboard switch), **Leaderboard**, **My certificates** (PDF, share link, verify, and a checklist for courses still in progress) | FR-L21, FR-L83, FR-S09 |
| Tests | Tiers, caps, study-ahead, week start, streak, badges, eligibility, bands, numbers, codes, durations (unit); lecture points and streak, caps and rule changes, exam points once, adjustments and revokes, leaderboards and opt-out, the full certificate flow with PDF, email, verification, numbering, revoke and reissue, templates (integration) | NFR-15 |

**Not yet in Phase 7:** push notifications for certificates and streaks (email is sent now), leagues and friends' leaderboards (social phase),
referral points. Admin uploads (template images, lecture images) are saved where the API serves them: in Development the admin panel's
`Storage:LocalRoot` points at the API's `wwwroot`; in production give both hosts the same folder (or a blob store behind `IFileStorage`).

**QuestPDF licence:** the Community licence (free for organisations under USD 1M annual revenue) is set in code. Larger organisations need a
commercial QuestPDF licence.

## What Phase 6 adds

| Area | Done in Phase 6 | SRS |
| --- | --- | --- |
| Database | PracticeItems (the practice library), PracticeAttempts, UserWords (SM-2), Translations (history), TranslationCache; settings for limits | 7.3, 7.6 |
| Language services | `ITranslator` (Azure Translator REST v3, with auto-detect) and `ISpeechAssessor` (Azure Speech short-audio REST: speech-to-text and pronunciation assessment with per-word accuracy) behind interfaces, like the AI provider. Keys stay in configuration. **Offline stand-ins** (glossary translator, sample pronunciation scores) for development and tests, always marked "sample" | 5.1, FR-L92, FR-L100 |
| Read | Graded passages at the learner's level; tap any word to translate it and save it to My words; read-aloud with a speed slider; comprehension questions graded with explanations | FR-L90 |
| Write | Writing tasks per level; the AI tutor scores the text and lists every correction (original → corrected + why) | FR-L91 |
| Speak | Record a sentence; the phone's WAV (any rate, mono or stereo) is converted to 16 kHz mono on the server; scores for accuracy, fluency, completeness and each word | FR-L92 |
| Free talk | Chat with an AI partner on a topic at your level; your mistakes come back corrected; replies can be read aloud. Only the last few turns go to the model (setting) | FR-L92 |
| Listen | Dictation read by the device voice (normal or slow); typed answer compared word by word: right, misspelt (typed → expected), missing, extra | FR-L93 |
| My words | Words saved from lectures, reading passages and translations; list, search, add by hand; **SM-2 flashcards** (Again / Hard / Good / Easy) | FR-L94, FR-L103 |
| Translate tab | Text or voice input, auto-detect, swap, listen to both sides, save to My words, history of the last 100; shared cache so repeated phrases cost nothing; **100 translations per learner per day** from local midnight (clearing history doesn't reset it) | FR-L100–L104, BR-15 |
| Per-learner limits | AI writing checks + chat messages per day, pronunciation checks per day (settings); learners see how many are left. Provider and budget errors are shown to learners as "the AI tutor isn't available right now" | 5.4 |
| Exam speaking | Submitted monthly exams now also pre-grade speaking answers with pronunciation scoring against the sentence. Confident scores are applied (setting `Practice.AutoGradeExamSpeaking`); others wait in the grading queue with the score and words to check. Sample scores never grade automatically | FR-L63 |
| Admin panel | **Practice library**: reading passages with questions, writing tasks, speaking and dictation sentences; same Draft → review → publish workflow, previews in the review queue; **Draft with AI** for any type and level | 4.3 |
| Lectures | Tapping a word: listen, save to My words, or (if it isn't in the lecture's word list) translate it | FR-L33, FR-L103 |
| Tests | SM-2, dictation diff, WAV conversion, reading grading, day boundaries, cache keys, offline services and AI outputs (unit); practice flow, limits, speaking upload, translation with cache/limit/history, voice translation, My words, AI drafts, exam speaking pre-grade (integration) | NFR-15 |

**Not yet in Phase 6:** points/XP for practice and certificates (Phase 7). The practice library ships with ten Spanish samples (when `Seed:SampleContent` is on).

### Set up Azure Translator and Azure Speech

```bash
# Translator (global resources can leave Region empty)
dotnet user-secrets --project src/LinguaPath.Api set "LanguageServices:Translator:ApiKey" "<key>"
dotnet user-secrets --project src/LinguaPath.Api set "LanguageServices:Translator:Region" "westeurope"
# Speech: the API (practice, voice translation) and the admin panel (exam speaking pre-grade) both need it
dotnet user-secrets --project src/LinguaPath.Api   set "LanguageServices:Speech:ApiKey" "<key>"
dotnet user-secrets --project src/LinguaPath.Api   set "LanguageServices:Speech:Region" "westeurope"
dotnet user-secrets --project src/LinguaPath.Admin set "LanguageServices:Speech:ApiKey" "<key>"
dotnet user-secrets --project src/LinguaPath.Admin set "LanguageServices:Speech:Region" "westeurope"
```

Without keys, Development uses the offline stand-ins (`LanguageServices:OfflineWhenNoKey`); in Production the learner sees
"Translation isn't set up yet". Each language's **STT locale** (Languages → edit, e.g. `es-ES`) is the speech locale for that language.
The admin panel reads exam recordings from the API over HTTP when they aren't on its own disk (the two hosts have separate `wwwroot`).

## What Phase 5 adds

| Area | Done in Phase 5 | SRS |
| --- | --- | --- |
| Database | AiPromptTemplates, AiUsageLogs, AiGenerationRequests, AiGeneratedItems; Course.Source; Question.SourceText; AI score, confidence and feedback on exam answers | 7.6, 7.4 |
| AI service layer | One `IAiContentService` for every AI call: stored, versioned prompt template → provider → JSON validated against the expected shape (retried once, then Failed) → usage logged with tokens, cost and duration. Provider and model are set in Settings (DeepSeek or any OpenAI-compatible API); keys stay in configuration. An **offline provider** answers with clearly-labelled sample JSON for development and tests | 5, 5.3, FR-A110 |
| Cost control | Monthly budget: jobs pause at 100% (resume after raising the budget), alert in the audit log and on the dashboard at 80%; per-request estimate and actual cost; usage by feature and by day | 5.4 |
| New language workflow | "Generate with AI" on a language without courses → request with estimated modules, lectures, questions and cost → **Super Admin approves (with notes added to every prompt) or rejects** → roadmap drafted → admin edits it in the course builder → "Approve roadmap" → lectures and questions written module by module. Everything lands in Draft with Source = AI | FR-A21–A24, FR-A23 |
| Content generation | Roadmap for a new course (FR-A31); "Write with AI" for one lecture: explanation, rule table, 10 examples, vocabulary, common mistakes, summary — saved as a new version (FR-A42); **AI assist** in the editor: expand, simplify, add examples (FR-A43); "Fill empty lectures with AI" for a whole course (FR-A44); questions grounded only in a lecture's text, each keeping its source sentence (FR-A62); a month of tips / words of the day scheduled one a day (FR-A52) | FR-A31, A42–A44, A52, A62 |
| Review | Review queue, lecture and question editors show the prompt, model and a "native speaker check required" flag for low-resource languages (setting) | FR-A72, BR-13 |
| Writing pre-grading | When an exam with writing is submitted, AI grades each answer with a rubric. At or above the confidence setting (80%) the grade is applied; otherwise the answer stays in the grading queue with the AI score and feedback pre-filled | FR-L63 |
| Background jobs | AI jobs are rows in the database worked through by a hosted runner in the admin panel: survive restarts, show progress, can be cancelled, retried or resumed; finished parts are skipped on retry | 5.3 |
| Admin panel | **AI generation** (requests, approvals, progress, drafts created, cost), forms for roadmap, questions and posts; **AI usage & prompts** (budget, cost per day/feature, recent calls, prompt editor with versions); AI buttons on Languages, Courses, the builder, lecture editor, question bank and posts; AI suggestion in the grading queue | 4.3–4.8 |
| Tests | Prompt rendering, JSON extraction/validation, cost and estimate, AI question mapping, offline provider (unit); new-language workflow end to end, questions/posts/assist, writing pre-grading, budget pause and resume (integration) | NFR-15 |

**Not yet in Phase 5:** speaking evaluation and learner-side AI (writing check, conversation partner, translation) come with the practice phase;
Hangfire is not used — the database-backed runner does the same job without another dependency and can be swapped later.

### Set up the AI provider

```bash
# DeepSeek (default in Settings: Ai.Provider = DeepSeek, Ai.Model = deepseek-chat)
dotnet user-secrets --project src/LinguaPath.Admin set "Ai:Providers:DeepSeek:ApiKey" "<your key>"
dotnet user-secrets --project src/LinguaPath.Api   set "Ai:Providers:DeepSeek:ApiKey" "<your key>"
# OpenAI or any OpenAI-compatible API: set Ai.Provider = OpenAI in Settings and Ai:Providers:OpenAI:ApiKey (BaseUrl is configurable)
```

Without a key, Development uses the **offline provider** (`Ai:OfflineWhenNoKey`), so every AI button works and produces sample drafts marked as such.
Set `Ai.Provider` to `Offline` in Settings to force it. Token prices for cost tracking are the `Ai.InputPricePerMillionUsd` / `Ai.OutputPricePerMillionUsd` settings.
The AI job runner runs in the **admin panel** (`Ai:Runner:Enabled`); the API only queues jobs (e.g. writing pre-grading after an exam).

## What Phase 4 adds

| Area | Done in Phase 4 | SRS |
| --- | --- | --- |
| Database | Questions, QuestionOptions, Quizzes, QuizSections, QuizQuestions, QuizAttempts, AttemptAnswers, MarkOverrides, ReviewCards | 7.4 |
| Question types | Multiple choice (one or several correct), true/false, fill in the blank, match pairs, reorder words, translate, listen and choose, speak the sentence, writing. Objective answers are graded instantly (case and punctuation ignored, accents count); writing and speaking go to the grading queue | FR-L51, FR-L63 |
| Daily quiz | One per active course per day (learner's time zone): questions from lectures done in the last 2 and 7 days plus due review items (rule editable in admin). Instant feedback with explanation; first answer is final; a quiz finished after midnight counts as practice (setting); "practise again" never changes marks | FR-L50–L54, FR-A63, BR-01 |
| Review list | Wrong answers go into a spaced-repetition list (Leitner boxes 1, 2, 4, 7, 15 days) that feeds the daily quiz | FR-L54 |
| Monthly exams | Exam builder per course and roadmap month: sections by skill/type, random-from-pool or fixed questions, time limit, pass mark. Unlocks at 80% of the month's lectures or on the month's last planned date. Server-side timer, auto-save, resume after a crash, auto-submit when time runs out (background job), shuffled questions and options, one active exam per learner, app-left events logged. Retakes: 2, 7 days apart, best score counts | FR-L60–L65, FR-A64, BR-02–BR-04 |
| Placement test | Optional ~10-question test (2 per CEFR level from the course's start level) that suggests the starting level in onboarding | FR-L11 |
| Marks | Every quiz and exam with score and pass/fail, course grade (30% daily quizzes + 70% best exam per month), score trend and skill breakdown charts, answer review after the attempt closes | FR-L70–L73, BR-05 |
| Admin panel | **Question bank** (filters by language, lecture, level, skill, type, difficulty, status; analytics with % correct, average time and flags for &lt;20% / &gt;95%), type-aware editor, review → publish, **Excel import/export** with template; **Quizzes & exams** (exam builder + daily quiz rule); **Grading queue** (read writing, play recordings, score + feedback); attempt details per learner with **mark override** (reason required, audited) | FR-A60, A61, A63–A66, A70, A80, A81, BR-14 |
| MAUI app | Quiz player for every question type (tap choices, type answers, pick matches, build sentences from word tiles, listen with the device voice or audio file, record speech), daily quiz card on Home, Monthly exams list with lock/retake status, exam timer and question navigator, result page with skill bars and answer review, My marks with trend and skill charts, placement test in onboarding | FR-L11, FR-L50–L73 |
| Tests | Grader, review scheduler, course grade, placement, midnight rules (unit); daily quiz, exam with grading and override, placement, Excel round trip (integration) | NFR-15 |

**Not yet in Phase 4:** AI scoring of writing and speaking, and AI-generated questions from a lecture (FR-A62) arrive in Phase 5 with the AI service layer;
until then a reviewer grades writing and speaking answers. Points/XP and streaks come with the points wallet (Phase 7). Certificates are Phase 7.

### Try it quickly

With `Seed:SampleContent` = `true`, the sample Spanish course gets about 80 questions, a published **Month 1 exam** and placement questions from A1 to C1.
In the app: onboarding → Spanish → *Not sure? Take a 10-question placement test*; finish a lecture → Home shows the **Daily quiz**; *Monthly exams* shows the month 1 exam
(it opens when 80% of month 1 is done, or on its planned date — for a quick try, set `Assessment.MonthlyExamUnlockPercent` to 0 in Settings).

## What Phase 3 adds

| Area | Done in Phase 3 | SRS |
| --- | --- | --- |
| Database | LectureExamples, LectureVocabulary, LectureLinks, LectureVersions, Posts, PostInteractions, ReviewItems, ContentReports, LectureBookmarks, LectureNotes, LectureRatings; Lecture gains cover image and publish date | 7.3, 7.4 |
| Content rules | Draft → In review → Approved → Published → Archived. Only **Published** lectures appear in courses and roadmaps. A reviewer can't approve their own submission (Super Admin can). Lesson HTML is cleaned on save (allow-list; no scripts, inline styles or `data:` images) | FR-A44–A46, FR-A70–A72, NFR-07 |
| Roadmap sync | Publishing, unpublishing, deleting or reordering lectures and modules, or changing a published lecture's minutes / core flag, updates every enrolled learner's roadmap right away. Completed lectures stay completed | FR-A32, BR-10 |
| Learner API | `GET /lectures/{id}`, progress, complete (minimum reading time from `Learning.MinReadingTimePercent`, error `too_quick`), bookmarks, note, rating, report; posts feed (my languages, pinned first, scheduled posts hidden until due), like, save, report | FR-L32–L38, L40–L42 |
| Admin panel | **Lectures** list + rich-text editor (Quill, with "Edit HTML"), examples / vocabulary / links rows, *Preview as learner* in a phone frame (same HTML template as the app), version history with side-by-side view and restore; **Posts** (schedule, pin, link to a lecture); **Review queue**; **Error reports**; dashboard counts; the roadmap builder links each lecture to its content | FR-A40–A47, A50–A52, A70–A72 |
| MAUI app | Lecture reader: lesson text, **tap a word** for its meaning (from the lecture's vocabulary), **read aloud** (device voices in the course language), examples and vocabulary with 🔊, links, related tips, bookmark, note, 1–5 star rating, report a mistake, reading timer and **Mark as complete** → offers the next lecture, **offline download**. Tips & posts feed (all / saved, like, save, report) and Bookmarks | FR-L32–L38, L40–L42 |
| Tests | Sanitizer, reader template, unlock rule and reader helpers (unit); reader, completion, bookmarks/notes/ratings/reports, feed, review → publish → roadmap sync (integration) | NFR-15 |

**Not yet in Phase 3:** translating words that are not in the lecture's word list (Translator arrives with the practice tools), AI-drafted lectures
(Phase 5 adds AI generation into the same review queue), audio files per example (read-aloud uses device voices for now), image uploads in the editor
(images by URL for now).

### Try it quickly

With `Seed:SampleContent` = `true` (Development), the *Spanish Essentials* lectures get sample lesson text, examples and vocabulary and are Published,
and 4 sample posts are added. Enrol in the app, open the first lecture, tap "hola" in the text, then read for half the estimated time and mark it complete.

## What Phase 2 adds

| Area | Done in Phase 2 | SRS |
| --- | --- | --- |
| Database | Courses, Modules, Lectures (planning fields; lesson text in Phase 3), LanguageRequests, LearnerCourses, LearnerRoadmapItems | 7.2, 7.3 |
| Roadmap planner | Paces lectures over the learner's study days up to the daily minutes; drops optional lectures first when time is short; suggests a later end date or more minutes when even core lectures don't fit | BR-10, FR-L15, FR-A32 |
| Learner API | Courses of a language, "request this language", plan check, enrol, my courses, today, roadmap, change plan (re-pace, progress kept), pause / resume (30 days per course, deadlines move) | FR-L10–L16, L20–L24, L30–L31, L120–L121, BR-11 |
| Admin panel | Languages (add, edit, publish rules), language requests (grouped, most wanted first), courses, **roadmap builder** with drag-and-drop modules and lectures, core/optional lectures, monthly exam checkpoints, publish / unpublish / archive; dashboard and learner page show enrolments | FR-A10–A12, A20, A30, A32, A33 |
| MAUI app | Onboarding wizard (language → level → goal → time, days, reminder, focus → plan check → start), Home driven by today's plan with course switcher and pull-to-refresh, Learn tab with the grouped roadmap, Course plan screen (edit + pause/resume) | 2.3, FR-L10–L24, L30, L120, L121 |
| Tests | Planner unit tests (pacing, optional dropping, suggestions, time zones) and onboarding integration tests | NFR-15 |

**Not yet in Phase 2:** the placement test (needs the question bank, Phase 4).
In Development, `Seed:SampleContent` seeds Spanish with a sample *Spanish Essentials (A1 → A2)* roadmap (4 modules, 20 lectures, 4 optional).

## What Phase 1 delivered

| Area | Done in Phase 1 | SRS |
| --- | --- | --- |
| Solution | Clean Architecture: Domain, Application, Infrastructure, Api, Admin, Shared, Mobile.Core, Mobile, tests | 2.2 |
| Database | Users (extended Identity), roles, refresh tokens, devices, email OTPs, blocked identities, languages, system settings, audit log | 7.1, 7.2, 7.6, 11.7 |
| Seed | 5 roles, first Super Admin, 12 languages, every business-rule default as a setting | 1.3, 6, 11.6 |
| Auth API | Register, email OTP verify/resend, login, Google, Apple, refresh rotation with reuse detection, logout, logout all devices, forgot/reset password | FR-L01–L04 |
| Profile API | Get/update profile, photo upload, change/set password, delete account (anonymised), export my data | FR-L05, FR-L06 |
| Other API | Languages list, device registration, app version check / force update | FR-L10, FR-A112 |
| Plumbing | `{ success, data, message, errors }` envelope, RFC 7807 errors, FluentValidation, Serilog + correlation id, rate limiting, Swagger, health check | 8.3, NFR-05/06/14 |
| Admin panel | Login, role-based sidebar (later modules shown as P2–P10), dashboard, admin users + roles, learners (search, details, suspend), settings editor, audit log | FR-A01, A90, A91, A92, A111, A112 |
| MAUI app | Splash (version check, restore session), Welcome, Login, Register, Verify email, Forgot/Reset password, Home, Profile, Edit profile, Change password; tabs Home/Learn/Practice/Translate/Profile | 2.3, FR-L01–L06 |
| Tests | Unit tests (tokens, OTP, validators, version check, mobile session refresh, API error parsing) + API integration tests on in-memory SQLite | NFR-15 |

## Requirements

- Visual Studio 2022 17.14+ or Visual Studio 2026 with **ASP.NET** and **.NET MAUI** workloads, .NET 10 SDK
- SQL Server 2019+ or LocalDB (installed with Visual Studio)
- Android emulator (API 26+) or an iPhone/iOS simulator (iOS 15+, needs a Mac)

## Run it

### 1. Set the JWT signing key (once)

The API refuses to start without a signing key of 32+ characters. Development already has one in
`appsettings.Development.json`. For any shared or production environment, use user-secrets or an environment variable:

```powershell
cd src/LinguaPath.Api
dotnet user-secrets set "Jwt:SigningKey" "<a long random string, 32+ characters>"
```

### 2. Database

The default connection string uses LocalDB: `Server=(localdb)\MSSQLLocalDB;Database=LinguaPath`.
Change `ConnectionStrings:DefaultConnection` in **both** `src/LinguaPath.Api/appsettings.json` and `src/LinguaPath.Admin/appsettings.json` for a full SQL Server.

Create the migration(s) (one per phase):

```powershell
dotnet tool install --global dotnet-ef
# Fresh database: one migration covers Phases 1-7
dotnet ef migrations add Initial -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
# Upgrading a database from an earlier zip? Add one migration per phase you haven't applied yet:
dotnet ef migrations add Phase2_Courses -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase3_Content -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase4_Assessment -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase5_Ai -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase6_Practice -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase7_Progress -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase8_Social -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase9_Competitions -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations add Phase10_Release -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
```

Phase 10 adds the `Notifications` table, `AspNetUsers.DateOfBirth` and `Posts.NotifiedAt`.

To get a SQL script for your DBA instead of applying it automatically:

```powershell
dotnet ef migrations script -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o database/LinguaPath.sql --idempotent
```

On start-up, the API and the admin panel apply pending migrations and seed roles, the Super Admin, languages and settings.
Without migrations they still start, creating the schema directly; a warning in the log reminds you to add migrations.

### 3. Start the API and the admin panel

In Visual Studio: right-click the solution → **Configure Startup Projects** → *Multiple*: `LinguaPath.Api` and `LinguaPath.Admin` → Start.
Or from a terminal:

```powershell
dotnet run --project src/LinguaPath.Api --launch-profile http      # http://localhost:5080/swagger
dotnet run --project src/LinguaPath.Admin --launch-profile https   # https://localhost:7090
```

Admin sign-in: **admin@linguapath.local** / **Admin@12345** (from `Seed` settings). Change it right away from *My account → Change password*.

### 4. Emails in development

`Email:Mode` is `Pickup`: emails are saved as `.html` files in `src/LinguaPath.Api/App_Data/emails` and the subject
(which contains the 6-digit code) is written to the API console. For real email set `Email:Mode` to `Smtp` and fill `Host`, `Port`, `UserName`, `Password`.

### 5. Run the MAUI app

Set `LinguaPath.Mobile` as the startup project, choose an Android emulator, and run.
In Debug builds the app calls `http://10.0.2.2:5080` (Android emulator → your PC) or `http://localhost:5080` (iOS simulator).
For a physical phone, change `AppConfig.ApiBaseUrl` in `MauiProgram.cs` to your PC's LAN IP and start the API with the `http` profile (it listens on all interfaces).

### 6. Google and Apple sign-in (optional in development)

| Setting (API) | Used for |
| --- | --- |
| `ExternalAuth:GoogleWeb:ClientId` / `ClientSecret` | Google sign-in from the app via the browser flow. Add `https://<api-host>/signin-google` as an authorised redirect URI in Google Cloud. |
| `ExternalAuth:GoogleClientIds` | Client ids accepted for `POST /api/v1/auth/google` (ID tokens obtained natively). |
| `ExternalAuth:AppleAudiences` | Your iOS bundle id `com.linguapath.app` (and Services ID if used) for `POST /api/v1/auth/apple`. Also enable *Sign in with Apple* for the App ID. |

Buttons still show when these are empty; the API answers "sign-in is not enabled".

## Tests

```powershell
dotnet test tests/LinguaPath.UnitTests
dotnet test tests/LinguaPath.IntegrationTests
```

Integration tests start the real API in memory against SQLite, register a learner, read the OTP from a captured email, and check
login, refresh-token rotation and theft detection, sign-out everywhere, password reset, validation errors, profile update, account deletion,
languages and the version check.

`LinguaPath.Server.slnf` opens everything except the MAUI project, which is useful on machines without the MAUI workload or in CI.

## Phase 10 test checklist

1. **Inbox:** sign in, enrol, set the course's study time a few minutes ahead (Course plan). Within 15 minutes of it the 🔔 on Home shows 1;
   open it, tap the reminder → Home. Swipe to delete. (Or call `IReminderService.RunAsync` from a test — see `ReleaseTests`.)
2. **New post:** publish a post for the learner's language in the admin panel → a "New in …" entry appears after the next reminder run.
3. **Urdu:** Edit profile → App language → اردو → Save. The app reloads right to left in Urdu; switch back to English the same way.
4. **Offline:** download a lecture, turn on airplane mode, mark it complete and review some flashcards; turn it off → toast
   "your offline progress was saved", and the roadmap shows the lecture done.
5. **Age:** sign up with a date of birth 12 years ago → refused. 16 years ago → you can chat only with friends.
6. **Analytics:** Admin → Analytics reports, try each tab, export Excel and PDF; Admin → Audit log shows the exports.
7. **Health:** `GET /health/ready` → `Healthy` with a `database` check; response headers include `Content-Security-Policy`.
8. **Crash report:** throw in a debug build (e.g. a test button) or post to `/api/v1/client-errors`; it appears in the log/Seq.
9. **Containers:** `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build`, then open the admin on :8081.
10. **Load:** run `tests/load/api-smoke.js` and `chat.js` against staging; both must pass their thresholds.

## Phase 9 test checklist

Questions come from the published question bank; the sample course seeds some. Publish more (Question bank) so 10-question matches have enough.

- [ ] Two learners on two devices: *Profile → People* → open the other → *⚔️ Challenge* → live, 5 questions → the other gets the challenge under *Compete → Now* → *Accept*
- [ ] Both see "Starting in 5 s", then the same question; answer on one phone → the other phone's progress bar moves; when both have answered the result shows at once (✅ / the right answer, scores) and the next question opens after 3 s
- [ ] Let a question run out on one phone → "⏰ Time's up"; after 5 questions → result screen: winner +30, loser +5, rating change, per-question review
- [ ] *Share to chat* → the card appears in the chat; tap it → the result opens. *Rematch* → a new challenge
- [ ] Challenge with *Play live* off → play your run now; the other accepts later and plays; their score shows only after both are done
- [ ] *Quick match* on one device, then on the other (same language, levels within one) → they're paired. Alone: after 30 s "Play the practice bot?" → bot match, practice points only, no rating
- [ ] Leave a live match halfway → it counts as a loss with 0 points
- [ ] Beat the same friend 4 times today → the 4th win pays 0 points (*Settings* → `Competition.MaxWinsVsSameOpponentPerDay`)
- [ ] Group of 3+ with a language → group info → *⚔️ Start a group battle* → card in the group → members tap *Join the battle* → *Start now* → leaderboard after every question → 1st +30, 2nd +20, 3rd +10
- [ ] Admin: *Tournaments* → *New tournament* (entry now, start in 10 min, 2 rounds, 5 questions) → *Publish* → learners *Join* in the app → *Start now* → learners *Play my round*; after round 1 the top half get round 2; at the end prizes are paid (wallet history)
- [ ] Admin: *Disqualify* the winner → their prize is revoked and the others move up a place
- [ ] *My league*: points from this week, ⬆️ for the top 5; Monday the clock closes the week and you're promoted/relegated
- [ ] *Privacy → Who can challenge me = Nobody* → a challenge to you is refused; a suspended learner can't start matches

## Phase 8 test checklist

- [ ] Two learners (two emulators, or the app + Swagger): *Profile → People* → search the other by display name → *Add friend*; the other sees the request and accepts
- [ ] *💬 Chats → New chat* → message → it appears live on the other device; ✓ → ✓✓ when their app is open → "✓✓ seen" when they open the chat; "typing…" shows while they type
- [ ] Double-tap a message → Reply / React / Translate / Delete for me / Delete for everyone / Report; deleted messages show "This message was deleted" on both sides
- [ ] Send `see example.com`, `name at gmail`, `0300 1234567`, `add me on snapchat` → each refused; on the 5th the "Community rules" warning appears and must be accepted
- [ ] Send `damn` → delivered as `d***`; send `stupid` → refused (strike); send `idiot` → chat suspended 24 h (learning still works)
- [ ] Admin *Moderation queue* → *All events* → the learner's events; open one → context and strike history; *Block permanently* → the app signs out within a minute and can't sign in or register again with that email
- [ ] Super Admin: learner page → *Unblock* with a reason → can sign in again; the audit log has the block and the unblock
- [ ] Send `honestly nobody likes you` (not on the word list) → delivered, then removed within seconds by the AI scan and a strike added (offline AI in development)
- [ ] Report a message → it's in the queue with the reporter; *Dismiss* → the report closes; *Moderation report* shows the false-positive rate
- [ ] *Abusive words* → add a Spanish-only word, test it in the test box, export to Excel, import the file back (0 added, 0 updated)
- [ ] *Link rules* → add exception `linguapath\.com` → `see linguapath.com` is delivered; add pattern `\bdiscord\b` → "let's use discord" is refused
- [ ] *New group* with a friend → announcement mode on → the member can't send; slow mode 30 s → a member waits between messages; make the member admin; leave as owner → they become owner
- [ ] Make a public group with "approve new members" → a third learner finds it in *Find groups* → *Ask to join* → admin approves in *Group info*
- [ ] *Privacy* → Who can message me = Friends only → a non-friend can't start a chat; Read receipts off → no "seen" either way; *Leaderboard → Friends*
- [ ] Block a learner from their profile → they can't open your profile or message you; *Privacy → Blocked learners → Unblock*

## Phase 7 test checklist

- [ ] App: complete a lecture → "+15 points · …" (more if you finish before its planned day); Home shows 🔥 1 and the points
- [ ] Complete the same lecture again (reopen it) → no new points; Profile → *Points and badges* → "First lecture" badge lit, history lists the lecture and the streak day
- [ ] Take today's daily quiz → 2 points per correct answer, +10 for 100%; the streak stays at 1 (same day)
- [ ] Do five practice exercises → only four pay 5 points (*Points rules* → PracticeSession → Times a day)
- [ ] Super Admin: Settings → `Points.DailyCap` = 20 → further lecture points stop at 20 for today; passing an exam still pays 100+
- [ ] *Leaderboard*: switch Week / Month / All time and a language; turn off "Show my name on leaderboards" → another learner sees "Anonymous learner"
- [ ] Admin: *Learners* → a learner → *Add or deduct points* +1,000 with a reason → tier Silver and the Silver badge; ✕ on the entry → back to Bronze, badge stays; audit log has both
- [ ] Sample course: finish every core lecture and pass the Month 1 exam with ≥ 60% → within a moment *My certificates* shows the certificate, an email arrives (Pickup folder), +500 points, course Completed
- [ ] *PDF* opens the certificate; *Share* shares the verification link; scanning the QR code shows "Valid" with the details; the same page without `?c=` only says Valid and asks for the code
- [ ] Admin: *Certificates* → open it → *Revoke* (tick "take back points") → public page says revoked, app PDF refused; *Restore and reissue* → valid again, same number, v2
- [ ] Admin: *Certificate templates* → new template for Spanish with a logo, signature and green accent → *Preview PDF* → save → *Reissue* the certificate → the PDF uses it
- [ ] Learner page → course → *Certificate rule* shows the BR-06 checklist; lower `Certificate.MinGrade`, then *Issue the certificate now* for a learner who qualifies

## Phase 6 test checklist

- [ ] App → *Practice* tab: the course's language and level show at the top; each card shows "Not tried yet" or your average
- [ ] *Read*: tap a word → translation → *Save to My words*; move the speed slider and *Read*; answer the questions → score, ticks and explanations; *Show translation*
- [ ] *Write*: write a few sentences starting with a small letter → score, feedback and the correction "mi → Mi" (offline sample) or real corrections with a key
- [ ] *Speak*: *Listen first*, then *Record* the sentence → score and coloured word chips; *Play my recording*. Deny the microphone → clear message
- [ ] *Listen*: *Play* / *Slowly*, type with a mistake and without an accent → misspelt words in orange (typed → expected), missing in red
- [ ] *Free talk*: pick a topic, write "quiero ir a México" → reply plus the corrected sentence; replies are read aloud (toggle)
- [ ] Super Admin: Settings → `Practice.AiCallsPerUserPerDay` = 1 → the second AI check says the daily limit is reached
- [ ] *Translate* tab: Detect → type "Hello friend" → result, "Detected: English"; ⇄ swap; 🎤 with *From* = Spanish; *Save to My words*; history; *Clear*
- [ ] Settings → `Translation.DailyLimitPerUser` = 3 → the fourth translation of the day is refused, also after clearing history
- [ ] Lecture: tap a word in the word list → *Save to My words*; tap another word → *Translate* → *Save*
- [ ] *My words* → search, swipe to delete, *Add*; *Review* → flip cards, Again (comes back in the session) / Good / Easy → "All done"
- [ ] Admin: *Practice library* → new reading passage with 2 questions (options one per line) → *Save and send for review* → Reviewer previews and approves → *Publish* → it appears in the app
- [ ] Admin: *Practice library → Draft with AI* → 5 dictation sentences at A2 → Drafts with the AI badge and prompt
- [ ] App: monthly exam with a speaking question → after submitting, the grading queue shows "Pronunciation …/100" with words to check (sample scores without a Speech key)

## Phase 5 test checklist

- [ ] Admin (Content Admin): *Languages* → add a language with no courses → *Generate with AI* → change the hours and watch the estimate → send for approval
- [ ] Admin (Super Admin): *AI generation* → open the request → approve with a note ("business context") → within a few seconds a Draft course appears (AI badge); the request says "Review the roadmap"
- [ ] Builder shows the AI banner: rename a module, delete a lecture → *Approve roadmap and write the content* → progress bar fills lecture by lecture; lectures get text, 10 examples and vocabulary; questions appear in the question bank as AI Drafts with "based on: …"
- [ ] Review queue: AI items show the model, the prompt (expand) and, for languages in `Ai.NativeCheckLanguages`, "native speaker check required"
- [ ] Lecture editor: *Write with AI* on a Draft lecture → new version in *Versions*; type an outline → *AI assist → Expand* → confirm → text replaced (not saved until *Save*)
- [ ] Course builder: *Fill empty lectures with AI* → only lectures without text are written
- [ ] Question bank: *Generate from a lecture with AI* → choose types and count → Drafts appear
- [ ] Posts: *Draft a month with AI* → 30 Draft posts, one per day at 06:00 UTC
- [ ] Super Admin: Settings → `Ai.MonthlyBudgetUsd` = 0.0001 → start a job → it pauses with a budget message; raise the budget → *Resume* → finishes
- [ ] Settings → `Ai.Provider` = DeepSeek with a real key → generate a lecture → *AI usage & prompts* shows tokens and cost; edit the grammar_lecture prompt → v1 active; switch back to built-in by activating another version
- [ ] App: take a monthly exam with a long writing answer and a one-word one → within a minute the long one is graded by AI ("AI (model)") and the short one waits in the grading queue with the AI suggestion pre-filled

## Phase 4 test checklist

- [ ] Admin: *Question bank* lists the sample Spanish questions; filter by lecture, type and skill; *Export to Excel* downloads the template with a "How to fill in" sheet
- [ ] Admin: edit the exported file (change a question, add a row without Id) → *Import* → "1 added, N updated"; the new question is Draft
- [ ] Admin: *New question* → switch the type and watch the fields change (gap ___ for fill-in, pairs for matching, sentence for translate/listen/speak) → save without a gap → clear error
- [ ] Admin: send a question for review → a Reviewer approves it in *Review queue* (preview opens) → *Publish*
- [ ] Admin: *Quizzes & exams* → open *Month 1 exam* → sections show warnings when the pool is too small; add a fixed section by ticking questions → save → publish
- [ ] App: onboarding → Spanish → *placement test* → answer → result shows a suggested level → *Use this level* fills the level step
- [ ] App: finish a lecture → Home *Daily quiz* → each answer shows right/wrong with the explanation → result page with skill bars → Home shows *Done*; *Practise again* doesn't change marks
- [ ] App: next day (or change the device date) → the daily quiz includes questions you got wrong
- [ ] App: *Monthly exams* → locked with "Unlocks at 80%…"; complete lectures (or set the unlock % to 0) → *Start exam* → timer counts down, question navigator jumps around, answers survive closing and reopening the app
- [ ] App: during the exam switch to another app and back → Admin *Learners* → the attempt shows "left the app"
- [ ] App: finish the exam with a writing and a speaking answer → result "Submitted! A teacher is grading…"
- [ ] Admin: *Grading queue* → read the text, play the recording → give points and feedback → the app result shows the final % and the teacher's feedback in *Review answers*
- [ ] Admin: open the attempt → change a mark with a reason → audit log has the change; the learner's mark updates
- [ ] App: *My marks* → course grade, daily and exam averages, trend bars (exam bars orange), skill bars, list of every attempt
- [ ] Let an exam run out of time → it is submitted automatically within a minute

## Phase 3 test checklist

- [ ] Admin: *Lectures* lists the Spanish lectures as Published; filter by course and status works
- [ ] Admin: open a lecture → change the text with the editor (heading, list, table, link) → *Save* → version goes up; *Versions* shows both, *View* compares side by side, *Restore* brings the old text back as a new version
- [ ] Admin: paste `<script>alert(1)</script>` with *Edit HTML* → save → the script is gone
- [ ] Admin: add 2 examples, 3 vocabulary words and a video link (add/remove rows) → save → *Preview as learner* shows them and the phone-frame text
- [ ] Admin: *Courses* → builder → *Add lecture* → it's a Draft and not in learners' roadmaps → write text → *Send for review*
- [ ] Admin (same user): *Review queue* → *Approve* → refused ("another reviewer"); as a second Reviewer → *Send back* with a comment → the editor shows the comment; resubmit → approve → *Publish* → the lecture appears at the end of the learner's roadmap
- [ ] Admin: *Posts* → new tip for Spanish, scheduled 2 minutes ahead, pinned, linked to lecture 1 → submit, approve, publish → it shows in the app after 2 minutes, on top, and at the end of lecture 1
- [ ] App: open the first lecture → it's *In progress* in the roadmap; tap a vocabulary word in the text → meaning + *Listen*; *Read aloud* reads the text in a Spanish voice
- [ ] App: *Mark as complete* is disabled with a countdown until half the estimated time has passed → complete → *Start next* opens lecture 2; Home progress goes up
- [ ] App: open a locked lecture from the roadmap → "Finish the lectures before this one" toast
- [ ] App: bookmark, write a note, rate 4 stars, *Report a mistake* → reopen: all kept; *Bookmarks* lists it; Admin *Error reports* shows the report → *Resolved*
- [ ] App: *Download* a lecture → airplane mode → open it again → "showing your downloaded copy"; complete is blocked offline with a clear message
- [ ] App: *Tips & posts* → like, save, *Saved* filter, report
- [ ] App with an Arabic or Urdu course (add one in Admin): lecture text and examples are right-to-left

## Phase 2 test checklist

- [ ] Admin: *Languages* lists 12 languages; Spanish is Published (sample content) and others Coming soon
- [ ] Admin: try to publish a language with no published course → blocked with a clear message
- [ ] Admin: *Courses & roadmaps* → open the Spanish course → drag a module and a lecture (also into another module) → reload → order kept
- [ ] Admin: add a module (level, month checkpoint) and a lecture (minutes, optional) → totals in the header update
- [ ] App: Home shows *Start your first course* → onboarding: Spanish → Beginner → A2 + Travel → 3 months, 20 min, Mon–Sat → plan says "Great plan" → *Start learning*
- [ ] App: Home shows the course, 0% progress, days left and today's lecture; *See full roadmap* shows modules with the first lecture *Next* and the rest *Locked*
- [ ] App: onboarding again with 1 month, 10 min, Saturday only → *needs more time* with "Study N minutes a day" / "Finish on …" buttons that re-check the plan
- [ ] App: tap a *Coming soon* language → *Request it* → the request appears in Admin → *Language requests*
- [ ] App: course *Plan* → change daily time / days / end date → *Save and re-plan* → message describes the new plan
- [ ] App: *Pause course* for 5 days → Home shows *Paused until …*; *Resume now* → back to active
- [ ] Admin: *Learners* → open the learner → *Courses* card shows the enrolment and progress

## Phase 1 test checklist

- [ ] API starts, `/swagger` loads, `/health` returns Healthy
- [ ] Admin: sign in as Super Admin; sidebar shows Phase 1 modules and greys out later ones (P2–P10)
- [ ] Admin: create a Content Admin; sign in as them in a private window and confirm *Admin users*, *Settings* and *Audit log* are hidden
- [ ] Admin: change a setting, then see the change in *Audit log*
- [ ] App: register → code arrives (API console / `App_Data/emails`) → verify → lands on Home with your first name
- [ ] App: wrong code shows "attempts left"; *Resend code* is disabled for 60 seconds
- [ ] App: sign out, sign in again; *Forgot password* → reset → sign in with the new password
- [ ] App: Profile → Edit profile (time zone, app language, reminders) → saved; change photo
- [ ] App: *Sign out on all devices* → any other signed-in device goes back to Welcome the next time it talks to the server
- [ ] Admin: suspend the learner → the app goes back to Welcome on its next request; signing in shows "Your account is suspended"
- [ ] Admin: set `App.MinVersion.Android` to `2.0.0` → app shows *Update required* on start; set back to `1.0.0`
- [ ] App: Profile → *Download my data* shares a JSON file; *Delete account* removes the account and the email can no longer sign in

## Project layout

```
src/
  LinguaPath.Domain          entities, roles, setting keys
  LinguaPath.Shared          request/response contracts shared by API and app
  LinguaPath.Application     services, validators, business errors (Result<T>)
  LinguaPath.Infrastructure  EF Core DbContext, Identity, JWT, OTP, email, file storage, seed data
  LinguaPath.Api             REST API /api/v1, Swagger
  LinguaPath.Admin           admin panel (MVC + Bootstrap 5)
  LinguaPath.Mobile.Core     API clients, session/tokens, ViewModels (no MAUI types, unit-tested)
  LinguaPath.Mobile          MAUI app: pages (XAML), Shell, platform services
tests/
  LinguaPath.UnitTests
  LinguaPath.IntegrationTests
  load/                      k6 load tests (Phase 10)
deploy/                      Dockerfiles, docker-compose, .env template (Phase 10)
database/ops/                backup, restore and health SQL (Phase 10)
docs/                        operations, security, release, acceptance (Phase 10)
.github/workflows/ci.yml     build, test, mobile builds, images
```

## Notes and decisions

- **Defaults chosen:** AI provider DeepSeek (setting `Ai.Provider`), admin panel MVC, Azure Speech and Translator behind interfaces (Phase 6).
- **Recordings are WAV**: the app records with Plugin.Maui.Audio; the API converts any PCM WAV to 16 kHz mono, the format Azure's
  short-audio API expects (up to 60 seconds). Other formats are refused for practice with a clear message.
- **Translations are cached** by a hash of source language, target language and the whitespace-normalised text (case kept, because it changes
  meaning). Sample (offline) translations are never cached. Detected-language requests are cached under "auto".
- **The listening sentence is sent to the app** so the device voice can read it; the app hides it until the learner checks. It is practice, so
  that is an acceptable trade for not storing generated audio.
- **Points are a ledger**: totals are sums of `PointTransactions`; the `UserPointsSummary` row is a cache rebuilt after every change.
  Revoking keeps the row (crossed out in history) so every change stays traceable. Tier badges stay once earned.
- **Certificate privacy**: the public page confirms validity by number alone; personal details need the code printed on the certificate,
  so certificate numbers can't be walked to collect names. The page is `noindex` and rate-limited (30 a minute per IP).
- **Certificates freeze what they print** (name, grade, level, template) at issue; *Reissue* refreshes them and keeps the number.
- **SM-2**: intervals 1, 6, then interval × ease; "Easy" adds 30%; "Again" resets the card, counts a lapse and brings it back in the same session;
  ease never goes below 1.3; intervals are capped at a year.
- **Refresh tokens** are stored as SHA-256 hashes and rotate on every use; reusing an old one revokes that sign-in everywhere.
- **Access tokens** carry the security stamp; signing out everywhere, password reset, suspension or blocking stop them within 60 seconds.
- **Deleted accounts** are anonymised immediately; the row stays so later records (marks, chat) keep valid links.
- **Blocked identities** (email, Google/Apple id, device id) are stored on a permanent block, so the learner can't come back with a new account.
- **Moderation order** for a message: blocked/suspended/unaccepted warning → flood limit → links and contacts → word list → spam → masking →
  delivery → AI scan. Links and abusive words are never delivered, so the AI only judges what the lists can't. Originals of masked and removed
  messages are kept for moderators only.
- **Word matching** collapses repeated letters on both sides ("hello" on the list matches "hellooo"); single short words (≤ 4 letters) only
  match whole words so "ass" never hits "class"; longer words also match inside compounds ("idiots", "stupidface").
- **Urdu UI** (Phase 10) covers every XAML string, validation and network messages. Texts that ViewModels build at run time
  (greetings, "3 lectures behind", some dialogs) and server error messages are still English; move them into `AppResources` the same way.
  Lecture content is in the language being learned and is not translated.
- **Language switch rebuilds the shell** instead of live-binding every label: simpler, trimming-safe, and pages reload their data anyway.
- **Reminders are created on the server**, not scheduled on the phone, so they follow plan changes, pauses and the learner's switches,
  and they also land in the inbox. Each has a dedup key (`study:{course}:{day}`, `exam:{course}:{month}:{date}`, …).
- **One HTML template** (`LectureHtml` in Shared) renders lectures in the app's WebView and in the admin preview, so what admins check is what learners see.
- **Lecture text is cleaned on save** with an allow-list sanitizer; the app also loads it in a WebView that opens real links in the browser.
- **Reading time** is counted only while the lecture is on screen and sent when you leave it or complete; the server enforces the minimum.
- **Quill** is loaded from the jsDelivr CDN; if it can't load, the editor falls back to editing HTML directly.
- **Exam time runs on the server**: the app's countdown is only a display; answers after the deadline are refused and the attempt is submitted by the
  `AttemptExpiryService` background job (every minute) or whenever the learner opens it.
- **Questions are soft-deleted without a global filter**, so old attempts keep showing the exact questions that were asked.
- **Excel** uses ClosedXML; audio recording and playback in the app use Plugin.Maui.Audio (recordings are uploaded as WAV).
- **AI never publishes**: every AI item is a Draft with Source = AI and its prompt/model recorded; Reviewer approval is still required (BR-13).
  Generating text for a Published lecture is refused — unpublish first, so learners never see unreviewed AI text.
- **AI answers are untrusted input**: lecture HTML goes through the same sanitizer as manual edits, questions through the same validation as the question editor.

## After Phase 10

All ten phases are in. Before going live: generate the migrations, run the Phase 10 checklist, the load tests and the ZAP baseline
on staging, fill in the legal page placeholders, configure Firebase, and record the sign-off in `docs/ACCEPTANCE.md`.
