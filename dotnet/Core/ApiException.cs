// API 오류 계층 — 설계서 3.1 에러 표의 error.code에 대응 (파이썬 api.py와 동일 의미론).
namespace Core;

public class ApiException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }

    public ApiException(string code, string message, int httpStatus = 0) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
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
    public AuthException(string code, string message, int httpStatus)
        : base(code, message, httpStatus) { }
}

/// <summary>MFA 코드 입력 필요.</summary>
public sealed class MfaRequiredException : ApiException
{
    public MfaRequiredException(string code, string message, int httpStatus)
        : base(code, message, httpStatus) { }
}

/// <summary>야간(21~08 KST) 발신 차단.</summary>
public sealed class NightBlockedException : ApiException
{
    public NightBlockedException(string code, string message, int httpStatus)
        : base(code, message, httpStatus) { }
}
