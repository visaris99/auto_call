"""공용 위젯 + 백그라운드 실행 헬퍼."""
from __future__ import annotations

import threading

import customtkinter as ctk

from ui.theme import COLORS, RESULTS, STATUS_LABEL, font, status_colors


def run_bg(widget, work, on_success=None, on_error=None):
    """work()를 데몬 스레드에서 실행하고 결과를 widget.after로 UI 스레드에 전달."""

    def runner():
        try:
            result = work()
        except Exception as exc:  # noqa: BLE001 — UI 콜백으로 전달
            if on_error:
                widget.after(0, lambda: on_error(exc))
        else:
            if on_success:
                widget.after(0, lambda: on_success(result))

    threading.Thread(target=runner, daemon=True).start()


class Badge(ctk.CTkLabel):
    def __init__(self, master, status: str):
        bg, fg = status_colors(status)
        super().__init__(master, text=f" {STATUS_LABEL.get(status, status)} ",
                         fg_color=bg, text_color=fg, corner_radius=6,
                         font=font(11, "bold"), height=22)


class StatusDot(ctk.CTkLabel):
    """상단 상태바의 ● ADB / ● CRM 표시."""

    def __init__(self, master, text: str):
        super().__init__(master, text=f"● {text}", font=font(12),
                         text_color=COLORS["muted"])

    def set_ok(self, ok: bool):
        self.configure(text_color=COLORS["success"] if ok else COLORS["danger"])


class ResultSelector(ctk.CTkFrame):
    """콜 결과 7종 버튼 — CRM 결과코드와 동일, 숫자키 1~7."""

    def __init__(self, master, on_change=None):
        super().__init__(master, fg_color="transparent")
        self.selected: str | None = None
        self.on_change = on_change
        self._buttons: dict[str, ctk.CTkButton] = {}
        for i, (code, label, key) in enumerate(RESULTS):
            btn = ctk.CTkButton(self, text=f"{label}\n({key})", width=76, height=48,
                                font=font(12), corner_radius=8,
                                command=lambda c=code: self.set(c))
            btn.grid(row=0, column=i, padx=3, pady=2)
            self._buttons[code] = btn
        self._restyle()

    def _restyle(self):
        for code, btn in self._buttons.items():
            if code == self.selected:
                btn.configure(fg_color=COLORS["ink"], text_color="white",
                              hover_color=COLORS["ink"])
            else:
                bg, fg = status_colors(code)
                btn.configure(fg_color=bg, text_color=fg, hover_color=COLORS["hover"])

    def set(self, code: str):
        self.selected = code
        self._restyle()
        if self.on_change:
            self.on_change(code)

    def reset(self):
        self.selected = None
        self._restyle()


class Toast(ctk.CTkToplevel):
    """우상단에 잠깐 떴다 사라지는 알림."""

    def __init__(self, master, message: str, duration_ms: int = 4000):
        super().__init__(master)
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        frame = ctk.CTkFrame(self, fg_color=COLORS["ink"], corner_radius=10)
        frame.pack()
        ctk.CTkLabel(frame, text=message, text_color="white",
                     font=font(13)).pack(padx=16, pady=10)
        self.update_idletasks()
        x = master.winfo_rootx() + master.winfo_width() - self.winfo_width() - 24
        y = master.winfo_rooty() + 70
        self.geometry(f"+{x}+{y}")
        self.after(duration_ms, self.destroy)
