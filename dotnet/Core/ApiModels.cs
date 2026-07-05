// 설계서 3장 API 계약의 응답 모델 (JSON은 camelCase — JsonSerializerDefaults.Web).
namespace Core;

public sealed record UserInfo(
    string Id,
    string LoginId,
    string Name,
    string? OrgName,
    string[] Roles,
    bool MustChangePassword);

public sealed record LoginResponse(string Token, string? ExpiresAt, UserInfo User);

public sealed record LeadItem(
    string Id,
    string? Name,
    string PhoneMasked,
    string Status,
    string? NextCallAt,
    string? Memo,
    string? UpdatedAt);

public sealed record QueueResponse(string? ServerTime, List<LeadItem> Items);

public sealed record CallLead(string Id, string Status, string? NextCallAt);

public sealed record CallResponse(bool Ok, CallLead Lead);

public sealed record VersionInfo(string? MinVersion, string? LatestVersion, string? DownloadUrl);
