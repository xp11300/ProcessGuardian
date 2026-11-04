namespace ProcessGuardian.Services.CommandLine;

internal enum ApplicationSelectionStatus
{
    Found,
    NotFound,
    Ambiguous,
}

internal sealed record ApplicationSelection<T>(ApplicationSelectionStatus Status, T? Item, int MatchCount, bool SelectedById)
    where T : class;

/// <summary>
/// Pure selector matching. A value that parses as a GUID matches entry ids only (never names). Any other value is
/// trimmed and compared with the whole software name, case-insensitively (ordinal); there is no partial, prefix or
/// path matching. Zero matches is NotFound and two or more is Ambiguous: an entry is never guessed.
/// </summary>
internal static class ApplicationSelector
{
    internal static ApplicationSelection<T> Select<T>(IEnumerable<T> items, Func<T, Guid> getId, Func<T, string?> getName, string selector)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(getId);
        ArgumentNullException.ThrowIfNull(getName);
        ArgumentNullException.ThrowIfNull(selector);

        var trimmed = selector.Trim();
        var byId = Guid.TryParse(trimmed, out var id);
        List<T> matches;
        if (byId)
        {
            matches = items.Where(item => getId(item) == id).ToList();
        }
        else if (trimmed.Length == 0)
        {
            matches = new List<T>();
        }
        else
        {
            matches = items.Where(item => string.Equals(getName(item)?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            0 => new ApplicationSelection<T>(ApplicationSelectionStatus.NotFound, null, 0, byId),
            1 => new ApplicationSelection<T>(ApplicationSelectionStatus.Found, matches[0], 1, byId),
            _ => new ApplicationSelection<T>(ApplicationSelectionStatus.Ambiguous, null, matches.Count, byId),
        };
    }
}
