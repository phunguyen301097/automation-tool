"""Scenario steps. Each public step is registered in STEPS and receives (ctx, args)."""
from __future__ import annotations

import datetime as dt
import re
import time
from pathlib import Path
from typing import Any, Callable

from playwright.sync_api import Page, TimeoutError as PWTimeout


class StepError(Exception):
    pass


class Context:
    def __init__(self, page: Page, config: dict, scenario, output_dir: Path, log: Callable[[str], None]):
        self.page = page
        self.config = config
        self.scenario = scenario
        self.output_dir = output_dir
        self.log = log
        # Report month defaults to the previous calendar month; set_date overrides it with the
        # month actually selected (e.g. Topology Stock has no date filter).
        last = dt.date.today().replace(day=1) - dt.timedelta(days=1)
        self.vars: dict[str, Any] = {"year": last.year, "month": last.month, "month2": f"{last.month:02d}",
                                     **scenario.vars}
        self.downloads: list[dict] = []

    @property
    def sel(self) -> dict:
        return self.config["selectors"]

    @property
    def timeouts(self) -> dict:
        return self.config["timeouts"]


STEPS: dict[str, Callable[[Context, Any], None]] = {}


def step(*names: str):
    def deco(fn):
        for n in names:
            STEPS[n] = fn
        return fn
    return deco


# --------------------------------------------------------------------------- helpers

def _url(ctx: Context, path: str) -> str:
    if path.startswith("http://") or path.startswith("https://") or path.startswith("file:"):
        return path
    return ctx.config["base_url"].rstrip("/") + "/" + path.lstrip("/")


def _wait_page_ready(ctx: Context) -> None:
    page = ctx.page
    page.wait_for_load_state("domcontentloaded", timeout=ctx.timeouts["navigation"])
    try:
        page.wait_for_load_state("networkidle", timeout=15_000)
    except PWTimeout:
        pass  # dashboards may keep long-polling; not fatal


def _as_list(v: Any) -> list:
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def _text_regex(text: str, exact: bool = False) -> re.Pattern:
    body = re.escape(text.strip())
    return re.compile(rf"^\s*{body}\s*$" if exact else body, re.I)


def _date_limits(ctx: Context) -> dict:
    try:
        lim = ctx.page.evaluate("() => (window.dateLimits || null)")
    except Exception:
        lim = None
    out = {}
    for k in ("minDate", "maxDate"):
        if lim and lim.get(k):
            out[k] = dt.datetime.strptime(str(lim[k]), "%Y%m%d").date()
    return out


_REL_RE = re.compile(r"^(today|yesterday|max|min)\s*(?:([+-])\s*(\d+)\s*([dwm]))?$", re.I)


def parse_date(ctx: Context, value: Any) -> dt.date:
    """Accepts 2026-08-31, 31/08/2026, today, today-7d, max, max-30d, min+1m."""
    if isinstance(value, dt.date):
        return value
    s = str(value).strip()
    for fmt in ("%Y-%m-%d", "%d/%m/%Y", "%Y%m%d"):
        try:
            return dt.datetime.strptime(s, fmt).date()
        except ValueError:
            pass
    m = _REL_RE.match(s)
    if not m:
        raise StepError(f"Unrecognized date '{value}'")
    anchor, sign, num, unit = m.groups()
    anchor = anchor.lower()
    today = dt.date.today()
    if anchor == "today":
        base = today
    elif anchor == "yesterday":
        base = today - dt.timedelta(days=1)
    else:
        limits = _date_limits(ctx)
        key = "maxDate" if anchor == "max" else "minDate"
        if key not in limits:
            raise StepError(f"'{anchor}' needs window.dateLimits on the page, which was not found")
        base = limits[key]
    if sign:
        n = int(num) * (-1 if sign == "-" else 1)
        if unit.lower() == "d":
            base += dt.timedelta(days=n)
        elif unit.lower() == "w":
            base += dt.timedelta(weeks=n)
        else:
            month = base.month - 1 + n
            year = base.year + month // 12
            month = month % 12 + 1
            import calendar
            base = base.replace(year=year, month=month, day=min(base.day, calendar.monthrange(year, month)[1]))
    return base


def _click_update_if_visible(ctx: Context) -> bool:
    btn = ctx.page.locator(ctx.sel["update_button"])
    try:
        if btn.count() and btn.first.is_visible():
            ctx.log("  'Parameters changed' button visible -> clicking it")
            btn.first.click()
            return True
    except Exception:
        pass
    return False


def _check_error(ctx: Context) -> None:
    err = ctx.page.locator(ctx.sel["error_message"])
    try:
        if err.count() and err.first.is_visible():
            text = err.first.inner_text().strip()
            if text:
                raise StepError(f"Dashboard shows an error: {text}")
    except StepError:
        raise
    except Exception:
        pass


# --------------------------------------------------------------------------- popups

def popup_locator(page: Page, config: dict):
    pc = config["popups"]
    ignore = "".join(f":not({i})" for i in pc.get("ignore") or [])
    parts = [p.strip() for p in pc["selector"].split(",") if p.strip()]
    return page.locator(", ".join(p + ignore for p in parts))


_dismissing: set[int] = set()  # pages currently closing a popup (the click would re-trigger the handler)


