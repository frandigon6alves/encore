using Encore.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Encore.Core.Data;

public sealed class EncoreDbContext(DbContextOptions<EncoreDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<AlbumTag> AlbumTags => Set<AlbumTag>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Ids are created in the domain (Guid v7), not by EF. Without ValueGeneratedNever, EF
        // treats a new Track reached through Album.Tracks as an *existing* row (key already set)
        // and issues an UPDATE that hits 0 rows -> DbUpdateConcurrencyException.
        b.Entity<Artist>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Album>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Track>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<OutboxMessage>().Property(x => x.Id).ValueGeneratedNever();

        b.Entity<Artist>(e =>
        {
            e.HasIndex(x => x.MbId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(300);
        });

        b.Entity<Album>(e =>
        {
            e.HasIndex(x => x.MbId).IsUnique();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.Version).IsRowVersion(); // Npgsql maps uint row versions to xmin
            e.HasOne(x => x.Artist).WithMany().HasForeignKey(x => x.ArtistId);
            e.HasMany(x => x.Tracks).WithOne().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            // Recommender filters on status; tiny table now, but documents intent.
            e.HasIndex(x => x.Status);
        });

        b.Entity<Track>(e =>
        {
            e.HasIndex(x => new { x.AlbumId, x.Position }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(500);
        });

        b.Entity<AlbumTag>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(64);
            e.HasIndex(x => x.Tag); // "albums by tag" and the recommender join
        });

        b.Entity<LibraryEntry>(e =>
        {
            e.HasKey(x => new { x.UserId, x.AlbumId });
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Review).HasMaxLength(LibraryEntry.MaxReviewLength);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId);
            // Keyset pagination for GET /api/v2/library: WHERE user_id = @u AND (added_at, album_id) < (@a, @id)
            e.HasIndex(x => new { x.UserId, x.AddedAt, x.AlbumId }).IsDescending(false, true, true);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.TraceParent).HasMaxLength(128);
            // Partial index: the publisher only ever scans unprocessed rows.
            e.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL");
        });

        b.Entity<ProcessedMessage>(e =>
        {
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(64);
        });
    }
}
