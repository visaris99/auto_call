import threading

from ui.theme import COLORS, STATUS_LABEL, RESULTS, status_colors
from ui.widgets import run_bg

ALL_STATUSES = ["NEW", "ASSIGNED", "NOANSWER", "CALLBACK", "INTERESTED",
                "CONSULT", "WON", "REJECT", "DNC", "RECYCLE"]


def test_every_status_has_label_and_colors():
    for s in ALL_STATUSES:
        assert s in STATUS_LABEL
        bg, fg = status_colors(s)
        assert bg.startswith("#") and fg.startswith("#")


def test_results_are_seven_with_keys_1_to_7():
    assert [r[2] for r in RESULTS] == [str(i) for i in range(1, 8)]
    codes = [r[0] for r in RESULTS]
    assert codes == ["NOANSWER", "CALLBACK", "INTERESTED", "CONSULT", "WON", "REJECT", "DNC"]


def test_crm_variant_mapping():
    """badge.tsx statusVariant와 동일해야 한다: WON/INTERESTED/CONSULT=brand,
    DNC/REJECT=danger, ASSIGNED/CALLBACK=soft."""
    assert status_colors("WON") == (COLORS["brand"], COLORS["ink"])
    assert status_colors("REJECT") == (COLORS["danger_soft"], COLORS["danger"])
    assert status_colors("CALLBACK") == (COLORS["brand_soft"], COLORS["ink"])
    assert status_colors("NEW") == (COLORS["surface2"], COLORS["foreground"])


class DummyWidget:
    """tk 없이 after만 흉내 — 지연 후 타이머 스레드에서 실행."""

    def after(self, delay, fn):
        threading.Timer(max(delay, 1) / 1000, fn).start()


def test_run_bg_success_and_error():
    done = threading.Event()
    results = {}

    def ok_work():
        return 42

    run_bg(DummyWidget(), ok_work,
           on_success=lambda v: (results.__setitem__("v", v), done.set()))
    assert done.wait(2) and results["v"] == 42

    err_done = threading.Event()
    run_bg(DummyWidget(), lambda: 1 / 0,
           on_error=lambda e: (results.__setitem__("e", type(e).__name__), err_done.set()))
    assert err_done.wait(2) and results["e"] == "ZeroDivisionError"