def dismiss_popups(page: Page, config: dict, log: Callable[[str], None]) -> int:
    """Close visible announcement popups. Returns how many were closed."""
    if id(page) in _dismissing:
        return 0
    _dismissing.add(id(page))
    try:
        pc = config["popups"]
        loc = popup_locator(page, config)
        closed = 0
        for _ in range(5):  # a popup may be followed by another one
            popups = _visible(loc)
            if not popups:
                break
            el = popups[0]
            try:
                title = " ".join(el.inner_text(timeout=1000).split())[:70]
            except Exception:
                title = "?"
            how = _close_popup(page, el, pc)
            log(f"  closed popup '{title}' ({how})")
            closed += 1
        return closed
    finally:
        _dismissing.discard(id(page))


def _close_popup(page: Page, el, pc: dict) -> str:
    def gone() -> bool:
        try:
            el.wait_for(state="hidden", timeout=2000)
            return True
        except Exception:
            return False

    buttons = _visible(el.locator(pc["close_selector"]))
    if not buttons:
        names = "|".join(re.escape(t) for t in pc["close_texts"])
        buttons = _visible(el.get_by_role("button", name=re.compile(rf"^\s*(?:{names})\s*$", re.I)))
    if buttons:
        try:
            buttons[0].click(timeout=3000)
            if gone():
                return "close button"
        except Exception:
            pass
    page.keyboard.press("Escape")
    if gone():
        return "Escape"
    # Last resort: remove the dialog and any backdrop that still blocks the page.
    el.evaluate("""(el) => {
        (el.closest('.modal, .p-dialog-mask, .swal2-container') || el).remove();
        document.querySelectorAll('.modal-backdrop, .p-dialog-mask, .swal2-container').forEach(b => b.remove());
        document.body.classList.remove('modal-open', 'swal2-shown');
        document.body.style.removeProperty('overflow');
        document.body.style.removeProperty('padding-right');
    }""")
    return "removed"


def install_popup_handler(page: Page, config: dict, log: Callable[[str], None]) -> None:
    """Let Playwright close popups automatically whenever one blocks an action."""
    if not config["popups"].get("auto_dismiss", True):
        return
    try:
        # no_wait_after: when the tool itself is clicking the popup's close button the handler
        # does nothing, and Playwright must not wait for the popup to disappear first.
        page.add_locator_handler(popup_locator(page, config),
                                 lambda *_: dismiss_popups(page, config, log), no_wait_after=True)
    except Exception as e:  # older Playwright without add_locator_handler
        log(f"  (automatic popup handler unavailable: {e}; popups are still closed before each step)")


@step("dismiss_popups", "close_popups")
def dismiss_popups_step(ctx: Context, args: Any = None) -> None:
    """Close announcement popups (runs automatically before each step)."""
    if not dismiss_popups(ctx.page, ctx.config, ctx.log):
        ctx.log("  no popup")


# --------------------------------------------------------------------------- navigation

@step("goto")
def goto(ctx: Context, args: Any) -> None:
    path = args if isinstance(args, str) else args["url"]
    ctx.log(f"  goto {_url(ctx, path)}")
    ctx.page.goto(_url(ctx, path), timeout=ctx.timeouts["navigation"], wait_until="domcontentloaded")
    _wait_page_ready(ctx)


@step("open_menu", "menu")
def open_menu(ctx: Context, args: Any) -> None:
    """Navigate via the sidebar. args: "Coverage time" | ["Latency", "Latency Mobile (Cellular)"] | "/app/bi/signal"."""
    items = _as_list(args)
    if len(items) == 1 and str(items[0]).startswith("/"):
        return goto(ctx, items[0])
    page = ctx.page
    sidebar = page.locator(ctx.sel["sidebar"]).first
    scope = sidebar
    for i, label in enumerate(items):
        last = i == len(items) - 1
        link = scope.locator("a").filter(has_text=_text_regex(str(label), exact=True)).first
        if not link.count():
            raise StepError(f"Menu item '{label}' not found in sidebar")
        href = link.get_attribute("href") or "#"
        ctx.log(f"  menu -> {label}" + ("" if href == "#" else f" ({href})"))
        if last and href not in ("#", ""):
            with page.expect_navigation(timeout=ctx.timeouts["navigation"], wait_until="domcontentloaded"):
                link.click()
            _wait_page_ready(ctx)
        else:
            link.click()
            page.wait_for_timeout(400)  # metisMenu expand animation
            # Next level lives inside this item's submenu.
            scope = link.locator("xpath=..")


@step("select_country", "country")
def select_country(ctx: Context, args: Any) -> None:
    """Select country by name ("Cambodia") or code ("kh")."""
    wanted = args if isinstance(args, str) else args["name"]
    page = ctx.page
    sel = ctx.sel["country_select"]
    page.wait_for_selector(sel, state="attached", timeout=ctx.timeouts["default"])
    info = page.evaluate(
        """([sel, wanted]) => {
            const el = document.querySelector(sel);
            const opts = Array.from(el.options);
            const w = String(wanted).trim().toLowerCase();
            const o = opts.find(o => o.value.toLowerCase() === w || o.text.trim().toLowerCase() === w);
            return {current: el.value, match: o ? o.value : null,
                    label: o ? o.text.trim() : null, available: opts.map(o => o.text.trim())};
        }""",
        [sel, wanted],
    )
    if not info["match"]:
        raise StepError(f"Country '{wanted}' not available. Options: {info['available']}")
    ctx.vars["country"] = info["label"]
    ctx.vars["country_code"] = info["match"]
    if info["current"] == info["match"]:
        ctx.log(f"  country already {info['label']}")
        return
    ctx.log(f"  country -> {info['label']}")
    url_before = page.url
    _set_select_values(ctx, sel, [info["match"]])
    # Switching country usually reloads the dashboard.
    try:
        page.wait_for_url(lambda u: u != url_before, timeout=10_000)
    except PWTimeout:
        pass
    _wait_page_ready(ctx)


