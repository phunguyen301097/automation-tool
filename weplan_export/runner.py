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

from .actions import STEPS, Context, StepError, _safe
from .config import Scenario


@dataclass
class Result:
    scenario: str
    ok: bool
    seconds: float
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
        page = ctx.new_page()
        page.goto(config["base_url"], wait_until="domcontentloaded")
        log("Log in in the opened browser window, wait until the dashboard is shown,")
        input("then press ENTER here to save the session... ")
        state.parent.mkdir(parents=True, exist_ok=True)
        ctx.storage_state(path=str(state))
        log(f"Session saved to {state}")
        browser.close()


def run_scenarios(scenarios: list[Scenario], config: dict, headed: bool | None = None,
                  slow_mo: int | None = None, trace: bool = False, stop_on_fail: bool = False) -> list[Result]:
    run_id = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    out_root = Path(config["output_dir"])
    artifacts_dir = out_root / "_runs" / run_id
    results: list[Result] = []

    with sync_playwright() as pw:
        browser = launch_browser(pw, config, headed=headed, slow_mo=slow_mo)
        for idx, sc in enumerate(scenarios, 1):
            log(f"=== [{idx}/{len(scenarios)}] {sc.name}")
            ctx = new_context(browser, config)
            if trace:
                ctx.tracing.start(screenshots=True, snapshots=True, sources=False)
            page = ctx.new_page()
            sctx = Context(page, config, sc, out_root, log)
            t0 = time.time()
            res = Result(scenario=sc.name, ok=False, seconds=0)
            current = None
            try:
                if config["auth"].get("required", True):
                    _ensure_session(page, config)
                for i, st in enumerate(sc.steps, 1):
                    if not isinstance(st, dict) or len(st) != 1:
                        raise StepError(f"Step {i} must be a mapping with a single key, got: {st!r}")
                    name, args = next(iter(st.items()))
                    current = f"{i}. {name}"
                    fn = STEPS.get(name)
                    if fn is None:
                        raise StepError(f"Unknown step '{name}'. Available: {sorted(STEPS)}")
                    log(f"- {current}")
                    fn(sctx, args)
                res.ok = True
            except Exception as e:
                res.error = f"{type(e).__name__}: {e}"
                res.failed_step = current
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
                if trace:
                    artifacts_dir.mkdir(parents=True, exist_ok=True)
                    tpath = artifacts_dir / f"{_safe(sc.name)}_trace.zip"
                    ctx.tracing.stop(path=str(tpath))
                    res.artifacts.append(str(tpath))
                res.seconds = round(time.time() - t0, 1)
                res.downloads = sctx.downloads
                ctx.close()
            results.append(res)
            log(f"=== {'PASS' if res.ok else 'FAIL'} {sc.name} ({res.seconds}s)")
            if stop_on_fail and not res.ok:
                break
        browser.close()

    _write_report(results, artifacts_dir)
    return results


def _ensure_session(page, config: dict) -> None:
    page.goto(config["base_url"].rstrip("/") + config["start_path"], wait_until="domcontentloaded")
    if is_logged_in(page, config):
        return
    if auto_login(page, config):
        return
    raise StepError("Not logged in. Run `python -m weplan_export login` first "
                    f"(or set {config['auth']['username_env']}/{config['auth']['password_env']}).")


def _write_report(results: list[Result], artifacts_dir: Path) -> None:
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    (artifacts_dir / "report.json").write_text(
        json.dumps([r.__dict__ for r in results], indent=2, ensure_ascii=False), encoding="utf-8")
    passed = sum(r.ok for r in results)
    print()
    print(f"{'RESULT':6} {'TIME':>7}  SCENARIO / FILE")
    for r in results:
        print(f"{'PASS' if r.ok else 'FAIL':6} {r.seconds:>6}s  {r.scenario}")
        for d in r.downloads:
            print(f"{'':16}-> {d['file']} ({d.get('data_rows', '?')} rows)")
        if r.error:
            print(f"{'':16}!! {r.failed_step}: {r.error}")
            for a in r.artifacts:
                print(f"{'':16}   {a}")
    print(f"\n{passed}/{len(results)} passed. Report: {artifacts_dir / 'report.json'}")
