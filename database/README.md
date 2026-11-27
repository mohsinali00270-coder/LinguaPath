# Database scripts

Generate the Phase 1 script from the EF Core model (it is not checked in, so it always matches the code):

```powershell
dotnet tool install --global dotnet-ef
dotnet ef migrations add Phase1_Initial -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o Persistence/Migrations
dotnet ef migrations script -p src/LinguaPath.Infrastructure -s src/LinguaPath.Api -o database/Phase1_Initial.sql --idempotent
```

Run `Phase1_Initial.sql` in SSMS against an empty `LinguaPath` database. Seed data (roles, Super Admin, languages, settings)
is inserted by the API or admin panel on first start-up.

Tables created in Phase 2: Courses, Modules, Lectures, LanguageRequests, LearnerCourses, LearnerRoadmapItems.

Tables created in Phase 1: AspNetUsers (extended), AspNetRoles, AspNetUserRoles, AspNetUserClaims, AspNetUserLogins,
AspNetUserTokens, AspNetRoleClaims, RefreshTokens, UserDevices, EmailOtps, BlockedIdentities, Languages, SystemSettings, AuditLogs.
