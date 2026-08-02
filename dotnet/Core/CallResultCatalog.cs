namespace Core;

public static class CallResultCatalog
{
    public static readonly (string Code, string Label, string Key)[] Results =
    {
        ("NOANSWER", "부재", "1"),
        ("CALLBACK", "콜백예약", "2"),
        ("INTERESTED", "가망", "3"),
        ("CONSULT", "상담중", "4"),
        ("WON", "가입", "5"),
        ("REJECT", "거절", "6"),
        ("DNC", "수신거부", "7"),
        ("APPOINTMENT", "상담예약", "8"),
        ("HANDOFF", "영업이관", "9"),
        ("RISK", "민원위험", "0"),
    };

    public static bool IsSpecial(string code) =>
        code is "DNC" or "APPOINTMENT" or "HANDOFF" or "RISK";
}
