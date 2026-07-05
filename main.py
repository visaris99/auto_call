"""TM 다이얼러 진입점."""
from __future__ import annotations

import datetime
import sys
import traceback
from tkinter import messagebox

import customtkinter as ctk

from api import ApiClient
from resources import load_private_fonts, resource_path
from state import Config, PendingCallQueue, config_dir
from ui.login import LoginFrame
from ui.theme import COLORS
from ui.widgets import run_bg
from ui.workspace import WorkspaceFrame
from version import APP_NAME, VERSION


def log_error(message: str) -> None:
    try:
        stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        with open(config_dir() / "error_log.txt", "a", encoding="utf-8") as f:
            f.write(f"[{stamp}] {message}\n")
    except OSError:
        pass


class App(ctk.CTk):
    def __init__(self):
        super().__init__(fg_color=COLORS["background"])
        ctk.set_appearance_mode("light")
        self.title(f"{APP_NAME} v{VERSION}")
        self.geometry("1000x680")
        self.minsize(920, 620)
        try:
            if sys.platform == "win32":
                # iconbitmap을 직접 호출해야 CTk가 200ms 뒤 기본 아이콘으로
                # 덮어쓰는 동작(_windows_set_titlebar_icon)을 건너뛴다.
                self.iconbitmap(resource_path("assets/icon.ico"))
            else:
                import tkinter as tk

                self._icon = tk.PhotoImage(file=resource_path("assets/milestone_logo.png"))
                self.iconphoto(True, self._icon)
        except Exception:  # noqa: BLE001 — 아이콘 실패는 치명적이지 않음
            pass

        self.config_data = Config.load()
        self.client = ApiClient(self.config_data.server_url)
        self.pending = PendingCallQueue()
        self._frame: ctk.CTkFrame | None = None
        self.show_login()
        self.after(500, self._check_version)

    def _swap(self, frame: ctk.CTkFrame):
        if self._frame is not None:
            self._frame.destroy()
        self._frame = frame
        frame.pack(fill="both", expand=True)

    def show_login(self):
        self._swap(LoginFrame(self, self.client, self.config_data,
                              on_success=self.show_workspace))

    def show_workspace(self):
        self._swap(WorkspaceFrame(self, self.client, self.config_data, self.pending,
                                  on_auth_lost=self.on_auth_lost))

    def on_auth_lost(self):
        messagebox.showinfo("세션 만료", "세션이 만료되었습니다. 다시 로그인해주세요.")
        self.show_login()

    def _check_version(self):
        def done(info):
            if not info:
                return
            min_version = str(info.get("minVersion") or "0")
            if tuple(VERSION.split(".")) < tuple(min_version.split(".")):
                messagebox.showwarning(
                    "업데이트 필요",
                    f"이 버전({VERSION})은 더 이상 지원되지 않습니다.\n"
                    f"관리자에게 새 버전을 요청하세요. (최신: {info.get('latestVersion')})")

        run_bg(self, self.client.check_version, on_success=done)


def main():
    try:
        load_private_fonts()  # assets/fonts/*.ttf 동봉 폰트 등록 (Windows)
        App().mainloop()
    except Exception as exc:  # noqa: BLE001 — 마지막 안전망
        log_error(f"Fatal: {exc}\n{traceback.format_exc()}")
        try:
            messagebox.showerror("치명적 오류", f"프로그램을 시작할 수 없습니다.\n{exc}")
        except Exception:  # noqa: BLE001
            pass
        raise


if __name__ == "__main__":
    main()
