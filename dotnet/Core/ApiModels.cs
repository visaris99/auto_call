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
