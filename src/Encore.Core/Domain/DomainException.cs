namespace Encore.Core.Domain;

/// <summary>A business rule was broken. The API maps it to 400 Bad Request.</summary>
public sealed class DomainException(string message) : Exception(message);
