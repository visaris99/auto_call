// GUI와 무관한 순수 헬퍼 — 시간 파싱/큐 정렬/포맷 (파이썬 logic.py와 동일 의미론).
using System.Globalization;
using System.Text;

namespace Core;

public static class QueueLogic
{
    public static DateTimeOffset? ParseIso(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;
    }

    public static bool IsCallbackDue(LeadItem item, DateTimeOffset now)
    {
        var dt = ParseIso(item.NextCallAt);
        return dt != null && dt <= now;
    }

    /// <summary>콜백 도래분을 오래된 순으로 맨 위에, 나머지는 서버 순서 유지.</summary>
    public static List<LeadItem> SortQueue(IEnumerable<LeadItem> items, DateTimeOffset now)
    {
        var list = items.ToList();
        var due = list.Where(x => IsCallbackDue(x, now))
                      .OrderBy(x => ParseIso(x.NextCallAt))
                      .ToList();
        var rest = list.Where(x => !IsCallbackDue(x, now));
        due.AddRange(rest);
        return due;
    }

    public static string FormatSeconds(int total)
    {
        total = Math.Max(0, total);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }

    /// <summary>'14:30' → 오늘(지났으면 내일)의 ISO 문자열. 형식 오류는 null.</summary>
    public static string? CallbackIso(string hhmm, DateTimeOffset now)
    {
        var parts = hhmm.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int hour)
            || !int.TryParse(parts[1], out int minute))
            return null;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return null;
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        if (target <= now)
            target = target.AddDays(1);
        return target.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>출력 가능한 ASCII만 남긴다 — 비밀번호/MFA 필드의 한글 IME 입력 차단용.</summary>
    public static string AsciiOnly(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (ch >= 0x20 && ch < 0x7F)
                sb.Append(ch);
        return sb.ToString();
    }
}
