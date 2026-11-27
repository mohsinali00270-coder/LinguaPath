using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> b)
    {
        b.ToTable("Friendships");
        b.Property(f => f.Status).HasConversion<int>();
        b.HasOne(f => f.Requester).WithMany().HasForeignKey(f => f.RequesterId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(f => f.Addressee).WithMany().HasForeignKey(f => f.AddresseeId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(f => new { f.RequesterId, f.AddresseeId }).IsUnique();
        b.HasIndex(f => new { f.AddresseeId, f.Status });
    }
}

internal sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> b)
    {
        b.ToTable("UserBlocks");
        b.HasKey(x => new { x.BlockerId, x.BlockedId });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.BlockedId);
    }
}

internal sealed class UserReportConfiguration : IEntityTypeConfiguration<UserReport>
{
    public void Configure(EntityTypeBuilder<UserReport> b)
    {
        b.ToTable("UserReports");
        b.Property(r => r.Reason).HasConversion<int>();
        b.Property(r => r.Status).HasConversion<int>();
        b.Property(r => r.Details).HasMaxLength(1000);
        b.Property(r => r.ReviewedByEmail).HasMaxLength(256);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.ReporterId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.ReportedUserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(r => new { r.Status, r.CreatedAt });
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("Conversations");
        b.Property(c => c.Type).HasConversion<int>();
        b.Property(c => c.DirectKey).HasMaxLength(80);
        b.Property(c => c.Name).HasMaxLength(80);
        b.Property(c => c.Description).HasMaxLength(500);
        b.Property(c => c.IconKey).HasMaxLength(40);
        b.HasOne(c => c.Language).WithMany().HasForeignKey(c => c.LanguageId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(c => c.DirectKey).IsUnique();
        b.HasIndex(c => new { c.IsPublic, c.IsActive });
    }
}

internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> b)
    {
        b.ToTable("ConversationMembers");
        b.Property(m => m.Role).HasConversion<int>();
        b.HasOne(m => m.Conversation).WithMany(c => c.Members).HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(m => new { m.ConversationId, m.UserId }).IsUnique();
        b.HasIndex(m => new { m.UserId, m.IsActive });
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("Messages");
        b.Property(m => m.Body).HasMaxLength(2000).IsRequired();
        b.Property(m => m.OriginalBody).HasMaxLength(2000);
        b.Property(m => m.Mentions).HasMaxLength(2000);
        b.Property(m => m.ClientId).HasMaxLength(64);
        b.Property(m => m.MessageType).HasConversion<int>();
        b.Property(m => m.ModerationStatus).HasConversion<int>();
        b.HasOne(m => m.Conversation).WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(m => new { m.ConversationId, m.Id });
        b.HasIndex(m => new { m.SenderId, m.SentAt });
        b.HasIndex(m => m.CompetitionId);
        // Retroactive AI scan (FR-M03).
        b.HasIndex(m => new { m.ModerationStatus, m.AiScannedAt });
    }
}

internal sealed class MessageRecipientStateConfiguration : IEntityTypeConfiguration<MessageRecipientState>
{
    public void Configure(EntityTypeBuilder<MessageRecipientState> b)
    {
        b.ToTable("MessageRecipientStates");
        b.HasKey(s => new { s.MessageId, s.UserId });
        b.HasOne(s => s.Message).WithMany().HasForeignKey(s => s.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(s => new { s.UserId, s.SeenAt });
    }
}

internal sealed class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> b)
    {
        b.ToTable("MessageReactions");
        b.HasKey(r => new { r.MessageId, r.UserId });
        b.Property(r => r.Emoji).HasMaxLength(16).IsRequired();
        b.HasOne<Message>().WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class AbusiveWordConfiguration : IEntityTypeConfiguration<AbusiveWord>
{
    public void Configure(EntityTypeBuilder<AbusiveWord> b)
    {
        b.ToTable("AbusiveWords");
        b.Property(w => w.Term).HasMaxLength(200).IsRequired();
        b.Property(w => w.NormalizedTerm).HasMaxLength(200).IsRequired();
        b.Property(w => w.Severity).HasConversion<int>();
        b.HasOne(w => w.Language).WithMany().HasForeignKey(w => w.LanguageId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(w => new { w.LanguageId, w.NormalizedTerm }).IsUnique();
    }
}

internal sealed class BlockedLinkPatternConfiguration : IEntityTypeConfiguration<BlockedLinkPattern>
{
    public void Configure(EntityTypeBuilder<BlockedLinkPattern> b)
    {
        b.ToTable("BlockedLinkPatterns");
        b.Property(p => p.Pattern).HasMaxLength(500).IsRequired();
        b.Property(p => p.Description).HasMaxLength(300);
    }
}

internal sealed class ModerationEventConfiguration : IEntityTypeConfiguration<ModerationEvent>
{
    public void Configure(EntityTypeBuilder<ModerationEvent> b)
    {
        b.ToTable("ModerationEvents");
        b.Property(e => e.EntityType).HasConversion<int>();
        b.Property(e => e.DetectedBy).HasConversion<int>();
        b.Property(e => e.Severity).HasConversion<int>();
        b.Property(e => e.ActionTaken).HasConversion<int>();
        b.Property(e => e.EntityId).HasMaxLength(60);
        b.Property(e => e.Text).HasMaxLength(2000);
        b.Property(e => e.Category).HasMaxLength(60);
        b.Property(e => e.MatchedTerms).HasMaxLength(500);
        b.Property(e => e.ReviewedByEmail).HasMaxLength(256);
        b.Property(e => e.ReviewNote).HasMaxLength(1000);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.ActionTaken, e.CreatedAt });
        b.HasIndex(e => new { e.UserId, e.CreatedAt });
    }
}

internal sealed class ModerationStrikeConfiguration : IEntityTypeConfiguration<ModerationStrike>
{
    public void Configure(EntityTypeBuilder<ModerationStrike> b)
    {
        b.ToTable("ModerationStrikes");
        b.Property(s => s.Severity).HasConversion<int>();
        b.Property(s => s.Reason).HasMaxLength(300).IsRequired();
        b.Property(s => s.RemovedBy).HasMaxLength(256);
        b.Property(s => s.RemovedReason).HasMaxLength(500);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => new { s.UserId, s.ExpiresAt });
    }
}
