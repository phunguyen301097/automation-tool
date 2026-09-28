"""Browser session management and scenario execution."""
from __future__ import annotations

import datetime as dt
import json
import os
import time
import traceback
from dataclasses import dataclass, field
from pathlib import Path

from playwright.sync_api import Browser, BrowserContext, Playwright, sync_playwright

from .actions import STEPS, Context, StepError, _safe, dismiss_popups, install_popup_handler
from .config import Scenario


@dataclass
class Result:
    scenario: str
    ok: bool
    seconds: float
    status: str = ""  # PASS | FAIL | STOPPED | NOT RUN
    downloads: list[dict] = field(default_factory=list)
    error: str | None = None
    failed_step: str | None = None
    artifacts: list[str] = field(default_factory=list)


def log(msg: str) -> None:
    print(f"[{dt.datetime.now():%H:%M:%S}] {msg}", flush=True)


def launch_browser(pw: Playwright, config: dict, headed: bool | None = None, slow_mo: int | None = None) -> Browser:
    b = config["browser"]
    kwargs: dict = {
        "headless": (not headed) if headed is not None else b["headless"],
        "slow_mo": slow_mo if slow_mo is not None else b.get("slow_mo", 0),
    }
    exe = b.get("executable_path") or os.environ.get("BROWSER_EXECUTABLE")
    if b.get("channel"):
        kwargs["channel"] = b["channel"]
    elif exe:
        kwargs["executable_path"] = exe
    try:
        return pw.chromium.launch(**kwargs)
    except Exception as e:
        # Fall back to a system Chrome/Edge if Playwright's bundled browser is not installed.
        if "Executable doesn't exist" in str(e) and not kwargs.get("channel"):
            for channel in ("chrome", "msedge"):
                try:
                    log(f"Bundled Chromium missing, trying installed '{channel}'")
                    return pw.chromium.launch(**{**kwargs, "channel": channel})
                except Exception:
                    continue
        raise


def new_context(browser: Browser, config: dict, use_state: bool = True) -> BrowserContext:
    b = config["browser"]
    kwargs: dict = {
        "accept_downloads": True,
        "viewport": b.get("viewport"),
        "locale": b.get("locale"),
    }
    state = Path(config["auth"]["storage_state"])
    if use_state and state.exists():
        kwargs["storage_state"] = str(state)
    ctx = browser.new_context(**kwargs)
    ctx.set_default_timeout(config["timeouts"]["default"])
    ctx.set_default_navigation_timeout(config["timeouts"]["navigation"])
    return ctx


def is_logged_in(page, config: dict) -> bool:
    url = page.url.lower()
    if any(m in url for m in config["auth"]["login_url_markers"]):
        return False
    try:
        return page.locator(config["selectors"]["logged_in_marker"]).count() > 0
    except Exception:
        return False


def auto_login(page, config: dict) -> bool:
    """Log in with WEPLAN_USERNAME / WEPLAN_PASSWORD env vars, if set."""
    a = config["auth"]
    user, pwd = os.environ.get(a["username_env"]), os.environ.get(a["password_env"])
    if not (user and pwd):
        return False
    log("Session not valid -> logging in with credentials from environment")
    page.goto(config["base_url"].rstrip("/") + a["login_path"], wait_until="domcontentloaded")
    page.locator(a["username_selector"]).first.fill(user)
    page.locator(a["password_selector"]).first.fill(pwd)
    page.locator(a["submit_selector"]).first.click()
    page.wait_for_load_state("domcontentloaded")
    try:
        page.locator(config["selectors"]["logged_in_marker"]).first.wait_for(timeout=config["timeouts"]["navigation"])
    except Exception:
        return False
    Path(a["storage_state"]).parent.mkdir(parents=True, exist_ok=True)
    page.context.storage_state(path=a["storage_state"])
    return True


def interactive_login(config: dict) -> None:
    """Open a visible browser, let the user log in, then save the session."""
    state = Path(config["auth"]["storage_state"])
    with sync_playwright() as pw:
        browser = launch_browser(pw, config, headed=True)
        ctx = new_context(browser, config, use_state=False)
        _keep_manual_downloads(ctx, config)
        page = ctx.new_page()
        page.goto(config["base_url"], wait_until="domcontentloaded")
        log("Log in in the opened browser window, wait until the dashboard is shown,")
        input("then press ENTER here to save the session... ")
        state.parent.mkdir(parents=True, exist_ok=True)
        ctx.storage_state(path=str(state))
        log(f"Session saved to {state}")
        browser.close()


