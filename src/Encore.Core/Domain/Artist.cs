namespace Encore.Core.Domain;

public sealed class Artist
{
    private Artist() { } // EF Core

    public Artist(Guid mbId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Artist name is required.");
        Id = Guid.CreateVersion7();
        MbId = mbId;
        Name = name.Trim();
    }

    public Guid Id { get; private set; }

    /// <summary>MusicBrainz artist id. Unique: one row per real artist.</summary>
    public Guid MbId { get; private set; }

    public string Name { get; private set; } = "";
}