# --------------------------------------------------------------------------- filters

_SET_SELECT_JS = """
([sel, wanted, clear]) => {
    const el = document.querySelector(sel);
    if (!el) return {error: 'not found'};
    const opts = Array.from(el.options);
    const norm = s => String(s).trim().toLowerCase();
    const want = wanted.map(norm);
    const hit = o => want.includes(norm(o.value)) || want.includes(norm(o.text));
    const missing = wanted.filter(w => !opts.some(o => norm(o.value) === norm(w) || norm(o.text) === norm(w)));
    if (missing.length) return {missing, available: opts.map(o => o.text.trim())};
    if (el.multiple) {
        opts.forEach(o => { if (hit(o)) o.selected = true; else if (clear) o.selected = false; });
    } else {
        const o = opts.find(hit); if (o) el.value = o.value;
    }
    const $ = window.jQuery;
    if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
    el.dispatchEvent(new Event('input', {bubbles: true}));
    el.dispatchEvent(new Event('change', {bubbles: true}));
    if ($) { try { $(el).trigger('changed.bs.select'); } catch (e) {} }
    return {selected: opts.filter(o => o.selected).map(o => o.text.trim())};
}
"""


def _set_select_values(ctx: Context, css: str, values: list, clear: bool = True, wait_options_ms: int | None = None) -> list[str]:
    page = ctx.page
    deadline = time.time() + (wait_options_ms if wait_options_ms is not None else ctx.timeouts["default"]) / 1000
    while True:
        res = page.evaluate(_SET_SELECT_JS, [css, [str(v) for v in values], clear])
        if "error" in res:
            raise StepError(f"Select '{css}' not found")
        if "missing" not in res:
            return res["selected"]
        # Options are often loaded asynchronously (e.g. geography); retry until timeout.
        if time.time() > deadline:
            raise StepError(f"Options {res['missing']} not found in '{css}'. Available: {res['available'][:50]}")
        page.wait_for_timeout(500)


def _select_ui(ctx: Context, css: str, values: list, clear: bool) -> list[str]:
    """Drive bootstrap-select through clicks, like a user would."""
    page = ctx.page
    wrapper = page.locator(f".bootstrap-select:has({css})").first
    toggle = wrapper.locator(".dropdown-toggle").first
    toggle.click()
    menu = wrapper.locator(".dropdown-menu").first
    menu.wait_for(state="visible", timeout=ctx.timeouts["default"])
    if clear:
        deselect = menu.locator(".bs-deselect-all")
        if deselect.count() and deselect.first.is_visible():
            deselect.first.click()
    for v in values:
        search = menu.locator(".bs-searchbox input")
        if search.count() and search.first.is_visible():
            search.first.fill(str(v))
        item = menu.locator("li a, li .dropdown-item").filter(has_text=_text_regex(str(v), exact=True)).first
        item.click()
        if search.count() and search.first.is_visible():
            search.first.fill("")
    page.keyboard.press("Escape")
    return page.evaluate(
        "(sel) => Array.from(document.querySelector(sel).selectedOptions).map(o => o.text.trim())", css
    )


_SELECT_ALL_JS = """
(sel) => {
    const el = document.querySelector(sel);
    const opts = Array.from(el.options).filter(o => !o.disabled && o.value !== '');
    if (!opts.length) return null;
    opts.forEach(o => o.selected = true);
    const $ = window.jQuery;
    if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
    el.dispatchEvent(new Event('input', {bubbles: true}));
    el.dispatchEvent(new Event('change', {bubbles: true}));
    if ($) { try { $(el).trigger('changed.bs.select'); } catch (e) {} }
    return opts.map(o => o.text.trim());
}
"""


_SELECT_BY_LABEL_JS = """
(wanted) => {
    const norm = s => s.replace(/\\(.*?\\)/g, ' ').replace(/\\s+/g, ' ').trim().toLowerCase();
    const w = norm(wanted);
    const labels = Array.from(document.querySelectorAll('label'))
        .filter(l => !l.closest('.modal') && norm(l.textContent) === w);
    for (const l of labels) {
        let sel = l.htmlFor ? document.getElementById(l.htmlFor) : null;
        if (!sel || sel.tagName !== 'SELECT') {
            const box = l.closest('.form-group, .form-group-sm, .col, div');
            sel = box ? box.querySelector('select') : null;
        }
        if (sel) {
            if (!sel.id) sel.setAttribute('data-weplan-label', wanted);
            return sel.id ? '#' + CSS.escape(sel.id) : `select[data-weplan-label="${wanted}"]`;
        }
    }
    return null;
}
"""


def _select_by_label(ctx: Context, label: str) -> str:
    """CSS selector of the <select> whose label reads `label` (counters like "(1 active)" ignored)."""
    deadline = time.time() + ctx.timeouts["default"] / 1000
    while (css := ctx.page.evaluate(_SELECT_BY_LABEL_JS, label)) is None:
        if time.time() > deadline:
            raise StepError(f"No filter labelled '{label}' found on the page")
        ctx.page.wait_for_timeout(500)
    ctx.log(f"  filter '{label}' -> {css}")
    return css


