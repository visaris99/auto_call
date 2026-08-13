// 오류 보고 체계 — 상담원이 화면의 코드를 그대로 관리자에게 전달하면
// 관리자가 원인 범주를 즉시 파악할 수 있게 하는 구조화 보고.
// 원칙(PRODUCT.md): 원인과 다음 행동을 분명한 한국어로 함께 안내한다.
using System.Text;

namespace Core;

/// <summary>사용자 노출·관리자 전달용 구조화 오류 보고.</summary>
public sealed record ErrorReport
{
    /// <summary>말로 전달 가능한 짧은 코드(예: "CRM-01").</summary>
    public required string Code { get; init; }

    /// <summary>다이얼로그 제목(짧은 명사구).</summary>
    public required string Title { get; init; }

    /// <summary>원인 — 한국어 한두 문장.</summary>
    public required string Cause { get; init; }

    /// <summary>상담원이 지금 할 행동.</summary>
    public required string NextAction { get; init; }

    /// <summary>관리자 전달용 상세(키-값). 표준 키: 세부코드/HTTP/엔드포인트/장치/원문.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>관리자에게 붙여넣을 보고 텍스트(코드·시각·버전·상세 포함).</summary>
    public string ToReportText(string appVersion, string? account, DateTimeOffset nowKst)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Milestone Dialer 오류 보고] {Code}");
        sb.AppendLine($"시각: {nowKst:yyyy-MM-dd HH:mm:ss} (KST)");
        sb.AppendLine($"버전: v{appVersion}");
        if (!string.IsNullOrEmpty(account)) sb.AppendLine($"계정: {account}");
        sb.AppendLine($"제목: {Title}");
        sb.AppendLine($"원인: {Cause}");
        foreach (var (key, value) in Details)
            if (!string.IsNullOrEmpty(value)) sb.AppendLine($"{key}: {value}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>예외 → ErrorReport 매핑 카탈로그. 코드는 범주-번호(전화로 불러주기 쉬운 형태).</summary>
public static class ErrorCatalog
{
    private static KeyValuePair<string, string> D(string key, string? value) =>
        new(key, value ?? "");

    /// <summary>CRM API 예외 → 보고. 세부코드·HTTP·엔드포인트를 상세에 보존한다.</summary>
    public static ErrorReport FromApi(ApiException ex)
    {
        var details = new List<KeyValuePair<string, string>>
        {
            D("세부코드", ex.Code),
            D("HTTP", ex.HttpStatus > 0 ? ex.HttpStatus.ToString() : ""),
            D("엔드포인트", ex.Endpoint),
            D("원문", ex.Message),
        };

        return ex switch
        {
            NetworkException => new ErrorReport
            {
                Code = "CRM-01",
                Title = "서버 연결 실패",
                Cause = "CRM 서버에 연결하지 못했습니다. 사무실 인터넷 또는 서버 문제일 수 있습니다.",
                NextAction = "네트워크 연결을 확인하고 다시 시도하세요. 반복되면 관리자에게 아래 코드를 전달하세요.",
                Details = details,
            },
            AuthException => new ErrorReport
            {
                Code = "CRM-02",
                Title = "세션 만료",
                Cause = "로그인 세션이 만료되었거나 다른 곳에서 로그아웃되었습니다.",
                NextAction = "다시 로그인하세요. 반복되면 관리자에게 문의하세요.",
                Details = details,
            },
            NightBlockedException => new ErrorReport
            {
                Code = "CRM-06",
                Title = "야간 발신 제한",
                Cause = ex.Message,
                NextAction = "발신 가능 시간(08시 이후)에 다시 시도하세요.",
                Details = details,
            },
            DncBlockedException => new ErrorReport
            {
                Code = "CRM-07",
                Title = "수신거부 고객",
                Cause = "수신거부(DNC) 고객으로 발신이 차단되었습니다.",
                NextAction = "이 고객에게는 발신할 수 없습니다. 큐를 새로고침하세요.",
                Details = details,
            },
            _ when ex.HttpStatus >= 500 || ex.Code == "INTERNAL" => new ErrorReport
            {
                Code = "CRM-04",
                Title = "CRM 서버 오류",
                Cause = $"CRM 서버가 오류를 반환했습니다: {ex.Message}",
                NextAction = "잠시 후 다시 시도하세요. 반복되면 관리자에게 아래 코드를 전달하세요.",
                Details = details,
            },
            _ when ex.Code is "INVALID_RESPONSE" or "QUEUE_TOO_LARGE" => new ErrorReport
            {
                Code = "CRM-05",
                Title = "CRM 응답 형식 오류",
                Cause = ex.Message,
                NextAction = "관리자에게 아래 코드를 전달하세요. CRM 서버 점검이 필요할 수 있습니다.",
                Details = details,
            },
            _ => new ErrorReport
            {
                Code = "CRM-03",
                Title = "요청 거부",
                Cause = ex.Message,
                NextAction = "입력 내용을 확인한 뒤 다시 시도하세요. 반복되면 관리자에게 아래 코드를 전달하세요.",
                Details = details,
            },
        };
    }

    /// <summary>ADB(안드로이드 장치) 오류 보고.</summary>
    public static ErrorReport Adb(string subCode, string cause, string nextAction, string? serial)
    {
        return new ErrorReport
        {
            Code = subCode,
            Title = "휴대폰(ADB) 오류",
            Cause = cause,
            NextAction = nextAction,
            Details = new List<KeyValuePair<string, string>>
            {
                D("장치", serial),
            },
        };
    }

    public const string AdbNoDevice = "ADB-01";       // 장치 없음/미선택
    public const string AdbDialFailed = "ADB-03";     // 발신 명령 실패
    public const string AdbHangupFailed = "ADB-04";   // 종료 명령 실패
    public const string AdbStateUnknown = "ADB-05";   // 통화 상태 확인 실패

    /// <summary>업데이트 오류 보고 — UpdateException.Code를 세부코드로 보존.</summary>
    public static ErrorReport Update(string subCode, string cause, string nextAction,
        string? detailCode = null, string? targetVersion = null)
    {
        return new ErrorReport
        {
            Code = subCode,
            Title = "업데이트 오류",
            Cause = cause,
            NextAction = nextAction,
            Details = new List<KeyValuePair<string, string>>
            {
                D("세부코드", detailCode),
                D("대상버전", targetVersion),
            },
        };
    }

    public const string UpdateCheckFailed = "UPD-01";   // 확인 실패(네트워크/서버)
    public const string UpdateNotConfigured = "UPD-02"; // 설치파일 미등록 등 구성 문제
    public const string UpdateInstallFailed = "UPD-03"; // 검증·설치 실패

    /// <summary>앱 내부 오류 보고.</summary>
    public static ErrorReport App(string subCode, string cause, string nextAction,
        string? exceptionType = null, string? raw = null)
    {
        return new ErrorReport
        {
            Code = subCode,
            Title = "앱 오류",
            Cause = cause,
            NextAction = nextAction,
            Details = new List<KeyValuePair<string, string>>
            {
                D("예외", exceptionType),
                D("원문", raw),
            },
        };
    }

    public const string AppUnhandled = "APP-01";  // 미처리 예외
    public const string AppClipboard = "APP-02";  // 클립보드 접근 실패
}