def _keep_manual_downloads(ctx: BrowserContext, config: dict) -> None:
    """Playwright stores downloads under a temp GUID name and deletes them when the
    browser closes. Save files downloaded by hand in a tool-opened window instead."""
    target = Path(config["output_dir"]) / "manual"

    def on_page(page):
        def on_download(download):
            target.mkdir(parents=True, exist_ok=True)
            path = target / download.suggested_filename
            download.save_as(str(path))
            log(f"Saved manual download -> {path}")
        page.on("download", on_download)

    for p in ctx.pages:
        on_page(p)
    ctx.on("page", on_page)


def inspect_page(config: dict, headed: bool = False, view: str = "macro") -> Path:
    """Dump the rendered DOM of the date widget and the result table to help tune selectors."""
    from .actions import STEPS, Context
    from .config import Scenario

    out = Path(config["output_dir"]) / "_inspect" / dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    out.mkdir(parents=True, exist_ok=True)
    sel = config["selectors"]

    def dump(page, name: str, css: str | None = None) -> None:
        if css:
            html = page.evaluate(
                "(s) => Array.from(document.querySelectorAll(s)).map(e => e.outerHTML).join('\\n\\n') || 'NOT FOUND'", css)
        else:
            html = page.content()
        (out / f"{name}.html").write_text(html, encoding="utf-8")
        page.screenshot(path=str(out / f"{name}.png"), full_page=True)
        log(f"  wrote {out / name}.html/.png")

    with sync_playwright() as pw:
        browser = launch_browser(pw, config, headed=headed)
        ctx = new_context(browser, config)
        _keep_manual_downloads(ctx, config)
        page = ctx.new_page()
        install_popup_handler(page, config, log)
        if config["auth"].get("required", True):
            _ensure_session(page, config)
        else:
            page.goto(config["base_url"].rstrip("/") + config["start_path"])
        try:
            page.wait_for_load_state("networkidle", timeout=20_000)
        except Exception:
            pass
        page.wait_for_timeout(3000)
        dump(page, "1_page")
        dump(page, "2_datepicker", sel["datepicker"])
        # Open the date widget and capture whatever popup appears.
        try:
            root = page.locator(sel["datepicker"]).first
            clickable = root.locator("input, button, [role=button], .form-control, div, span")
            (clickable.first if clickable.count() else root).click()
            page.wait_for_timeout(1500)
            dump(page, "3_page_datepicker_open")
            page.keyboard.press("Escape")
        except Exception as e:
            log(f"  could not open date widget: {e}")
        # Load the result table and capture the area around "Download table".
        try:
            sctx = Context(page, config, Scenario(name="inspect", steps=[]), out, log)
            STEPS["choose_view"](sctx, view)
            STEPS["wait_for_table"](sctx, {})
            dump(page, "4_results", sel["results"])
            btn = page.locator("button, a, [role=button]").filter(has_text=sel["download_button_text"]).first
            if btn.count():
                btn.click()
                page.wait_for_timeout(1500)
                dump(page, "5_download_menu_open")
            else:
                log(f"  button '{sel['download_button_text']}' not found")
        except Exception as e:
            log(f"  could not load results: {e}")
            dump(page, "4_page_error")
        browser.close()
    log(f"Done. Send the folder {out} (zip) to adjust selectors.")
    return out


class _RunState:
    """Tracks whether the user closed the browser / pressed Ctrl+C, to stop the whole run."""

    def __init__(self):
        self.stop_reason: str | None = None
        self.closing = False  # True while the tool itself closes pages/contexts

    def watch_page(self, page) -> None:
        page.on("close", lambda _: self._closed("the browser window was closed"))

    def watch_browser(self, browser) -> None:
        browser.on("disconnected", lambda _: self._closed("the browser was closed"))

    def _closed(self, reason: str) -> None:
        if not self.closing and not self.stop_reason:
            self.stop_reason = reason


_FIRST_NAV_STEPS = {"goto", "open_menu", "menu", "select_country", "country"}