@step("select_filter", "select", "filter")
def select_filter(ctx: Context, args: dict) -> None:
    """args: {id: carrier_filter | label: "Technology", options: [ECONET], clear: true, mode: js|ui, all: true}"""
    css = args.get("selector") or (f"#{args['id']}" if args.get("id") else _select_by_label(ctx, args["label"]))
    values = [str(v) for v in _as_list(args.get("options", args.get("value")))]
    clear = args.get("clear", True)
    mode = args.get("mode", "js")
    ctx.page.wait_for_selector(css, state="attached", timeout=ctx.timeouts["default"])
    if args.get("all"):
        _select_all(ctx, css, mode)
        return
    if not values:
        ctx.log(f"  clear {css}")
        ctx.page.evaluate(
            """(sel) => { const el = document.querySelector(sel);
                Array.from(el.options).forEach(o => o.selected = false);
                const $ = window.jQuery; if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
                el.dispatchEvent(new Event('change', {bubbles: true})); }""",
            css,
        )
        return
    if mode == "ui":
        selected = _select_ui(ctx, css, values, clear)
    else:
        selected = _set_select_values(ctx, css, values, clear, args.get("wait_options_ms"))
    ctx.log(f"  {css} = {selected}")


def _select_all(ctx: Context, css: str, mode: str) -> None:
    """Select every option ("Select All"). Waits for options that load asynchronously."""
    page = ctx.page
    deadline = time.time() + ctx.timeouts["default"] / 1000
    if mode == "ui":
        wrapper = page.locator(f".bootstrap-select:has({css})").first
        wrapper.locator(".dropdown-toggle").first.click()
        wrapper.locator(".bs-select-all").first.click()
        page.keyboard.press("Escape")
        selected = page.evaluate(
            "(sel) => Array.from(document.querySelector(sel).selectedOptions).map(o => o.text.trim())", css)
    else:
        while (selected := page.evaluate(_SELECT_ALL_JS, css)) is None:
            if time.time() > deadline:
                raise StepError(f"'{css}' has no options to select")
            page.wait_for_timeout(500)
    ctx.log(f"  {css} = all {len(selected)}: {selected}")


@step("filters")
def filters(ctx: Context, args: dict) -> None:
    """Shorthand: {carrier_filter: [ECONET, LUMITEL], coverage_filter: ["4G"]}"""
    for fid, values in args.items():
        select_filter(ctx, {"id": fid, "options": values})


@step("set_date", "date")
def set_date(ctx: Context, args: dict) -> None:
    """args: {from: 2026-08-01, to: 2026-08-31, mode: input|calendar|preset|skip, preset: "Last 30 days"}"""
    dcfg = {**ctx.config["date"], **{k: v for k, v in args.items() if k in ("mode", "input_format", "range_separator")}}
    page = ctx.page
    mode = args.get("mode") or ("preset" if args.get("preset") else dcfg["mode"])
    if mode == "skip":
        ctx.log("  date: skipped, using the dashboard's current range")
        return
    page.wait_for_selector(ctx.sel["datepicker"], state="attached", timeout=ctx.timeouts["default"])

    if mode == "preset":
        ctx.log(f"  date preset -> {args['preset']}")
        before = _date_widget_text(ctx)
        _open_datepicker(ctx)
        items = _visible(page.get_by_text(_text_regex(args["preset"], exact=True)))
        if not items:
            raise StepError(f"Preset '{args['preset']}' not found in the date picker")
        items[0].click()
        _click_apply_if_visible(ctx)
        page.keyboard.press("Escape")
        _read_preset_range(ctx, dcfg, before)
        return

    d_from = parse_date(ctx, args["from"])
    d_to = parse_date(ctx, args.get("to", args["from"]))
    if d_from > d_to:
        raise StepError(f"Date from {d_from} is after to {d_to}")
    limits = _date_limits(ctx)
    if limits.get("minDate") and d_from < limits["minDate"]:
        ctx.log(f"  WARNING: {d_from} < dashboard minDate {limits['minDate']}")
    if limits.get("maxDate") and d_to > limits["maxDate"]:
        ctx.log(f"  WARNING: {d_to} > dashboard maxDate {limits['maxDate']}")
    _set_date_vars(ctx, d_from, d_to)

    fmt = dcfg["input_format"]
    cal = dcfg["calendar"]
    expected = d_from.strftime(fmt) + dcfg["range_separator"] + d_to.strftime(fmt)
    if mode not in ("auto", "input", "calendar"):
        raise StepError(f"Unknown date mode '{mode}'")
    kind, inputs = _detect_date_widget(ctx, allow_calendar=(mode != "input"),
                                       prefer_calendar=(mode == "calendar"))
    if kind == "inputs":
        if len(inputs) >= 2:
            # Separate start / end inputs.
            ctx.log(f"  date -> start '{d_from.strftime(fmt)}', end '{d_to.strftime(fmt)}'")
            _type_into(inputs[0], d_from.strftime(fmt))
            _type_into(inputs[1], d_to.strftime(fmt))
        else:
            ctx.log(f"  date -> '{expected}'")
            _type_into(inputs[0], expected)
    elif kind == "css_calendar":
        ctx.log(f"  date (calendar) -> {d_from} .. {d_to}")
        for d in (d_from, d_to):
            _calendar_pick(ctx, cal, d)
    else:
        ctx.log(f"  date (calendar, by text) -> {d_from} .. {d_to}")
        _text_calendar_pick(ctx, d_from)
        _text_calendar_pick(ctx, d_to)
    _click_apply_if_visible(ctx)
    page.keyboard.press("Escape")
    _verify_date_shown(ctx, expected)


