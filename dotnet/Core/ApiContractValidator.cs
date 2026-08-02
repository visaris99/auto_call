using System.Text.RegularExpressions;

namespace Core;

/// <summary>CRM이 경로·헤더에서 요구하는 공통 계약을 HTTP 요청 전에 검증한다.</summary>
public static partial class ApiContractValidator
{
    [GeneratedRegex(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uuid4Pattern();

    public static string RequireUuid4(string value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (!Uuid4Pattern().IsMatch(normalized))
        {
            throw new ApiException(
                "VALIDATION",
                $"{fieldName}에는 UUID v4 값이 필요합니다.",
                400);
        }
        return normalized;
    }
}