def run_scenarios(scenarios: list[Scenario], config: dict, headed: bool | None = None,
                  slow_mo: int | None = None, trace: bool = False, stop_on_fail: bool = False,
                  isolated: bool | None = None, done: list[str] | None = None) -> list[Result]:
    """Run scenarios one after another.

    By default all scenarios share one browser page (one window with --headed, a single
    login check). isolated=True gives each scenario a fresh context instead. Closing the
    browser window or pressing Ctrl+C stops the whole run; remaining scenarios are reported
    as NOT RUN. `done` are scenarios finished in an earlier run (--resume): recorded as DONE
    in this run's report so a later --resume skips them too. The report is rewritten after
    every scenario, so it survives the process being killed.
    """
    if isolated is None:
        isolated = bool(config["browser"].get("isolated", False))
    run_id = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    out_root = Path(config["output_dir"])
    artifacts_dir = out_root / "_runs" / run_id
    n = 2
    while artifacts_dir.exists():  # two runs in the same second
        artifacts_dir = out_root / "_runs" / f"{run_id}_{n}"
        n += 1
    results: list[Result] = []
    earlier = [Result(scenario=n, ok=True, seconds=0, status="DONE") for n in (done or [])]
    state = _RunState()

    def save_progress() -> None:
        ran = {r.scenario for r in results}
        pending = [Result(scenario=s.name, ok=False, seconds=0, status="NOT RUN")
                   for s in scenarios if s.name not in ran]
        _write_report_json(earlier + results + pending, artifacts_dir)

    save_progress()

    pw = sync_playwright().start()
    browser = ctx = page = None
    try:
        browser = launch_browser(pw, config, headed=headed, slow_mo=slow_mo)
        state.watch_browser(browser)
        if not isolated:
            ctx = new_context(browser, config)
            if trace:
                ctx.tracing.start(screenshots=True, snapshots=True, sources=False)
            page = ctx.new_page()
            state.watch_page(page)
            install_popup_handler(page, config, log)
        session_checked = False
        previous_failed = False

        for idx, sc in enumerate(scenarios, 1):
            if state.stop_reason:
                results.append(Result(scenario=sc.name, ok=False, seconds=0, status="NOT RUN"))
                continue
            log(f"=== [{idx}/{len(scenarios)}] {sc.name}")
            if isolated:
                ctx = new_context(browser, config)
                if trace:
                    ctx.tracing.start(screenshots=True, snapshots=True, sources=False)
                page = ctx.new_page()
                state.watch_page(page)
                install_popup_handler(page, config, log)
                session_checked = False
            elif trace:
                ctx.tracing.start_chunk()
            sctx = Context(page, config, sc, out_root, log)
            t0 = time.time()
            res = Result(scenario=sc.name, ok=False, seconds=0)
            current = None
            try:
                first = next(iter(sc.steps[0])) if sc.steps and isinstance(sc.steps[0], dict) else None
                if config["auth"].get("required", True) and not session_checked:
                    _ensure_session(page, config)
                    session_checked = True
                elif previous_failed or page.url == "about:blank" or first not in _FIRST_NAV_STEPS:
                    # Start from a clean page when the previous scenario broke off midway,
                    # is still blank, or does not navigate by itself.
                    page.goto(config["base_url"].rstrip("/") + config["start_path"], wait_until="domcontentloaded")
                for i, st in enumerate(sc.steps, 1):
                    if not isinstance(st, dict) or len(st) != 1:
                        raise StepError(f"Step {i} must be a mapping with a single key, got: {st!r}")
                    name, args = next(iter(st.items()))
                    current = f"{i}. {name}"
                    fn = STEPS.get(name)
                    if fn is None:
                        raise StepError(f"Unknown step '{name}'. Available: {sorted(STEPS)}")
                    log(f"- {current}")
                    if config["popups"].get("auto_dismiss", True) and name not in ("dismiss_popups", "close_popups"):
                        dismiss_popups(page, config, log)
                    fn(sctx, args)
                res.ok = True
                res.status = "PASS"
            except KeyboardInterrupt:
                state.stop_reason = state.stop_reason or "interrupted (Ctrl+C)"
                res.status = "STOPPED"
                res.error = f"Stopped: {state.stop_reason}"
                res.failed_step = current
            except Exception as e:
                res.failed_step = current
                if state.stop_reason:
                    res.status = "STOPPED"
                    res.error = f"Stopped: {state.stop_reason}"
                else:
                    res.status = "FAIL"
                    res.error = f"{type(e).__name__}: {e}"
                    log(f"FAILED at step {current}: {res.error}")
                    if not isinstance(e, StepError):
                        traceback.print_exc()
                    artifacts_dir.mkdir(parents=True, exist_ok=True)
                    base = artifacts_dir / _safe(sc.name)
                    try:
                        page.screenshot(path=f"{base}.png", full_page=True)
                        Path(f"{base}.html").write_text(page.content(), encoding="utf-8")
                        res.artifacts += [f"{base}.png", f"{base}.html"]
                    except Exception:
                        pass
            finally:
                res.seconds = round(time.time() - t0, 1)
                res.downloads = sctx.downloads
                if not state.stop_reason:
                    if trace:
                        artifacts_dir.mkdir(parents=True, exist_ok=True)
                        tpath = artifacts_dir / f"{_safe(sc.name)}_trace.zip"
                        try:
                            (ctx.tracing.stop if isolated else ctx.tracing.stop_chunk)(path=str(tpath))
                            res.artifacts.append(str(tpath))
                        except Exception:
                            pass
                    if isolated:
                        state.closing = True
                        ctx.close()
                        state.closing = False
            results.append(res)
            save_progress()
            previous_failed = not res.ok
            if state.stop_reason:
                log(f"=== STOPPED {sc.name}: {state.stop_reason} -> stopping the run")
            else:
                log(f"=== {res.status} {sc.name} ({res.seconds}s)")
            if stop_on_fail and not res.ok and not state.stop_reason:
                state.stop_reason = "--stop-on-fail"
    except KeyboardInterrupt:
        state.stop_reason = state.stop_reason or "interrupted (Ctrl+C)"
        log(f"Stopping: {state.stop_reason}")
    finally:
        # Keep the refreshed session (cookies, "announcement seen" flags) for the next run.
        if not isolated and page is not None and not state.stop_reason:
            _save_session(page, config)
        done = {r.scenario for r in results}
        results += [Result(scenario=s.name, ok=False, seconds=0, status="NOT RUN")
                    for s in scenarios if s.name not in done]
        state.closing = True
        for close in (lambda: browser and browser.close(), pw.stop):
            try:
                close()
            except Exception:
                pass

    _write_report(earlier + results, artifacts_dir, state.stop_reason)
    return results