def _set_date_vars(ctx: Context, d_from: dt.date, d_to: dt.date) -> None:
    """Variables for file names: ${date_from} ${date_to} ${year} ${month} (8) ${month2} (08)."""
    ctx.vars.update(date_from=d_from.isoformat(), date_to=d_to.isoformat(),
                    year=d_from.year, month=d_from.month, month2=f"{d_from.month:02d}")


def _read_preset_range(ctx: Context, dcfg: dict, before: str) -> None:
    """After clicking a preset, read the range the widget shows and set the date variables."""
    fmt = dcfg["input_format"]
    pattern = re.compile(r"\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}")
    shown = ""
    for i in range(16):
        shown = _date_widget_text(ctx)
        if shown != before or i >= 4:
            dates = []
            for token in pattern.findall(shown):
                try:
                    dates.append(dt.datetime.strptime(token, fmt).date())
                except ValueError:
                    pass
            if len(dates) >= 2:
                _set_date_vars(ctx, dates[0], dates[1])
                ctx.log(f"  date widget shows: '{shown}' -> {dates[0]} .. {dates[1]}")
                return
        ctx.page.wait_for_timeout(250)
    raise StepError(f"Could not read the date range after the preset (widget shows '{shown}'). "
                    "Check date.input_format")


_CALENDAR_JS = (Path(__file__).parent / "calendar.js").read_text(encoding="utf-8")


def _text_calendar(ctx: Context, d: dt.date) -> dict:
    return ctx.page.evaluate(_CALENDAR_JS, [d.year, d.month, d.day])


def _text_calendar_pick(ctx: Context, d: dt.date) -> None:
    """Click day `d` in the open calendar popup, navigating months with its arrows."""
    page = ctx.page
    for _ in range(40):
        res = _text_calendar(ctx, d)
        if res.get("error"):
            path = _dump_datepicker(ctx)
            raise StepError(f"Calendar: cannot find {d} ({res['error']}, months shown: {res.get('shown')}). "
                            f"Widget HTML saved to {path}")
        if res.get("nav"):
            if ctx.config["popups"].get("auto_dismiss", True):
                dismiss_popups(page, ctx.config, ctx.log)  # mouse clicks bypass the locator handler
            page.mouse.click(res["x"], res["y"])
            page.wait_for_timeout(300)
            continue
        if res["disabled"]:
            raise StepError(f"Day {d} is not selectable in the calendar (outside the dashboard's date limits?)")
        if ctx.config["popups"].get("auto_dismiss", True) and dismiss_popups(page, ctx.config, ctx.log):
            continue  # a popup covered the calendar; locate the day again
        page.mouse.click(res["x"], res["y"])
        page.wait_for_timeout(300)
        return
    raise StepError(f"Could not navigate the calendar to {d:%B %Y}")


def _click_apply_if_visible(ctx: Context) -> None:
    page = ctx.page
    apply_css = ctx.config["date"]["calendar"].get("apply")
    if apply_css:
        _click_if_visible(page.locator(apply_css).first)
    # Generic Apply/OK only while a calendar popup is still open (the dashboard's closes itself).
    if not (_calendar_open(ctx) or _text_calendar(ctx, dt.date.today()).get("error") != "no-calendar"):
        return
    btn = page.get_by_role("button", name=re.compile(r"^\s*(apply|ok|aplicar|done|select)\s*$", re.I))
    for b in _visible(btn):
        b.click()
        break


def _verify_date_shown(ctx: Context, expected: str) -> None:
    """Fail if the widget ends up showing a different range (avoids exporting the wrong period)."""
    page = ctx.page
    pattern = re.compile(r"\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}\s*-\s*\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}")
    want = re.sub(r"\s+", "", expected)
    shown = ""
    for _ in range(12):
        shown = _date_widget_text(ctx)
        if want in re.sub(r"\s+", "", shown):
            ctx.log(f"  date widget shows: '{shown}'")
            return
        page.wait_for_timeout(250)
    m = pattern.search(shown)
    if m:
        raise StepError(f"Date widget shows '{m.group(0)}' instead of '{expected}'. "
                        "Check date.input_format / the calendar selection")
    ctx.log(f"  WARNING: could not read the selected range from the widget (shows '{shown[:80]}')")


def _date_widget_text(ctx: Context) -> str:
    try:
        return ctx.page.evaluate(
            """(sel) => { const el = document.querySelector(sel); if (!el) return '';
                const vals = Array.from(el.querySelectorAll('input')).map(i => i.value).join(' ');
                return (el.innerText + ' ' + vals).replace(/\\s+/g, ' ').trim(); }""",
            ctx.sel["datepicker"])
    except Exception:
        return ""


def _calendar_open(ctx: Context) -> bool:
    return bool(_visible(ctx.page.locator(ctx.config["date"]["calendar"]["title"])))


def _visible(loc) -> list:
    out = []
    for i in range(loc.count()):
        try:
            if loc.nth(i).is_visible():
                out.append(loc.nth(i))
        except Exception:
            pass
    return out


def _click_if_visible(loc) -> None:
    try:
        if loc.count() and loc.is_visible():
            loc.click()
    except Exception:
        pass


def _type_into(inp, text: str) -> None:
    inp.click()
    inp.press("Control+A")
    inp.press("Delete")
    inp.type(text, delay=20)
    inp.press("Enter")


