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
        self.vars: dict[str, Any] = dict(scenario.vars)
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


@step("select_filter", "select", "filter")
def select_filter(ctx: Context, args: dict) -> None:
    """args: {id: carrier_filter, options: [ECONET], clear: true, mode: js|ui}"""
    css = args.get("selector") or f"#{args['id']}"
    values = [str(v) for v in _as_list(args.get("options", args.get("value")))]
    clear = args.get("clear", True)
    mode = args.get("mode", "js")
    ctx.page.wait_for_selector(css, state="attached", timeout=ctx.timeouts["default"])
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


@step("filters")
def filters(ctx: Context, args: dict) -> None:
    """Shorthand: {carrier_filter: [ECONET, LUMITEL], coverage_filter: ["4G"]}"""
    for fid, values in args.items():
        select_filter(ctx, {"id": fid, "options": values})


@step("set_date", "date")
def set_date(ctx: Context, args: dict) -> None:
    """args: {from: 2026-08-01, to: 2026-08-31, mode: input|calendar|preset, preset: "Last 30 days"}"""
    dcfg = {**ctx.config["date"], **{k: v for k, v in args.items() if k in ("mode", "input_format", "range_separator")}}
    page = ctx.page
    mode = args.get("mode") or ("preset" if args.get("preset") else dcfg["mode"])
    page.wait_for_selector(ctx.sel["datepicker"], state="attached", timeout=ctx.timeouts["default"])

    if mode == "preset":
        ctx.log(f"  date preset -> {args['preset']}")
        page.locator(ctx.sel["datepicker_input"]).first.click()
        page.get_by_text(_text_regex(args["preset"], exact=True)).first.click()
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
    ctx.vars["date_from"] = d_from.isoformat()
    ctx.vars["date_to"] = d_to.isoformat()

    inp = page.locator(ctx.sel["datepicker_input"]).first
    if mode == "input":
        fmt = dcfg["input_format"]
        text = d_from.strftime(fmt) + dcfg["range_separator"] + d_to.strftime(fmt)
        ctx.log(f"  date -> '{text}'")
        inp.click()
        inp.press("Control+A")
        inp.press("Delete")
        inp.type(text, delay=20)
        inp.press("Enter")
        page.keyboard.press("Escape")
        page.wait_for_timeout(300)
        value = inp.input_value()
        if value.replace(" ", "") != text.replace(" ", ""):
            ctx.log(f"  WARNING: date input now shows '{value}' (expected '{text}'). "
                    "Check date.input_format in config.yaml or use mode: calendar")
    elif mode == "calendar":
        cal = dcfg["calendar"]
        ctx.log(f"  date (calendar) -> {d_from} .. {d_to}")
        inp.click()
        for d in (d_from, d_to):
            _calendar_pick(ctx, cal, d)
        if cal.get("apply"):
            page.locator(cal["apply"]).first.click()
        page.keyboard.press("Escape")
    else:
        raise StepError(f"Unknown date mode '{mode}'")


def _calendar_pick(ctx: Context, cal: dict, d: dt.date) -> None:
    page = ctx.page
    target = d.strftime("%B %Y").lower()
    for _ in range(36):
        title = page.locator(cal["title"]).first.inner_text().lower()
        title = " ".join(title.split())
        if d.strftime("%B").lower() in title and str(d.year) in title:
            break
        # Compare month/year to decide direction.
        shown = None
        for fmt in ("%B %Y", "%b %Y"):
            try:
                shown = dt.datetime.strptime(title.title(), fmt).date()
                break
            except ValueError:
                continue
        go_next = shown is None or shown < d.replace(day=1)
        page.locator(cal["next"] if go_next else cal["prev"]).first.click()
        page.wait_for_timeout(150)
    else:
        raise StepError(f"Could not navigate calendar to {target}")
    page.locator(cal["day"]).filter(has_text=_text_regex(str(d.day), exact=True)).first.click()


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
    loc.wait_for(state="visible", timeout=ctx.timeouts["data_load"])
    ctx.log(f"  view -> {label}")
    loc.click()
    if isinstance(args, dict) and args.get("municipality"):
        select_filter(ctx, {"id": "municipalitySelect", "options": [args["municipality"]]})


@step("wait_for_table", "wait_data")
def wait_for_table(ctx: Context, args: Any = None) -> None:
    """Wait until #results is shown and the table has rows."""
    args = args or {}
    page = ctx.page
    timeout = args.get("timeout", ctx.timeouts["data_load"])
    min_rows = args.get("min_rows", 1)
    deadline = time.time() + timeout / 1000
    rows_sel = args.get("rows", ctx.sel["table_rows"])
    t0 = time.time()
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
        if time.time() > deadline:
            raise StepError(f"Timed out after {timeout/1000:.0f}s waiting for table "
                            f"(results visible={results_visible}, rows={rows}, loading={loading})")
        page.wait_for_timeout(1000)
    # Let the table finish rendering.
    page.wait_for_timeout(args.get("settle_ms", 1500))
    ctx.vars["rows"] = page.locator(rows_sel).count()
    ctx.log(f"  table ready: {ctx.vars['rows']} rows ({time.time()-t0:.1f}s)")


# --------------------------------------------------------------------------- download

_FORMAT_TEXT = {
    "xlsx": r"excel|xlsx",
    "xls": r"excel|xls",
    "csv": r"csv",
    "json": r"json",
    "pdf": r"pdf",
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
    if args.get("verify", True):
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


def _render_filename(template: str, variables: dict) -> str:
    from string import Template
    raw = Template(template).safe_substitute({k: _safe(str(v)) for k, v in variables.items()})
    return "/".join(_safe(part) for part in raw.split("/"))


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
