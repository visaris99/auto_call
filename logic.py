"""GUI와 무관한 순수 헬퍼 — 시간 파싱/큐 정렬/포맷."""
from __future__ import annotations

from datetime import datetime, timedelta


def parse_iso(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError:
        return None


def is_callback_due(item: dict, now: datetime) -> bool:
    dt = parse_iso(item.get("nextCallAt"))
    return dt is not None and dt <= now


def sort_queue(items: list[dict], now: datetime) -> list[dict]:
    """콜백 도래분을 오래된 순으로 맨 위에, 나머지는 서버 순서 유지."""
    due = [x for x in items if is_callback_due(x, now)]
    due.sort(key=lambda x: parse_iso(x["nextCallAt"]))
    rest = [x for x in items if not is_callback_due(x, now)]
    return due + rest


def format_seconds(total: int) -> str:
    h, rem = divmod(max(0, int(total)), 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def callback_iso(hhmm: str, now: datetime) -> str | None:
    """'14:30' → 오늘(지났으면 내일)의 aware ISO 문자열. 형식 오류는 None."""
    try:
        hour_s, minute_s = hhmm.strip().split(":")
        hour, minute = int(hour_s), int(minute_s)
    except ValueError:
        return None
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        return None
    target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
    if target <= now:
        target += timedelta(days=1)
    return target.isoformat()