def _open_datepicker(ctx: Context) -> None:
    """Click whatever the date widget renders (input, button or text)."""
    page = ctx.page
    inside = _visible(page.locator(ctx.sel["datepicker_input"]))
    if inside:
        inside[0].click()
    else:
        root = page.locator(ctx.sel["datepicker"]).first
        clickable = _visible(root.locator("input, button, [role=button], [tabindex], .form-control, div, span"))
        (clickable[0] if clickable else root).click()
    page.wait_for_timeout(800)


def _detect_date_widget(ctx: Context, allow_calendar: bool, prefer_calendar: bool = False):
    """Returns ("inputs", [locators]) | ("css_calendar", None) | ("text_calendar", None)."""
    page = ctx.page
    if not prefer_calendar:
        if page.locator(ctx.sel["datepicker_input"]).count():
            try:
                page.locator(ctx.sel["datepicker_input"]).first.wait_for(state="visible", timeout=5_000)
            except PWTimeout:
                pass
        inputs = _visible(page.locator(ctx.sel["datepicker_input"]))
        if inputs:
            return "inputs", inputs
        ctx.log("  no <input> inside the date widget -> clicking it to open the picker")
    _open_datepicker(ctx)
    if not prefer_calendar:
        inputs = _visible(page.locator(ctx.config["date"]["popup_inputs"]))
        if inputs:
            return "inputs", inputs
    if allow_calendar:
        if _calendar_open(ctx):
            return "css_calendar", None
        probe = _text_calendar(ctx, dt.date.today())
        if probe.get("error") != "no-calendar":
            ctx.log(f"  calendar popup found (months shown: {', '.join(probe.get('shown', []))})")
            return "text_calendar", None
    path = _dump_datepicker(ctx)
    raise StepError(
        "Could not find a date input or calendar. The date widget's HTML was saved to "
        f"{path}. Send that file to adjust selectors, or use `set_date: {{mode: skip}}` "
        "to keep the dashboard's default range."
    )


def _dump_datepicker(ctx: Context) -> Path:
    page = ctx.page
    html = page.evaluate(
        """(sel) => {
            const parts = [];
            const root = document.querySelector(sel);
            parts.push('<!-- ' + sel + ' -->\\n' + (root ? root.outerHTML : 'NOT FOUND'));
            // Popups are often appended to <body>; keep the visible, recently shown ones.
            for (const el of document.body.children) {
                const r = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (r.width > 0 && r.height > 0 && style.display !== 'none' && style.visibility !== 'hidden'
                    && /picker|calendar|popover|dropdown|dialog|overlay|menu/i.test(el.className + ' ' + el.id)) {
                    parts.push('<!-- body > ' + el.tagName + '.' + el.className + ' -->\\n' + el.outerHTML);
                }
            }
            return parts.join('\\n\\n');
        }""",
        ctx.sel["datepicker"],
    )
    path = ctx.output_dir / "_debug" / f"datepicker_{_safe(ctx.scenario.name)}.html"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(html, encoding="utf-8")
    ctx.page.screenshot(path=str(path.with_suffix(".png")), full_page=True)
    return path


def _parse_month_title(title: str) -> dt.date | None:
    title = " ".join(title.split()).title()
    for fmt in ("%B %Y", "%b %Y", "%m/%Y", "%Y-%m"):
        try:
            return dt.datetime.strptime(title, fmt).date().replace(day=1)
        except ValueError:
            continue
    return None


def _calendar_pick(ctx: Context, cal: dict, d: dt.date) -> None:
    page = ctx.page
    target = d.replace(day=1)
    for _ in range(40):
        titles = _visible(page.locator(cal["title"]))
        if not titles:
            raise StepError("Calendar popup is not open (date.calendar.title matched nothing visible)")
        raw = titles[0].inner_text()
        shown = _parse_month_title(raw)
        if shown is None:
            raise StepError(f"Cannot read calendar month from '{raw.strip()}'")
        if shown == target:
            break
        nav = _visible(page.locator(cal["next"] if shown < target else cal["prev"]))
        if not nav:
            raise StepError(f"Cannot move calendar from {shown:%b %Y} to {target:%b %Y} (outside date limits?)")
        nav[0].click()
        page.wait_for_timeout(200)
    else:
        raise StepError(f"Could not navigate calendar to {target:%B %Y}")
    day = page.locator(cal["day"]).filter(has_text=_text_regex(str(d.day), exact=True))
    if not _visible(day):
        raise StepError(f"Day {d} is not selectable in the calendar (outside the dashboard's date limits?)")
    _visible(day)[0].click()
    page.wait_for_timeout(200)


# --------------------------------------------------------------------------- query / results

@step("apply", "run_query")
def apply(ctx: Context, args: Any = None) -> None:
    if not _click_update_if_visible(ctx):
        ctx.log("  nothing to apply")


@step("choose_view", "view")
def choose_view(ctx: Context, args: Any) -> None:
    """Click a visualization card: macro | population_range | admin_1 | ... | '#byCountry' | {text: 'Macro data'}."""
    page = ctx.page
    _click_update_if_visible(ctx)
    if isinstance(args, dict) and "text" in args:
        loc = page.locator(".chartModeSelector").filter(has_text=_text_regex(args["text"], exact=False)).first
        label = args["text"]
    else:
        key = args if isinstance(args, str) else args["view"]
        css = ctx.config["views"].get(key, key)
        if not css.startswith(("#", ".", "[")):
            loc = page.locator(".chartModeSelector").filter(has_text=_text_regex(key)).first
        else:
            loc = page.locator(css).first
        label = key
    try:
        loc.wait_for(state="visible", timeout=15_000)
    except PWTimeout:
        # The dashboard may already show results (e.g. restored last query): the cards are hidden then.
        if page.locator(ctx.sel["results"]).first.is_visible():
            ctx.log(f"  view cards hidden, results already shown -> keeping current view (wanted {label})")
            return
        loc.wait_for(state="visible", timeout=ctx.timeouts["data_load"])
    ctx.log(f"  view -> {label}")
    loc.click()
    if isinstance(args, dict) and args.get("municipality"):
        select_filter(ctx, {"id": "municipalitySelect", "options": [args["municipality"]]})


