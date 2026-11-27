# Acceptance run against the SRS

How each acceptance criterion in SRS 10.3 and 11.9 is checked. "Automated" = an integration test in
`tests/LinguaPath.IntegrationTests` (run `dotnet test`); "Manual" = a step to do on a phone or in the admin panel before sign-off.

## SRS 10.3 Key scenarios

| # | Criterion | How it is checked |
|---|---|---|
| 1 | New learner registers, picks Spanish A1 → B1, Travel, 6 months, 20 min/day and sees a paced roadmap within 3 s | Automated: `AuthFlowTests.Register_verify_then_profile_works`, `OnboardingTests.Learner_enrols_and_gets_a_paced_roadmap`. Timing: `tests/load/api-smoke.js` (p95 < 500 ms on `today` and `roadmap`). Manual: onboarding on a phone. |
| 2 | Daily quiz only from studied content, with explanations | Automated: `AssessmentTests.Daily_quiz_unlocks_after_a_lecture_gives_instant_feedback_and_counts_once` |
| 3 | Late monthly exam auto-closed and graded; low-confidence writing goes to the grading queue | Automated: `AssessmentTests.Monthly_exam_unlocks_hides_answers_waits_for_grading_then_counts`, `AiTests.Writing_is_pre_graded_confident_answers_are_applied_the_rest_wait_for_a_reviewer` |
| 4 | BR-06 → certificate PDF and push; QR opens "Valid" | Automated: `ProgressTests.Meeting_br06_issues_a_numbered_certificate_with_pdf_points_email_and_verification` (PDF, number, verification). Phase 10: the certificate now also lands in the inbox and is pushed through FCM. Manual: scan the QR on a phone; receive the push with Firebase configured (docs/RELEASE.md). |
| 5 | AI generates 10 questions from a published lecture, each with its source sentence, Draft until approved | Automated: `AiTests.New_language_needs_approval_then_roadmap_review_then_writes_lectures_and_questions_in_draft`, `AiTests.Questions_need_lecture_text_and_posts_are_scheduled_one_a_day` |
| 6 | New language "Swahili": AI can't start until a Super Admin approves the cost; lectures hidden until reviewed | Automated: same AI test (approval gate, Draft state), `ContentTests.Draft_lectures_stay_out_of_roadmaps` |
| 7 | Tap a word → meaning and audio; recorded sentence → per-word pronunciation score | Automated: `PracticeTests.Speaking_takes_a_phone_wav_returns_word_scores_and_rejects_other_formats`. Manual: tap-a-word and read-aloud on a phone (device voices). |
| 8 | Translation for any supported pair, daily limit with a clear message, save to My words | Automated: `PracticeTests.Translation_detects_the_language_keeps_history_uses_the_cache_and_enforces_the_daily_limit`, `PracticeTests.Words_are_saved_once_reviewed_with_sm2_and_deleted` |
| 9 | Mark override in the audit log with old/new score, reason and user | Manual: Admin → Grading queue → override a mark with a reason, then Admin → Audit log shows old and new score, reason and admin. |

## SRS 11.9 Social acceptance

| # | Criterion | How it is checked |
|---|---|---|
| 1 | Lecture 3 days early = 25 points, same total from any course | Automated: `ProgressTests.Completing_a_lecture_gives_points_once_starts_the_streak_and_earns_the_first_badge` and the daily-cap test (study-ahead bonus capped at +15) |
| 2 | Delete for me / for everyone; "This message was deleted" within 1 s; rows kept with `IsActive = 0` | Automated: `SocialChatTests.Direct_chat_delivery_seen_and_deletes`. Timing: `tests/load/chat.js` (p95 round trip < 1 s). |
| 3 | `check www.example.com` or a phone number rejected in direct and group chats | Automated: `SocialChatTests.Links_are_rejected_and_five_attempts_add_a_strike_warning` |
| 4 | Three abusive messages in 90 days → logged out everywhere, can't sign in or re-register | Automated: `SocialChatTests.Strike_ladder_suspends_then_blocks`, `SocialChatTests.Severe_words_block_at_once` |
| 5 | 1 v 1 challenge: same 10 questions, faster correct answer scores higher, winner gets 30 points | Automated: `CompetitionTests.Live_challenge_same_questions_faster_wins_and_points` |
| 6 | Owner creates a group, adds 5, promotes an admin who starts a group battle with a live leaderboard | Automated: `SocialChatTests.Groups_roles_announcement_mode_and_owner_transfer`, `CompetitionTests.Group_battle_with_live_leaderboard_and_top_three_bonus` |

## Phase 10 requirements

| Requirement | How it is checked |
|---|---|
| FR-L110 reminders (study time, daily quiz ready, exam in 3 days, streak at risk, certificate, new post) | Automated: `ReleaseTests.Study_reminder_lands_in_the_inbox_once_…`, `ReleaseTests.No_study_reminder_outside_the_window_…`, `ReleaseTests.Exam_soon_and_new_post_reminders_…`; unit: `ReleaseUnitTests` (windows, quiet hours, streak, exam, switches) |
| FR-L112 inbox | Automated: the first `ReleaseTests` test (list, unread count, read, read all, delete). Manual: 🔔 on Home, tap opens the right screen. |
| FR-A100 analytics reports with Excel and PDF | Automated: `ReleaseTests.Every_analytics_report_runs_and_exports_to_excel_and_pdf`. Manual: Admin → Analytics reports, open both files. |
| BR-32 age 13+, under-18 friends only | Automated: `ReleaseTests.Under_13_cannot_register_and_under_18_chats_with_friends_only`; unit: `ReleaseUnitTests.Ages_*` |
| NFR-08 retention | Automated: `ReleaseTests.Maintenance_deletes_old_practice_recordings_…` |
| NFR-09 offline | Manual: airplane-mode steps in docs/RELEASE.md (offline lectures, queued completions and reviews). |
| NFR-10 English / Urdu, RTL | Unit: `LocalizationAndRoutingTests` (every key has Urdu, RTL). Manual: switch language in Edit profile. |
| NFR-12 accessibility | Manual: TalkBack and VoiceOver pass (icon buttons have descriptions, titles are headings, decorative images hidden). |
| NFR-01/02 performance | `tests/load/*.js` against staging (thresholds fail the run). |
| NFR-05/06 security, rate limits | `ReleaseTests.Health_endpoints_security_headers_…`, Phase 1 auth tests, docs/SECURITY.md checklist and ZAP baseline. |
| NFR-07 backups | Monthly restore drill (docs/OPERATIONS.md). |
| NFR-14 monitoring | `/health/*`, Seq, crash reports (`ReleaseTests` posts one). |

## Sign-off

Record the date, build number, `dotnet test` result, k6 summary and who did the manual checks:

| Date | Build | Tests | Load | Manual checks by |
|---|---|---|---|---|
| | | | | |