def _save_session(page, config: dict) -> None:
    if not config["auth"].get("required", True):
        return
    try:
        if not page.is_closed() and is_logged_in(page, config):
            page.context.storage_state(path=config["auth"]["storage_state"])
    except Exception:
        pass


def _ensure_session(page, config: dict) -> None:
    page.goto(config["base_url"].rstrip("/") + config["start_path"], wait_until="domcontentloaded")
    if is_logged_in(page, config):
        return
    if auto_login(page, config):
        return
    raise StepError("Not logged in. Run `python -m weplan_export login` first "
                    f"(or set {config['auth']['username_env']}/{config['auth']['password_env']}).")


def _write_report_json(results: list[Result], artifacts_dir: Path) -> None:
    """Write report.json atomically (a killed process never leaves a half-written file)."""
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    tmp = artifacts_dir / "report.json.tmp"
    tmp.write_text(json.dumps([r.__dict__ for r in results], indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, artifacts_dir / "report.json")


def completed_in_last_run(output_dir: str | Path, run: str | None = None) -> tuple[Path | None, set[str]]:
    """Scenarios finished (PASS or DONE) in the latest run, or in `run` (a folder of downloads/_runs)."""
    runs = Path(output_dir) / "_runs"
    if run and run != "latest":
        report = Path(run) / "report.json" if Path(run).is_dir() else runs / run / "report.json"
    else:
        reports = sorted(runs.glob("*/report.json"))
        report = reports[-1] if reports else None
    if report is None or not report.exists():
        return None, set()
    data = json.loads(report.read_text(encoding="utf-8"))
    return report, {r["scenario"] for r in data if r.get("status") in ("PASS", "DONE")}


def _write_report(results: list[Result], artifacts_dir: Path, stop_reason: str | None = None) -> None:
    _write_report_json(results, artifacts_dir)
    passed = sum(r.ok for r in results)
    earlier = [r for r in results if r.status == "DONE"]
    print()
    if earlier:
        print(f"DONE in an earlier run (skipped): {len(earlier)} scenario(s)")
    print(f"{'RESULT':8} {'TIME':>7}  SCENARIO / FILE")
    for r in results:
        if r.status == "DONE":
            continue
        status = r.status or ("PASS" if r.ok else "FAIL")
        print(f"{status:8} {r.seconds:>6}s  {r.scenario}")
        for d in r.downloads:
            print(f"{'':18}-> {d['file']} ({d.get('data_rows', '?')} rows)")
        if r.error and status == "FAIL":
            print(f"{'':18}!! {r.failed_step}: {r.error}")
            for a in r.artifacts:
                print(f"{'':18}   {a}")
    if stop_reason:
        print(f"\nRun stopped: {stop_reason}.")
    left = sum(1 for r in results if not r.ok)
    print(f"\n{passed}/{len(results)} done. Report: {artifacts_dir / 'report.json'}")
    if left:
        print(f"{left} scenario(s) not finished: run the same command again with --resume to continue.")
