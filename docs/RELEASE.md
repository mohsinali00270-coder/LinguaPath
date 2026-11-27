# Store release (Android and iOS)

NFR-13: Android 8.0+ (API 26) and iOS 15+ — set in `LinguaPath.Mobile.csproj`.

## Before every release

1. Bump `ApplicationDisplayVersion` (e.g. `1.1.0`, shown to users) and `ApplicationVersion` (integer build number, +1 each upload).
2. `AppConfig.ApiBaseUrl` (in `MauiProgram.cs`) points at production for Release builds.
3. Test on a real Android phone and an iPhone in **Urdu** as well as English (Profile → Edit profile → App language): layout flips
   right to left, no clipped text, screen reader reads buttons (TalkBack / VoiceOver).
4. Airplane mode: open a downloaded lecture, mark it complete, review flashcards; back online, the toast "your offline progress was saved".

## Push notifications (Firebase)

1. Firebase console → add the Android app `com.linguapath.app` and the iOS app with the same bundle id.
2. Download `google-services.json` → `src/LinguaPath.Mobile/Platforms/Android/` and `GoogleService-Info.plist` →
   `src/LinguaPath.Mobile/Platforms/iOS/`. Their presence switches on `UseFirebase` (adds `Plugin.Firebase.CloudMessaging` and the
   `FIREBASE` symbol). Don't commit them if the repo is public.
3. iOS: upload your APNs key (.p8) in Firebase → Cloud Messaging; enable Push Notifications for the App ID; set
   `aps-environment` to `production` in `Entitlements.plist` for TestFlight/App Store builds.
4. Server: `Push__FcmProjectId` and `Push__FcmServiceAccount` (service-account JSON path) in the API's secrets.
5. Check: sign in on a phone, then from the API host run a reminder (or issue a certificate) — the push arrives and tapping it
   opens the right screen. Without Firebase the app still works with the in-app inbox only.

Plugin.Firebase's API changes between major versions; if the build fails in `Services/Push.cs`, compare it with the plugin's
current README (initialisation in `UseFirebasePush`, token in `MauiPushTokenProvider`, taps in `PushTaps`).

## Android (Google Play)

```bash
# one-time: create an upload key and keep it (and its passwords) in your password manager
keytool -genkeypair -v -keystore linguapath-upload.keystore -alias linguapath -keyalg RSA -keysize 2048 -validity 10000

dotnet publish src/LinguaPath.Mobile -f net10.0-android -c Release \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=linguapath-upload.keystore -p:AndroidSigningKeyAlias=linguapath \
  -p:AndroidSigningKeyPass=env:LP_KEY_PASS -p:AndroidSigningStorePass=env:LP_STORE_PASS
# → bin/Release/net10.0-android/publish/com.linguapath.app-Signed.aab
```

Play Console:
- **App content:** privacy policy URL `https://<api-host>/privacy`; account deletion URL `https://<api-host>/delete-account`
  (Google requires a web page as well as the in-app option); target audience 13+; Data safety form (see below).
- **Content rating:** the app has user-to-user chat (moderated, no links or media) → answer the questionnaire accordingly.
- Upload the `.aab` to Internal testing first, then Closed → Production.

## iOS (App Store)

```bash
dotnet publish src/LinguaPath.Mobile -f net10.0-ios -c Release \
  -p:ArchiveOnBuild=true -p:RuntimeIdentifier=ios-arm64 \
  -p:CodesignKey="Apple Distribution: <Your Company> (<TEAMID>)" -p:CodesignProvision="LinguaPath App Store"
# → upload the .ipa with Transporter or `xcrun altool --upload-app`
```

App Store Connect:
- **Sign in with Apple** is offered because Google sign-in is (guideline 4.8) — already in the app.
- **Account deletion** in the app (guideline 5.1.1(v)) — Profile → Delete account.
- **User-generated content** (guideline 1.2): filter, report, block and moderation exist (Phase 8); mention them in review notes.
- Privacy nutrition labels: see below. Age rating 12+ / 13+ with "Unrestricted web access: No".
- Give the reviewer a demo learner account with a course already started.

## Data safety / privacy labels

| Data | Collected | Purpose | Shared |
|---|---|---|---|
| Name, email | Yes | Account | No |
| Date of birth (optional) | Yes | Age rules (BR-32) | No |
| Photos (profile, optional) | Yes | Profile | No |
| Audio (speaking practice) | Yes | App functionality; deleted after 30 days | Processed by Azure Speech |
| Messages (chat) | Yes | App functionality, moderation | No |
| App activity (progress, scores) | Yes | App functionality | No |
| Crash logs | Yes | Diagnostics | No |
| Device id, push token | Yes | Notifications, security | Google/Apple for push |

Data is encrypted in transit (HTTPS) and users can request deletion.
