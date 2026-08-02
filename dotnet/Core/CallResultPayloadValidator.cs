using System.Globalization;

namespace Core;

/// <summary>CRM call-result.input 스키마와 같은 결과 저장 제약을 선제 검증한다.</summary>
public static class CallResultPayloadValidator
{
    public const int MemoMaxLength = 2000;
    public const int TalkSecondsMax = 86_400;

    public static void Validate(
        string resultCode,
        int talkSeconds,
        string? memo,
        string? callbackAt,
        string? appointmentAt)
    {
        if (!CallResultCatalog.Results.Any(result => result.Code == resultCode))
            Invalid("지원하지 않는 상담 결과 코드입니다.");
        if (talkSeconds is < 0 or > TalkSecondsMax)
            Invalid($"통화 시간은 0~{TalkSecondsMax}초 사이여야 합니다.");
        if (memo?.Length > MemoMaxLength)
            Invalid($"통화 메모는 {MemoMaxLength}자 이하여야 합니다.");

        ValidateConditionalDate(resultCode, "CALLBACK", "callbackAt", callbackAt);
        ValidateConditionalDate(resultCode, "APPOINTMENT", "appointmentAt", appointmentAt);
    }

    private static void ValidateConditionalDate(
        string resultCode,
        string targetCode,
        string fieldName,
        string? value)
    {
        bool supplied = !string.IsNullOrEmpty(value);
        if (resultCode == targetCode && !supplied)
            Invalid($"{targetCode} 결과에는 {fieldName}이 필요합니다.");
        if (resultCode != targetCode && supplied)
            Invalid($"{fieldName}은 {targetCode} 결과일 때만 입력할 수 있습니다.");
        if (supplied && !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out _))
            Invalid($"{fieldName} 형식을 확인하세요.");
    }

    private static void Invalid(string message) =>
        throw new ApiException("VALIDATION", message, 400);
}
