"""CRM(globals.css/labels.ts/badge.tsx)에서 이식한 디자인 토큰 — 값 임의 변경 금지."""
from __future__ import annotations

COLORS = {
    "background": "#f3f1ea",   # 웜 크림
    "surface": "#ffffff",
    "surface2": "#faf8f2",
    "foreground": "#211f1a",
    "ink": "#161410",          # 제목·기본 버튼(near-black)
    "line": "#e8e3d8",
    "track": "#ebe6db",
    "hover": "#f0ebe0",
    "brand": "#ecdf4a",        # 로고 옐로
    "brand_soft": "#f6efbe",
    "gold": "#a98a1f",
    "danger": "#b3372c",
    "danger_soft": "#f7e2df",
    "success": "#1a7f4b",
    "success_soft": "#e2f0e8",
    "muted": "#8a857a",
}

# labels.ts LEAD_STATUS_LABEL과 동일
STATUS_LABEL = {
    "NEW": "신규", "ASSIGNED": "배정됨", "NOANSWER": "부재", "CALLBACK": "콜백예약",
    "INTERESTED": "가망", "CONSULT": "상담중", "WON": "가입", "REJECT": "거절",
    "DNC": "수신거부", "RECYCLE": "재활용",
}

# CALL_RESULT_LABEL 7종 + 단축키 1~7
RESULTS = [
    ("NOANSWER", "부재", "1"), ("CALLBACK", "콜백예약", "2"), ("INTERESTED", "가망", "3"),
    ("CONSULT", "상담중", "4"), ("WON", "가입", "5"), ("REJECT", "거절", "6"),
    ("DNC", "수신거부", "7"),
]

# badge.tsx statusVariant와 동일한 매핑
_VARIANT = {
    "WON": "brand", "INTERESTED": "brand", "CONSULT": "brand",
    "DNC": "danger", "REJECT": "danger",
    "ASSIGNED": "soft", "CALLBACK": "soft",
}
_VARIANT_COLORS = {
    "brand": (COLORS["brand"], COLORS["ink"]),
    "soft": (COLORS["brand_soft"], COLORS["ink"]),
    "danger": (COLORS["danger_soft"], COLORS["danger"]),
    "neutral": (COLORS["surface2"], COLORS["foreground"]),
}

FONT_FAMILY = "맑은 고딕"


def status_colors(status: str) -> tuple[str, str]:
    """상태코드 → (배경색, 글자색)."""
    return _VARIANT_COLORS[_VARIANT.get(status, "neutral")]


def font(size: int, weight: str = "normal"):
    """CTkFont 팩토리 — tk 초기화 이후에만 호출할 것."""
    import customtkinter as ctk

    return ctk.CTkFont(family=FONT_FAMILY, size=size, weight=weight)
