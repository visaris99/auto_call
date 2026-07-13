// UI 공용 헬퍼 — CRM 상태 라벨/색 매핑(ui/theme.py·badge.tsx와 동일), 결과 10종, 리스트 행 모델.
using System.Windows.Media;
using Core;

namespace MilestoneDialer;

public static class Ui
{
    public const string Version = "2.4.4";

    private static readonly Dictionary<string, Brush> Cache = new();

    public static Brush Brush(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["NEW"] = "신규",
        ["ASSIGNED"] = "배정됨",
        ["NOANSWER"] = "부재",
        ["CALLBACK"] = "콜백예약",
        ["INTERESTED"] = "가망",
        ["CONSULT"] = "상담중",
        ["WON"] = "가입",
        ["REJECT"] = "거절",
        ["DNC"] = "수신거부",
        ["RECYCLE"] = "재활용",
        ["APPOINTMENT"] = "예약",
        ["HANDOFF"] = "이관",
        ["RISK"] = "리스크",
    };

    public static string LabelFor(string status) => Labels.GetValueOrDefault(status, status);

    /// <summary>badge.tsx statusVariant와 동일한 매핑 → (배경, 글자) 브러시.</summary>
    public static (Brush Bg, Brush Fg) StatusColors(string status) => status switch
    {
        "WON" or "INTERESTED" or "CONSULT" => (Brush("#ECDF4A"), Brush("#161410")),
        "DNC" or "REJECT" => (Brush("#F7E2DF"), Brush("#B3372C")),
        "ASSIGNED" or "CALLBACK" or "APPOINTMENT" => (Brush("#F6EFBE"), Brush("#161410")),
        "HANDOFF" => (Brush("#E7EEF8"), Brush("#244E86")),
        "RISK" => (Brush("#F7E2DF"), Brush("#B3372C")),
        _ => (Brush("#FAF8F2"), Brush("#211F1A")),
    };

    /// <summary>CRM 콜 결과 10종 + 단축키 1~9/0.</summary>
    public static readonly (string Code, string Label, string Key)[] Results =
    {
        ("NOANSWER", "부재", "1"), ("CALLBACK", "콜백예약", "2"), ("INTERESTED", "가망", "3"),
        ("CONSULT", "상담중", "4"), ("WON", "가입", "5"), ("REJECT", "거절", "6"),
        ("DNC", "수신거부", "7"), ("APPOINTMENT", "예약", "8"), ("HANDOFF", "이관", "9"),
        ("RISK", "리스크", "0"),
    };
}

/// <summary>큐 ListBox 한 행 — 표시에 필요한 값을 미리 계산해 바인딩.</summary>
public sealed class LeadRow
{
    public LeadItem Item { get; }
    public bool Due { get; }
    public string Name { get; }
    public string StatusLabel { get; }
    public Brush BadgeBg { get; }
    public Brush BadgeFg { get; }
    public Brush RowBrush { get; }
    public string TimeText { get; }

    public LeadRow(LeadItem item, DateTimeOffset now)
    {
        Item = item;
        Due = QueueLogic.IsCallbackDue(item, now);
        Name = string.IsNullOrEmpty(item.Name) ? "(이름없음)" : item.Name!;
        StatusLabel = Ui.LabelFor(item.Status);
        (BadgeBg, BadgeFg) = Ui.StatusColors(item.Status);
        RowBrush = Due ? Ui.Brush("#F6EFBE") : Ui.Brush("#FFFFFF");
        var dt = QueueLogic.ParseIso(item.NextCallAt);
        TimeText = dt?.ToLocalTime().ToString("HH:mm") ?? "";
    }
}
