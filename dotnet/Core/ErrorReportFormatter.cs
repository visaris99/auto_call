namespace Core;

/// <summary>관리자에게 전달할 오류 보고문. 전달받지 않은 토큰·전화번호는 포함하지 않는다.</summary>
public static class ErrorReportFormatter
{
    public static string Build(
        string appVersion,
        string code,
        string title,
        string message,
        string? user,
        string? requestId,
        DateTimeOffset occurredAt)
    {
        string requestIdLine = string.IsNullOrWhiteSpace(requestId)
            ? ""
            : $"요청 ID: {requestId!.Trim()}\n";
        return "[Milestone Dialer 오류 보고]\n"
               + $"시각: {occurredAt:yyyy-MM-dd HH:mm:ss}\n"
               + $"버전: {appVersion}\n"
               + $"코드: {(string.IsNullOrWhiteSpace(code) ? "(없음)" : code)}\n"
               + requestIdLine
               + $"구분: {title}\n"
               + $"내용: {message}\n"
               + $"사용자: {user ?? "(미로그인)"}";
    }
}
