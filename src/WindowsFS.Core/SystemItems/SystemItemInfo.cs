namespace WindowsFS.Core.SystemItems;

/// <summary>Explains the usual Windows role of a recognized location, not its verified creator.</summary>
public sealed record SystemItemInfo(
    string Title,
    string Purpose,
    string ManagedBy,
    string Necessity,
    string CleanupAdvice,
    string SourceUrl,
    string MatchBasis);
