"""로그인 화면 — CRM 계정으로 로그인, MFA 계정은 코드 입력란이 나타난다."""
from __future__ import annotations

from tkinter import messagebox

import customtkinter as ctk
from PIL import Image

from api import ApiError, MfaRequired, NetworkError
from logic import ascii_only
from resources import resource_path
from ui.theme import COLORS, font
from ui.widgets import run_bg


class LoginFrame(ctk.CTkFrame):
    def __init__(self, master, client, config, on_success):
        super().__init__(master, fg_color=COLORS["background"])
        self.client = client
        self.config_data = config
        self.on_success = on_success
        self._mfa_visible = False
        self._build()

    def _ascii_var(self) -> ctk.StringVar:
        """비ASCII(한글 IME 등) 입력을 즉시 제거하는 StringVar."""
        var = ctk.StringVar()

        def sanitize(*_args):
            cleaned = ascii_only(var.get())
            if cleaned != var.get():
                var.set(cleaned)

        var.trace_add("write", sanitize)
        return var

    def _field(self, parent, label: str, textvariable=None, show: str | None = None):
        ctk.CTkLabel(parent, text=label, font=font(12),
                     text_color=COLORS["muted"], anchor="w").pack(
            fill="x", padx=48, pady=(10, 2))
        entry = ctk.CTkEntry(parent, width=260, height=40, font=font(13),
                             textvariable=textvariable,
                             **({"show": show} if show else {}))
        entry.pack(padx=48)
        return entry

    def _build(self):
        card = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=16,
                            border_width=1, border_color=COLORS["line"])
        card.place(relx=0.5, rely=0.45, anchor="center")

        try:
            self._logo = ctk.CTkImage(
                light_image=Image.open(resource_path("assets/milestone_logo.png")),
                size=(160, 160))
            ctk.CTkLabel(card, image=self._logo, text="").pack(padx=48, pady=(28, 0))
        except OSError:
            ctk.CTkLabel(card, text="Milestone Dialer", font=font(22, "bold"),
                         text_color=COLORS["ink"]).pack(padx=48, pady=(36, 4))

        ctk.CTkLabel(card, text="CRM 계정으로 로그인하세요", font=font(12),
                     text_color=COLORS["muted"]).pack(pady=(6, 4))

        self.id_var = self._ascii_var()
        self.id_entry = self._field(card, "아이디", textvariable=self.id_var)
        if self.config_data.last_login_id:
            self.id_var.set(self.config_data.last_login_id)

        self.pw_var = self._ascii_var()
        self.pw_entry = self._field(card, "비밀번호", textvariable=self.pw_var, show="•")

        self.mfa_var = self._ascii_var()
        self.mfa_label = ctk.CTkLabel(card, text="인증앱 6자리 코드", font=font(12),
                                      text_color=COLORS["muted"], anchor="w")
        self.mfa_entry = ctk.CTkEntry(card, width=260, height=40, font=font(13),
                                      textvariable=self.mfa_var)
        # MFA 필요해질 때만 pack

        self.error_label = ctk.CTkLabel(card, text="", font=font(12),
                                        text_color=COLORS["danger"], wraplength=260)
        self.error_label.pack(pady=(8, 0))

        self.submit_btn = ctk.CTkButton(card, text="로그인", width=260, height=42,
                                        font=font(14, "bold"), corner_radius=8,
                                        fg_color=COLORS["ink"], hover_color="#2d2a24",
                                        command=self.submit)
        self.submit_btn.pack(padx=48, pady=(10, 8))

        ctk.CTkButton(card, text="서버 주소 설정", width=260, height=28, font=font(11),
                      fg_color="transparent", text_color=COLORS["muted"],
                      hover_color=COLORS["hover"],
                      command=self._server_settings).pack(pady=(0, 28))

        for entry in (self.id_entry, self.pw_entry, self.mfa_entry):
            entry.bind("<Return>", lambda e: self.submit())

    def _server_settings(self):
        dialog = ctk.CTkInputDialog(title="서버 주소",
                                    text=f"CRM 서버 주소:\n(현재: {self.client.base_url})")
        value = dialog.get_input()
        if value and value.strip():
            self.client.base_url = value.strip().rstrip("/")
            self.config_data.server_url = self.client.base_url
            self.config_data.save()

    def submit(self):
        login_id = self.id_var.get().strip()
        password = self.pw_var.get()
        code = self.mfa_var.get().strip() or None if self._mfa_visible else None
        if not login_id or not password:
            self.error_label.configure(text="아이디와 비밀번호를 입력하세요.")
            return
        self.submit_btn.configure(state="disabled", text="확인 중…")
        self.error_label.configure(text="")
        run_bg(self, lambda: self.client.login(login_id, password, code),
               on_success=self._on_login, on_error=self._on_error)

    def _on_login(self, user: dict):
        self.submit_btn.configure(state="normal", text="로그인")
        if user.get("mustChangePassword"):
            messagebox.showwarning(
                "비밀번호 변경 필요",
                "초기 비밀번호 상태입니다.\n웹 CRM에서 비밀번호를 변경한 뒤 다시 로그인하세요.")
            self.client.logout()
            return
        self.config_data.last_login_id = user["loginId"]
        self.config_data.save()
        self.on_success()

    def _on_error(self, exc: Exception):
        self.submit_btn.configure(state="normal", text="로그인")
        if isinstance(exc, MfaRequired):
            if not self._mfa_visible:
                self._mfa_visible = True
                self.mfa_label.pack(fill="x", padx=48, pady=(10, 2), after=self.pw_entry)
                self.mfa_entry.pack(padx=48, after=self.mfa_label)
                self.error_label.configure(text="인증앱의 6자리 코드를 입력하세요.")
            else:
                self.error_label.configure(text=exc.message)
            self.mfa_entry.focus()
        elif isinstance(exc, (ApiError, NetworkError)):
            self.error_label.configure(text=exc.message)
        else:
            self.error_label.configure(text=f"오류: {exc}")
