namespace Encore.Api;

/// <summary>
/// Entry-point marker for WebApplicationFactory. Tests reference both the API and the
/// Worker, and both have a top-level "Program" class, so "Program" is ambiguous there.
/// </summary>
public interface IApiAssemblyMarker;
