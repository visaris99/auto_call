namespace Core;

public static class UpdateCheck
{
    /// <summary>candidate가 current보다 새 버전이면 true. null/공백/파싱 불가는 false.</summary>
    public static bool IsNewer(string current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        return Version.TryParse(current, out var mine)
               && Version.TryParse(candidate.Trim(), out var theirs)
               && theirs > mine;
    }
}
