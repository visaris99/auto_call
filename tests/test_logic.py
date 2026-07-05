from datetime import datetime, timezone, timedelta

from logic import parse_iso, is_callback_due, sort_queue, format_seconds, callback_iso, ascii_only

KST = timezone(timedelta(hours=9))
NOW = datetime(2026, 7, 5, 10, 0, tzinfo=KST)


def lead(id_, next_call_at=None):
    return {"id": id_, "nextCallAt": next_call_at}


def test_parse_iso():
    assert parse_iso(None) is None
    assert parse_iso("") is None
    dt = parse_iso("2026-07-05T14:30:00+09:00")
    assert dt.tzinfo is not None and dt.hour == 14


def test_is_callback_due():
    assert is_callback_due(lead("a", "2026-07-05T09:59:00+09:00"), NOW) is True
    assert is_callback_due(lead("b", "2026-07-05T10:01:00+09:00"), NOW) is False
    assert is_callback_due(lead("c", None), NOW) is False


def test_sort_queue_due_callbacks_first_oldest_first():
    items = [lead("a"), lead("b", "2026-07-05T09:30:00+09:00"),
             lead("c", "2026-07-05T14:00:00+09:00"), lead("d", "2026-07-05T09:00:00+09:00")]
    assert [x["id"] for x in sort_queue(items, NOW)] == ["d", "b", "a", "c"]


def test_sort_queue_keeps_server_order_for_rest():
    items = [lead("a"), lead("b"), lead("c")]
    assert [x["id"] for x in sort_queue(items, NOW)] == ["a", "b", "c"]


def test_format_seconds():
    assert format_seconds(0) == "00:00"
    assert format_seconds(75) == "01:15"
    assert format_seconds(3700) == "1:01:40"


def test_callback_iso_today_and_tomorrow():
    assert callback_iso("14:30", NOW) == "2026-07-05T14:30:00+09:00"
    assert callback_iso("09:00", NOW) == "2026-07-06T09:00:00+09:00"  # 지난 시각 → 내일


def test_callback_iso_invalid():
    assert callback_iso("25:00", NOW) is None
    assert callback_iso("abc", NOW) is None
    assert callback_iso("", NOW) is None


def test_ascii_only_strips_hangul_and_controls():
    assert ascii_only("abc123!@#") == "abc123!@#"
    assert ascii_only("pass워드123") == "pass123"
    assert ascii_only("한글만") == ""
    assert ascii_only("tab\there\n") == "tabhere"
