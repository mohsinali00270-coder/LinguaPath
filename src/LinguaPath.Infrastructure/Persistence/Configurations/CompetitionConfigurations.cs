using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class CompetitionConfiguration : IEntityTypeConfiguration<Competition>
{
    public void Configure(EntityTypeBuilder<Competition> b)
    {
        b.ToTable("Competitions");
        b.Property(c => c.Mode).HasConversion<int>();
        b.Property(c => c.Status).HasConversion<int>();
        b.Property(c => c.Level).HasConversion<int>();
        b.Property(c => c.ConcurrencyStamp).IsConcurrencyToken();
        b.HasOne(c => c.Language).WithMany().HasForeignKey(c => c.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => new { c.Status, c.NextEventAt });
        b.HasIndex(c => new { c.Mode, c.Status, c.LanguageId });
        b.HasIndex(c => c.TournamentId);
    }
}

internal sealed class CompetitionPlayerConfiguration : IEntityTypeConfiguration<CompetitionPlayer>
{
    public void Configure(EntityTypeBuilder<CompetitionPlayer> b)
    {
        b.ToTable("CompetitionPlayers");
        b.Property(p => p.State).HasConversion<int>();
        b.Property(p => p.Result).HasConversion<int?>();
        b.Property(p => p.BotName).HasMaxLength(60);
        b.HasOne(p => p.Competition).WithMany(c => c.Players).HasForeignKey(p => p.CompetitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(p => new { p.CompetitionId, p.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL");
        b.HasIndex(p => new { p.UserId, p.State });
    }
}

internal sealed class CompetitionQuestionConfiguration : IEntityTypeConfiguration<CompetitionQuestion>
{
    public void Configure(EntityTypeBuilder<CompetitionQuestion> b)
    {
        b.ToTable("CompetitionQuestions");
        b.HasKey(q => new { q.CompetitionId, q.SortOrder });
        b.HasOne<Competition>().WithMany(c => c.Questions).HasForeignKey(q => q.CompetitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(q => q.Question).WithMany().HasForeignKey(q => q.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompetitionAnswerConfiguration : IEntityTypeConfiguration<CompetitionAnswer>
{
    public void Configure(EntityTypeBuilder<CompetitionAnswer> b)
    {
        b.ToTable("CompetitionAnswers");
        b.Property(a => a.AnswerText).HasMaxLength(500);
        b.HasOne<Competition>().WithMany().HasForeignKey(a => a.CompetitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CompetitionPlayer>().WithMany().HasForeignKey(a => a.PlayerId).OnDelete(DeleteBehavior.NoAction);
        // One answer per player and question (a retry after a dropped connection is ignored).
        b.HasIndex(a => new { a.PlayerId, a.QuestionIndex }).IsUnique();
        b.HasIndex(a => new { a.CompetitionId, a.QuestionIndex });
    }
}

internal sealed class CompetitionRatingConfiguration : IEntityTypeConfiguration<CompetitionRating>
{
    public void Configure(EntityTypeBuilder<CompetitionRating> b)
    {
        b.ToTable("CompetitionRatings");
        b.HasKey(r => new { r.UserId, r.LanguageId });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Language>().WithMany().HasForeignKey(r => r.LanguageId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.LanguageId, r.Rating });
    }
}

internal sealed class TournamentConfiguration : IEntityTypeConfiguration<Tournament>
{
    public void Configure(EntityTypeBuilder<Tournament> b)
    {
        b.ToTable("Tournaments");
        b.Property(t => t.Title).HasMaxLength(120).IsRequired();
        b.Property(t => t.Description).HasMaxLength(1000);
        b.Property(t => t.CreatedByEmail).HasMaxLength(256);
        b.Property(t => t.Status).HasConversion<int>();
        b.Property(t => t.Level).HasConversion<int>();
        b.HasOne(t => t.Language).WithMany().HasForeignKey(t => t.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(t => new { t.Status, t.StartsAt });
    }
}

internal sealed class TournamentEntryConfiguration : IEntityTypeConfiguration<TournamentEntry>
{
    public void Configure(EntityTypeBuilder<TournamentEntry> b)
    {
        b.ToTable("TournamentEntries");
        b.Property(e => e.DisqualifiedReason).HasMaxLength(500);
        b.Property(e => e.DisqualifiedBy).HasMaxLength(256);
        b.HasOne(e => e.Tournament).WithMany(t => t.Entries).HasForeignKey(e => e.TournamentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(e => new { e.TournamentId, e.UserId }).IsUnique();
    }
}

internal sealed class LeagueConfiguration : IEntityTypeConfiguration<League>
{
    public void Configure(EntityTypeBuilder<League> b)
    {
        b.ToTable("Leagues");
        b.Property(l => l.Tier).HasConversion<int>();
        b.HasIndex(l => new { l.WeekStart, l.Tier, l.Number }).IsUnique();
    }
}

internal sealed class LeagueMemberConfiguration : IEntityTypeConfiguration<LeagueMember>
{
    public void Configure(EntityTypeBuilder<LeagueMember> b)
    {
        b.ToTable("LeagueMembers");
        b.HasKey(m => new { m.LeagueId, m.UserId });
        b.Property(m => m.Outcome).HasConversion<int?>();
        b.HasOne(m => m.League).WithMany(l => l.Members).HasForeignKey(m => m.LeagueId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(m => m.UserId);
    }
}