@step("wait_for_table", "wait_data")
def wait_for_table(ctx: Context, args: Any = None) -> None:
    """Wait until #results is shown and the table has rows (or stays empty: no data for the filters)."""
    args = args or {}
    page = ctx.page
    timeout = args.get("timeout", ctx.timeouts["data_load"])
    min_rows = args.get("min_rows", 1)
    # Results shown, nothing loading and still no rows after this long -> the table is empty
    # (e.g. no 5G measurements in a market). 0 disables it.
    empty_after = args.get("empty_after_ms", 20_000) / 1000
    deadline = time.time() + timeout / 1000
    rows_sel = args.get("rows", ctx.sel["table_rows"])
    t0 = time.time()
    empty_since = None
    while True:
        _check_error(ctx)
        results_visible = page.locator(ctx.sel["results"]).first.is_visible()
        rows = page.locator(rows_sel).count() if results_visible else 0
        loading = False
        if ctx.sel.get("loading"):
            try:
                loading = page.locator(ctx.sel["loading"]).count() > 0
            except Exception:
                loading = False
        if results_visible and rows >= min_rows and not loading:
            break
        if results_visible and rows == 0 and not loading and empty_after:
            empty_since = empty_since or time.time()
            if time.time() - empty_since >= empty_after:
                ctx.vars["rows"] = 0
                ctx.log(f"  WARNING: table is empty (no data for these filters) after {time.time()-t0:.1f}s")
                return
        else:
            empty_since = None
        if time.time() > deadline:
            raise StepError(f"Timed out after {timeout/1000:.0f}s waiting for table "
                            f"(results visible={results_visible}, rows={rows}, loading={loading})")
        page.wait_for_timeout(1000)
    # Let the table finish rendering.
    page.wait_for_timeout(args.get("settle_ms", 1500))
    ctx.vars["rows"] = page.locator(rows_sel).count()
    ctx.log(f"  table ready: {ctx.vars['rows']} rows ({time.time()-t0:.1f}s)")


# --------------------------------------------------------------------------- download

# Menu items of "Download table": As XLSX / As JSON / As CSV / As PDF / As TXT / As PNG.
_FORMAT_TEXT = {
    "xlsx": r"^\s*As XLSX\s*$|excel|xlsx",
    "xls": r"^\s*As XLS\s*$|excel",
    "csv": r"^\s*As CSV\s*$",
    "json": r"^\s*As JSON\s*$",
    "pdf": r"^\s*As PDF\s*$",
    "txt": r"^\s*As TXT\s*$",
    "png": r"^\s*As PNG\s*$",
}


@step("download_table", "download")
def download_table(ctx: Context, args: Any = None) -> None:
    """Click "Download table", optionally choose file type, save the file.

    args:
      format: xlsx | csv | ...           file type to pick in the menu (if any)
      format_text: "Excel"               exact text of the menu item (overrides format)
      format_select: {selector, option}  a <select> to set before clicking
      button_text: "Download table"
      filename: "${scenario}_${country}_${date_from}_${date_to}"   (extension added automatically)
      verify: true                        open xlsx/csv and check it has rows
    """
    args = args or {}
    if isinstance(args, str):
        args = {"format": args}
    page = ctx.page
    scope = page.locator(args.get("container", ctx.sel["table_container"])).first
    button_text = args.get("button_text", ctx.sel["download_button_text"])
    fmt = (args.get("format") or "").lower()

    if args.get("format_select"):
        fs = args["format_select"]
        _set_select_values(ctx, fs["selector"], [fs["option"]])

    btn = scope.locator("button, a, [role=button]").filter(has_text=_text_regex(button_text)).first
    if not btn.count():
        # Fall back to the whole page (button may be rendered in a toolbar outside the container).
        btn = page.locator("button, a, [role=button]").filter(has_text=_text_regex(button_text)).first
    btn.wait_for(state="visible", timeout=ctx.timeouts["default"])
    btn.scroll_into_view_if_needed()

    option_re = None
    if args.get("format_text"):
        option_re = _text_regex(args["format_text"])
    elif fmt:
        option_re = re.compile(_FORMAT_TEXT.get(fmt, re.escape(fmt)), re.I)

    ctx.log(f"  click '{button_text}'" + (f" -> {option_re.pattern}" if option_re else ""))
    with page.expect_download(timeout=ctx.timeouts["download"]) as dl_info:
        btn.click()
        if option_re is not None:
            _click_format_option(ctx, btn, option_re)
    download = dl_info.value

    suggested = download.suggested_filename or "table"
    ext = Path(suggested).suffix or (f".{fmt}" if fmt else "")
    ctx.vars.setdefault("date_from", "")
    ctx.vars.setdefault("date_to", "")
    ctx.vars["timestamp"] = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    template = args.get("filename")
    name = _render_filename(template, ctx.vars) if template else f"{_safe(ctx.scenario.name)}_{ctx.vars['timestamp']}"
    if not name.lower().endswith(ext.lower()):
        name += ext
    target = ctx.output_dir / name
    target.parent.mkdir(parents=True, exist_ok=True)
    download.save_as(str(target))
    failure = download.failure()
    if failure:
        raise StepError(f"Download failed: {failure}")
    size = target.stat().st_size
    ctx.log(f"  saved {target} ({size:,} bytes, server name '{suggested}')")
    info = {"file": str(target), "bytes": size, "suggested": suggested}
    if args.get("verify", True) and ctx.vars.get("rows") != 0:
        info["data_rows"] = _verify_file(target)
        ctx.log(f"  verified: {info['data_rows']} data rows")
    ctx.downloads.append(info)


