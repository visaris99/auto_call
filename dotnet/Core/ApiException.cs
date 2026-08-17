// API 오류 계층 — 설계서 3.1 에러 표의 error.code에 대응 (파이썬 api.py와 동일 의미론).
namespace Core;

public class ApiException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }
    public string? RequestId { get; }

    public ApiException(string code, string message, int httpStatus = 0,
        string? requestId = null) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
    }
}

/// <summary>연결 실패/타임아웃 — 재시도 대상.</summary>
public sealed class NetworkException : ApiException
{
    public NetworkException(string message = "서버에 연결할 수 없습니다.")
        : base("NETWORK", message, 0) { }
}

/// <summary>토큰 없음/만료(UNAUTHENTICATED) — 재로그인 필요.</summary>
public sealed class AuthException : ApiException
{
    public AuthException(string code, string message, int httpStatus, string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>MFA 코드 입력 필요.</summary>
public sealed class MfaRequiredException : ApiException
{
    public MfaRequiredException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>초기 비밀번호 변경 필요 — API 토큰은 발급되지 않는다.</summary>
public sealed class PasswordChangeRequiredException : ApiException
{
    public const string DialogTitle = "비밀번호 변경 필요";

    public static string BuildUserMessage(string crmBaseUrl) =>
        $"초기 비밀번호 상태입니다.\n웹 CRM({crmBaseUrl})에서 비밀번호를 변경한 뒤 다시 로그인하세요.";

    public PasswordChangeRequiredException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>야간(21~08 KST) 발신 차단.</summary>
public sealed class NightBlockedException : ApiException
{
    public NightBlockedException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>수신거부(DNC) 고객 발신 차단.</summary>
public sealed class DncBlockedException : ApiException
{
    public const string UserMessage = "수신거부 고객 — 발신 불가, 큐를 새로고침하세요";

    public DncBlockedException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>발신 장치 미등록·하트비트 만료·ADB 단절.</summary>
public sealed class DeviceNotReadyException : ApiException
{
    public const string DialogTitle = "발신 장치 준비 필요";
    public const string NextAction =
        "USB 연결과 ADB 상태를 확인한 뒤 다시 시도하세요. 계속되면 CRM 관리자에게 장치 등록 상태를 확인해 달라고 요청하세요.";

    public DeviceNotReadyException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}

/// <summary>이 PC가 다른 사용자 소유로 등록됨.</summary>
public sealed class DeviceOwnershipConflictException : ApiException
{
    public const string DialogTitle = "장치 소유권 충돌";
    public const string NextAction =
        "기존 사용자의 다이얼러를 종료한 뒤 CRM 관리자에게 장치 해제를 요청하세요.";

    public DeviceOwnershipConflictException(string code, string message, int httpStatus,
        string? requestId = null)
        : base(code, message, httpStatus, requestId) { }
}
