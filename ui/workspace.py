"""콜 워크스페이스 — 좌측 큐, 우측 고객 카드·통화 컨트롤·결과 기록."""
from __future__ import annotations

import time
import uuid
from datetime import datetime
from tkinter import messagebox

import customtkinter as ctk

import adb
from api import ApiError, AuthError, NetworkError, NightBlocked
from logic import callback_iso, format_seconds, is_callback_due, parse_iso, sort_queue
from ui.theme import COLORS, RESULTS, font
from ui.widgets import Badge, ResultSelector, StatusDot, Toast, run_bg

QUEUE_POLL_MS = 60_000
ADB_POLL_MS = 5_000
FLUSH_POLL_MS = 30_000
TICK_MS = 1_000


def now_local() -> datetime:
    return datetime.now().astimezone()


class WorkspaceFrame(ctk.CTkFrame):
    def __init__(self, master, client, config, pending, on_auth_lost):
        super().__init__(master, fg_color=COLORS["background"])
        self.client = client
        self.config_data = config
        self.pending = pending
        self.on_auth_lost = on_auth_lost

        self.leads: list[dict] = []
        self.current: dict | None = None
        self.call_started: float | None = None  # time.monotonic()
        self.talk_seconds = 0
        self.today_dials = 0
        self.today_won = 0
        self._notified_callbacks: set[str] = set()
        self._destroyed = False

        self._build()
        self._bind_keys()
        self._update_banner()
        self.refresh_queue()
        self.after(TICK_MS, self._tick)
        self.after(ADB_POLL_MS, self._poll_adb)
        self.after(FLUSH_POLL_MS, self._poll_flush)
        self.after(QUEUE_POLL_MS, self._poll_queue)

    # ---------- 레이아웃 ----------

    def _build(self):
        self.grid_columnconfigure(1, weight=1)
        self.grid_rowconfigure(1, weight=1)

        # 상단 상태바
        bar = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=0,
                           border_width=1, border_color=COLORS["line"], height=52)
        bar.grid(row=0, column=0, columnspan=2, sticky="ew")
        user = self.client.user or {}
        ctk.CTkLabel(bar, text=f"🏢 {user.get('orgName', '')} · {user.get('name', '')}",
                     font=font(13, "bold"), text_color=COLORS["ink"]).pack(side="left", padx=16)
        self.today_label = ctk.CTkLabel(bar, text="오늘: 발신 0 · 가입 0",
                                        font=font(12), text_color=COLORS["gold"])
        self.today_label.pack(side="left", padx=12)
        self.crm_dot = StatusDot(bar, "CRM")
        self.crm_dot.pack(side="right", padx=(4, 16))
        self.adb_dot = StatusDot(bar, "ADB")
        self.adb_dot.pack(side="right", padx=4)
        self.banner = ctk.CTkLabel(bar, text="", font=font(11), text_color=COLORS["danger"])
        self.banner.pack(side="right", padx=12)

        # 좌측 큐
        left = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=12,
                            border_width=1, border_color=COLORS["line"], width=280)
        left.grid(row=1, column=0, sticky="nsw", padx=(12, 6), pady=12)
        left.grid_propagate(False)
        head = ctk.CTkFrame(left, fg_color="transparent")
        head.pack(fill="x", padx=12, pady=(12, 4))
        ctk.CTkLabel(head, text="오늘의 콜 큐", font=font(13, "bold"),
                     text_color=COLORS["ink"]).pack(side="left")
        ctk.CTkButton(head, text="↻", width=28, height=24, font=font(12),
                      fg_color=COLORS["surface2"], text_color=COLORS["ink"],
                      hover_color=COLORS["hover"],
                      command=self.refresh_queue).pack(side="right")
        self.queue_box = ctk.CTkScrollableFrame(left, fg_color="transparent")
        self.queue_box.pack(fill="both", expand=True, padx=6, pady=(0, 8))

        # 우측 상세
        right = ctk.CTkFrame(self, fg_color="transparent")
        right.grid(row=1, column=1, sticky="nsew", padx=(6, 12), pady=12)
        right.grid_columnconfigure(0, weight=1)

        card = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                            border_width=1, border_color=COLORS["line"])
        card.grid(row=0, column=0, sticky="ew")
        top = ctk.CTkFrame(card, fg_color="transparent")
        top.pack(fill="x", padx=20, pady=(18, 0))
        self.name_label = ctk.CTkLabel(top, text="-", font=font(24, "bold"),
                                       text_color=COLORS["ink"])
        self.name_label.pack(side="left")
        self.badge_slot = ctk.CTkFrame(top, fg_color="transparent")
        self.badge_slot.pack(side="left", padx=10)
        self.phone_label = ctk.CTkLabel(card, text="📱 -", font=font(17),
                                        text_color=COLORS["gold"])
        self.phone_label.pack(anchor="w", padx=20, pady=(6, 0))
        memo_row = ctk.CTkFrame(card, fg_color="transparent")
        memo_row.pack(fill="x", padx=20, pady=(8, 16))
        ctk.CTkLabel(memo_row, text="리드 메모", font=font(11),
                     text_color=COLORS["muted"]).pack(side="left")
        self.lead_memo_entry = ctk.CTkEntry(memo_row, font=font(12), height=30,
                                            placeholder_text="영업 메모 (칸반에 표시)")
        self.lead_memo_entry.pack(side="left", fill="x", expand=True, padx=8)
        ctk.CTkButton(memo_row, text="메모 저장", width=76, height=30, font=font(11),
                      fg_color=COLORS["surface2"], text_color=COLORS["ink"],
                      hover_color=COLORS["hover"],
                      command=self.save_lead_memo).pack(side="left")

        # 통화 컨트롤
        controls = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                                border_width=1, border_color=COLORS["line"])
        controls.grid(row=1, column=0, sticky="ew", pady=(10, 0))
        inner = ctk.CTkFrame(controls, fg_color="transparent")
        inner.pack(pady=14)
        self.dial_btn = ctk.CTkButton(inner, text="📞 발신 (F1)", width=170, height=52,
                                      font=font(15, "bold"), corner_radius=10,
                                      fg_color=COLORS["success"], hover_color="#14653c",
                                      command=self.dial)
        self.dial_btn.grid(row=0, column=0, padx=8)
        self.hangup_btn = ctk.CTkButton(inner, text="⏹ 종료 (F2)", width=170, height=52,
                                        font=font(15, "bold"), corner_radius=10,
                                        fg_color=COLORS["danger"], hover_color="#8f2c23",
                                        state="disabled", command=self.hangup)
        self.hangup_btn.grid(row=0, column=1, padx=8)
        self.timer_label = ctk.CTkLabel(inner, text="00:00", font=font(20, "bold"),
                                        text_color=COLORS["ink"], width=90)
        self.timer_label.grid(row=0, column=2, padx=12)

        # 결과 기록
        result_card = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                                   border_width=1, border_color=COLORS["line"])
        result_card.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        ctk.CTkLabel(result_card, text="상담 결과 (숫자키 1~7)", font=font(12, "bold"),
                     text_color=COLORS["ink"]).pack(anchor="w", padx=20, pady=(14, 4))
        self.result_selector = ResultSelector(result_card, on_change=self._on_result_change)
        self.result_selector.pack(padx=14, pady=2)
        form = ctk.CTkFrame(result_card, fg_color="transparent")
        form.pack(fill="x", padx=20, pady=(6, 16))
        ctk.CTkLabel(form, text="메모", font=font(11),
                     text_color=COLORS["muted"]).pack(side="left")
        self.memo_entry = ctk.CTkEntry(form, font=font(12), height=32,
                                       placeholder_text="상담 메모…")
        self.memo_entry.pack(side="left", fill="x", expand=True, padx=8)
        self.callback_entry = ctk.CTkEntry(form, font=font(12), height=32, width=76,
                                           placeholder_text="14:30")
        # CALLBACK 선택 시에만 pack
        self.save_btn = ctk.CTkButton(form, text="💾 저장하고 다음 (F3)", width=170,
                                      height=36, font=font(13, "bold"), corner_radius=8,
                                      fg_color=COLORS["ink"], hover_color="#2d2a24",
                                      command=self.save_result)
        self.save_btn.pack(side="left", padx=(8, 0))

    def _bind_keys(self):
        root = self.winfo_toplevel()
        root.bind("<F1>", lambda e: self.dial())
        root.bind("<F2>", lambda e: self.hangup())
        root.bind("<F3>", lambda e: self.save_result())
        for code, _label, key in RESULTS:
            root.bind(key, lambda e, c=code: self._key_result(c))

    def _key_result(self, code: str):
        # 입력창에 타이핑 중일 때 숫자키를 가로채지 않는다
        focus = self.focus_get()
        if focus is not None and "entry" in str(focus).lower():
            return
        self.result_selector.set(code)

    # ---------- 큐 ----------

    def refresh_queue(self):
        run_bg(self, lambda: self.client.queue(),
               on_success=self._on_queue, on_error=self._on_queue_error)

    def _poll_queue(self):
        if self._destroyed:
            return
        self.refresh_queue()
        self.after(QUEUE_POLL_MS, self._poll_queue)

    def _on_queue(self, items: list[dict]):
        self.crm_dot.set_ok(True)
        self.leads = items
        ids = {x["id"] for x in items}
        if self.current is None or self.current["id"] not in ids:
            self._select(items[0] if items else None)
        else:
            self._render_queue()

    def _on_queue_error(self, exc: Exception):
        if isinstance(exc, AuthError):
            self.on_auth_lost()
            return
        self.crm_dot.set_ok(False)

    def _render_queue(self):
        for child in self.queue_box.winfo_children():
            child.destroy()
        now = now_local()
        for item in sort_queue(self.leads, now):
            due = is_callback_due(item, now)
            selected = self.current is not None and item["id"] == self.current["id"]
            bg = COLORS["brand_soft"] if due else (
                COLORS["hover"] if selected else COLORS["surface"])
            row = ctk.CTkFrame(self.queue_box, fg_color=bg, corner_radius=8)
            row.pack(fill="x", pady=2, padx=2)
            name = item.get("name") or "(이름없음)"
            prefix = "🔔 " if due else ""
            ctk.CTkLabel(row, text=f"{prefix}{name}", font=font(13),
                         text_color=COLORS["ink"], anchor="w").pack(
                side="left", padx=(10, 4), pady=6)
            Badge(row, item["status"]).pack(side="right", padx=8)
            dt = parse_iso(item.get("nextCallAt"))
            if dt is not None:
                ctk.CTkLabel(row, text=dt.strftime("%H:%M"), font=font(11),
                             text_color=COLORS["muted"]).pack(side="right")
            for widget in (row, *row.winfo_children()):
                widget.bind("<Button-1>", lambda e, it=item: self._select(it))
            if due and item["id"] not in self._notified_callbacks:
                self._notified_callbacks.add(item["id"])
                Toast(self.winfo_toplevel(),
                      f"🔔 재통화 시간: {name} ({dt.strftime('%H:%M') if dt else ''})")

    def _select(self, item: dict | None):
        if self.call_started:
            return  # 통화 중에는 리드 전환 금지
        self.current = item
        for child in self.badge_slot.winfo_children():
            child.destroy()
        if item is None:
            self.name_label.configure(text="✅ 대기 중인 콜이 없습니다")
            self.phone_label.configure(text="큐가 비어 있습니다 — 새 배정을 기다리세요")
            self.lead_memo_entry.delete(0, "end")
        else:
            self.name_label.configure(text=item.get("name") or "(이름없음)")
            self.phone_label.configure(text=f"📱 {item['phoneMasked']}")
            Badge(self.badge_slot, item["status"]).pack()
            self.lead_memo_entry.delete(0, "end")
            if item.get("memo"):
                self.lead_memo_entry.insert(0, item["memo"])
        self._render_queue()

    # ---------- 통화 ----------

    def dial(self):
        if self.current is None or self.call_started is not None:
            return
        if not adb.is_connected():
            messagebox.showwarning("ADB", "휴대폰이 연결되지 않았습니다.\nUSB 연결과 디버깅 허용을 확인하세요.")
            return
        lead = self.current
        self.dial_btn.configure(state="disabled", text="발신 중…")

        def work():
            phone = self.client.reveal(lead["id"])
            if not adb.call(phone):
                raise RuntimeError("ADB 발신에 실패했습니다.")

        run_bg(self, work, on_success=lambda _: self._on_dialed(),
               on_error=self._on_dial_error)

    def _on_dialed(self):
        self.call_started = time.monotonic()
        self.talk_seconds = 0
        self.today_dials += 1
        self._update_today()
        self.dial_btn.configure(text="📞 발신 (F1)")
        self.hangup_btn.configure(state="normal")

    def _on_dial_error(self, exc: Exception):
        self.dial_btn.configure(state="normal", text="📞 발신 (F1)")
        if isinstance(exc, AuthError):
            self.on_auth_lost()
        elif isinstance(exc, NetworkError):
            self.crm_dot.set_ok(False)
            messagebox.showerror("연결 오류", exc.message)
        elif isinstance(exc, ApiError):
            messagebox.showerror("발신 불가", exc.message)
        else:
            messagebox.showerror("오류", str(exc))

    def hangup(self):
        if self.call_started is None:
            return
        run_bg(self, adb.hangup)
        self._end_call()

    def _end_call(self):
        if self.call_started is not None:
            self.talk_seconds = int(time.monotonic() - self.call_started)
            self.call_started = None
        self.hangup_btn.configure(state="disabled")
        self.dial_btn.configure(state="normal")

    # ---------- 결과 기록 ----------

    def _on_result_change(self, code: str):
        if code == "CALLBACK":
            self.callback_entry.pack(side="left", padx=(8, 0), before=self.save_btn)
        else:
            self.callback_entry.pack_forget()

    def save_lead_memo(self):
        if self.current is None:
            return
        lead = self.current
        memo = self.lead_memo_entry.get().strip()
        run_bg(self, lambda: self.client.save_memo(lead["id"], memo),
               on_success=lambda _: Toast(self.winfo_toplevel(), "메모 저장됨"),
               on_error=self._on_dial_error)

    def save_result(self):
        if self.current is None:
            return
        code = self.result_selector.selected
        if code is None:
            messagebox.showwarning("결과 선택", "상담 결과를 먼저 선택하세요 (1~7).")
            return
        callback_at = None
        if code == "CALLBACK":
            callback_at = callback_iso(self.callback_entry.get(), now_local())
            if callback_at is None:
                messagebox.showwarning("시간 형식", "콜백 시간을 HH:MM 형식으로 입력하세요 (예: 14:30).")
                return
        if self.call_started is not None:
            self._end_call()
        lead = self.current
        payload = {"result_code": code, "talk_seconds": self.talk_seconds,
                   "memo": self.memo_entry.get().strip() or None,
                   "callback_at": callback_at}
        key = str(uuid.uuid4())
        self.save_btn.configure(state="disabled")

        def ok(_res):
            self.save_btn.configure(state="normal")
            if code == "WON":
                self.today_won += 1
            self._update_today()
            self._reset_form()
            self.refresh_queue()

        def err(exc: Exception):
            self.save_btn.configure(state="normal")
            if isinstance(exc, NetworkError):
                self.pending.add(idempotency_key=key, lead_id=lead["id"], payload=payload)
                self._update_banner()
                self.crm_dot.set_ok(False)
                Toast(self.winfo_toplevel(), "연결 실패 — 기록을 대기열에 보관했습니다")
                self._reset_form()
                self.leads = [x for x in self.leads if x["id"] != lead["id"]]
                self._select(self.leads[0] if self.leads else None)
            elif isinstance(exc, AuthError):
                self.pending.add(idempotency_key=key, lead_id=lead["id"], payload=payload)
                self.on_auth_lost()
            elif isinstance(exc, NightBlocked):
                messagebox.showwarning("야간 제한", exc.message)
            elif isinstance(exc, ApiError):
                messagebox.showerror("저장 실패", exc.message)
            else:
                messagebox.showerror("오류", str(exc))

        run_bg(self, lambda: self.client.log_call(lead["id"], idempotency_key=key,
                                                  **payload),
               on_success=ok, on_error=err)

    def _reset_form(self):
        self.result_selector.reset()
        self.memo_entry.delete(0, "end")
        self.callback_entry.delete(0, "end")
        self.callback_entry.pack_forget()
        self.talk_seconds = 0
        self.timer_label.configure(text="00:00")

    # ---------- 주기 작업 ----------

    def _update_today(self):
        self.today_label.configure(text=f"오늘: 발신 {self.today_dials} · 가입 {self.today_won}")

    def _update_banner(self):
        n = len(self.pending.items())
        self.banner.configure(text=f"📤 전송 대기 {n}건" if n else "")

    def _tick(self):
        if self._destroyed:
            return
        if self.call_started is not None:
            self.timer_label.configure(
                text=format_seconds(int(time.monotonic() - self.call_started)))
        self.after(TICK_MS, self._tick)

    def _poll_adb(self):
        if self._destroyed:
            return
        run_bg(self, adb.is_connected, on_success=self.adb_dot.set_ok)
        self.after(ADB_POLL_MS, self._poll_adb)

    def _poll_flush(self):
        if self._destroyed:
            return
        if self.pending.items():
            def done(_res):
                self._update_banner()

            def fail(exc):
                if isinstance(exc, AuthError):
                    self.on_auth_lost()

            run_bg(self, lambda: self.pending.flush(self.client),
                   on_success=done, on_error=fail)
        self.after(FLUSH_POLL_MS, self._poll_flush)

    def destroy(self):
        self._destroyed = True
        super().destroy()