def _click_format_option(ctx: Context, btn, option_re: re.Pattern) -> None:
    """After clicking the download button, pick the file-type item if a menu/modal appears."""
    page = ctx.page
    candidates = page.locator(
        ".dropdown-menu.show a, .dropdown-menu.show button, .dropdown-menu.show li, "
        ".p-menu a, .p-menuitem-link, .p-tieredmenu a, .modal.show button, .modal.show a, "
        "[role=menuitem], [role=option], button, a, label"
    ).filter(has_text=option_re)
    deadline = time.time() + 5
    while time.time() < deadline:
        n = candidates.count()
        for i in range(n):
            c = candidates.nth(i)
            try:
                if c.is_visible() and c.inner_text().strip().lower() != btn.inner_text().strip().lower():
                    c.click()
                    return
            except Exception:
                continue
        page.wait_for_timeout(250)
    ctx.log("  (no file-type menu appeared; assuming the button downloads directly)")


def _safe(s: str) -> str:
    return re.sub(r"[^\w.\-]+", "_", s).strip("_")


def _safe_filename(s: str) -> str:
    """Keep spaces and letters; drop only characters Windows does not allow in file names."""
    s = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', "_", s)
    return s.strip().rstrip(".")


def _render_filename(template: str, variables: dict) -> str:
    from .config import render
    raw = render(template, {k: (_safe_filename(str(v)) if not isinstance(v, dict) else v)
                            for k, v in variables.items()})
    return "/".join(_safe_filename(part) for part in str(raw).split("/"))


def _verify_file(path: Path) -> int:
    suffix = path.suffix.lower()
    if path.stat().st_size == 0:
        raise StepError(f"Downloaded file {path} is empty")
    if suffix in (".xlsx", ".xlsm"):
        import openpyxl
        wb = openpyxl.load_workbook(path, read_only=True)
        rows = sum(max(ws.max_row - 1, 0) for ws in wb.worksheets)
        wb.close()
    elif suffix in (".csv", ".txt"):
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            rows = max(sum(1 for line in f if line.strip()) - 1, 0)
    else:
        return -1
    if rows <= 0:
        raise StepError(f"Downloaded file {path} has no data rows")
    return rows


# --------------------------------------------------------------------------- generic steps

@step("click")
def click(ctx: Context, args: Any) -> None:
    """args: "#css" | {selector: ".x"} | {text: "Macro data"} | {role: button, name: "OK"}"""
    page = ctx.page
    if isinstance(args, str):
        args = {"selector": args}
    if "text" in args:
        loc = page.get_by_text(args["text"], exact=args.get("exact", False))
    elif "role" in args:
        loc = page.get_by_role(args["role"], name=args.get("name"))
    else:
        loc = page.locator(args["selector"])
    loc = loc.nth(args.get("index", 0))
    ctx.log(f"  click {args}")
    loc.click(timeout=args.get("timeout", ctx.timeouts["default"]))


@step("fill")
def fill(ctx: Context, args: dict) -> None:
    ctx.page.locator(args["selector"]).first.fill(str(args["value"]))


@step("press")
def press(ctx: Context, args: Any) -> None:
    ctx.page.keyboard.press(args if isinstance(args, str) else args["key"])


@step("wait")
def wait(ctx: Context, args: Any) -> None:
    ms = args if isinstance(args, (int, float)) else args.get("ms", 1000)
    ctx.page.wait_for_timeout(ms)


@step("wait_for")
def wait_for(ctx: Context, args: Any) -> None:
    if isinstance(args, str):
        args = {"selector": args}
    ctx.page.locator(args["selector"]).first.wait_for(
        state=args.get("state", "visible"), timeout=args.get("timeout", ctx.timeouts["data_load"])
    )


@step("screenshot")
def screenshot(ctx: Context, args: Any = None) -> None:
    name = (args if isinstance(args, str) else (args or {}).get("name")) or "screenshot"
    path = ctx.output_dir / "screenshots" / f"{_safe(ctx.scenario.name)}_{_safe(name)}.png"
    path.parent.mkdir(parents=True, exist_ok=True)
    ctx.page.screenshot(path=str(path), full_page=True)
    ctx.log(f"  screenshot {path}")


@step("evaluate", "js")
def evaluate(ctx: Context, args: Any) -> None:
    result = ctx.page.evaluate(args if isinstance(args, str) else args["script"])
    ctx.log(f"  js -> {result!r}")


@step("pause")
def pause(ctx: Context, args: Any = None) -> None:
    """Open Playwright Inspector (only useful with --headed)."""
    ctx.page.pause()


@step("set_var")
def set_var(ctx: Context, args: dict) -> None:
    ctx.vars.update(args)
