"""Loading and merging of config.yaml and scenario files."""
from __future__ import annotations

import copy
import fnmatch
import itertools
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

DEFAULT_CONFIG: dict[str, Any] = {
    "base_url": "https://dashboard.weplananalytics.com",
    # page:   one page (KPI file) for every market, then the next page (file order).
    # market: every page for one market, then the next market.
    "run_order": "page",
    "start_path": "/app/bi/coverage",
    "output_dir": "downloads",
    "auth": {
        "required": True,
        "storage_state": ".auth/state.json",
        # A URL containing one of these means the session expired.
        "login_url_markers": ["login", "signin", "auth"],
        "username_env": "WEPLAN_USERNAME",
        "password_env": "WEPLAN_PASSWORD",
        "login_path": "/",
        "username_selector": "input[type=email], input[name=email], input[name=username]",
        "password_selector": "input[type=password]",
        "submit_selector": "button[type=submit], input[type=submit]",
    },
    "browser": {
        "headless": True,
        # False: all scenarios share one page. True: fresh context per scenario (--isolated).
        "isolated": False,
        "channel": None,  # "chrome" / "msedge" to use an installed browser
        "executable_path": None,
        "slow_mo": 0,
        "viewport": {"width": 1600, "height": 1000},
        "locale": "en-US",
    },
    "timeouts": {
        "default": 30_000,
        "navigation": 90_000,
        "data_load": 300_000,
        "download": 180_000,
    },
    "selectors": {
        "logged_in_marker": "aside.dash-sidebar, #dash",
        "sidebar": "aside.dash-sidebar",
        "country_select": "#dropdownCountryChooser",
        "datepicker": "#datepicker",
        "datepicker_input": "#datepicker input",
        "results": "#results",
        "table_container": "#tableProvinces",
        "table_rows": "#tableProvinces table tbody tr",
        "loading": ".panelLoader:visible, .graphChooserLoader:visible, .p-datatable-loading-overlay, .dash-table-loading",
        "update_button": "#updateButton",
        "error_message": "#errors_row #error_message",
        "download_button_text": "Download table",
    },
    "date": {
        # auto    : input if the widget has one, else inputs in its popup, else click the calendar.
        # input   : type "01-09-2026 - 25-09-2026" into the date input.
        # calendar: click the start and end day in the popup calendar.
        "mode": "auto",
        "input_format": "%d-%m-%Y",  # the dashboard shows 01-09-2026 - 25-09-2026
        "range_separator": " - ",
        # Inputs looked for in the popup when #datepicker itself has no <input>.
        "popup_inputs": ".daterangepicker input, .p-datepicker input, .dp__menu input, .mx-datepicker-main input, "
                        ".vc-popover-content input, .flatpickr-calendar input, [role=dialog] input, .dropdown-menu.show input",
        # Defaults cover daterangepicker / vue2-daterange-picker (two months + Apply) and PrimeVue.
        "calendar": {
            "title": ".daterangepicker .drp-calendar.left .month, .daterangepicker .calendar.left .month, "
                     ".p-datepicker-title, .p-datepicker-header",
            "prev": ".daterangepicker .prev.available, .p-datepicker-prev, .p-datepicker-prev-button",
            "next": ".daterangepicker .next.available, .p-datepicker-next, .p-datepicker-next-button",
            "day": ".daterangepicker .drp-calendar.left td.available:not(.off), "
                   ".daterangepicker .calendar.left td.available:not(.off), "
                   "td:not(.p-datepicker-other-month):not(.p-datepicker-day-cell-other-month) > span",
            "apply": ".daterangepicker .applyBtn, .drp-buttons .applyBtn",
        },
    },
    # Announcement popups (e.g. "What's New: New Delta Analysis in Map View") that block the
    # page are closed automatically before actions and before every step.
    "popups": {
        "auto_dismiss": True,
        "selector": "#changelogAnnouncer .modal.show, #changelogAnnouncer [role=dialog], "
                    "#changelogAnnouncer .p-dialog, .modal.show, [role=dialog][aria-modal=true], "
                    ".p-dialog-mask .p-dialog, .swal2-popup",
        # Dialogs a scenario may open on purpose; never auto-closed.
        "ignore": ["#locationSourceModal", "#user_preferences_modal", "#user_account_modal"],
        "close_selector": ".btn-close, [aria-label='Close' i], [data-bs-dismiss=modal], "
                          ".p-dialog-header-close, .swal2-close, .close",
        "close_texts": ["Close", "Got it", "OK", "Okay", "Dismiss", "Skip", "Later", "Not now",
                        "Understood", "Continue", "Cerrar", "Entendido", "Aceptar"],
    },
    # Aliases for the visualization cards ("Select a visualization mode").
    "views": {
        "macro": "#byCountry",
        "population_range": "#byRanges",
        "admin_1": "#byRegions",
        "admin_2": "#byProvinces",
        "admin_3": "#byMunicipality",
    },
}


def deep_merge(base: dict, override: dict | None) -> dict:
    out = copy.deepcopy(base)
    for k, v in (override or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = deep_merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def load_config(path: str | Path | None) -> dict:
    data: dict = {}
    if path and Path(path).exists():
        data = yaml.safe_load(Path(path).read_text(encoding="utf-8")) or {}
    return deep_merge(DEFAULT_CONFIG, data)


@dataclass
class Scenario:
    name: str
    steps: list[dict]
    vars: dict[str, Any] = field(default_factory=dict)
    source: str = ""
    tags: list[str] = field(default_factory=list)


_VAR_RE = re.compile(r"\$\{([\w.]+)\}")
_MISSING = object()


def lookup(variables: dict[str, Any], name: str) -> Any:
    """Resolve "a" or a dotted path "a.b" (matrix values may be mappings)."""
    value: Any = variables
    for part in name.split("."):
        if isinstance(value, dict) and part in value:
            value = value[part]
        else:
            return _MISSING
    return value


def _to_text(v: Any) -> str:
    if isinstance(v, list):
        return ", ".join(_to_text(x) for x in v)
    return str(v)


def render(value: Any, variables: dict[str, Any]) -> Any:
    """Recursively substitute ${var} / ${var.key} in strings. Unknown variables are left as is."""
    if isinstance(value, str):
        # Keep native types (e.g. a list of options) when the whole string is one variable.
        m = _VAR_RE.fullmatch(value)
        if m:
            v = lookup(variables, m.group(1))
            if v is not _MISSING:
                return v

        def sub(match: re.Match) -> str:
            v = lookup(variables, match.group(1))
            return match.group(0) if v is _MISSING else _to_text(v)

        return _VAR_RE.sub(sub, value)
    if isinstance(value, list):
        return [render(v, variables) for v in value]
    if isinstance(value, dict):
        return {k: render(v, variables) for k, v in value.items()}
    return value


def _expand_matrix(matrix: dict[str, list] | None) -> list[dict]:
    if not matrix:
        return [{}]
    keys = list(matrix)
    values = [v if isinstance(v, list) else [v] for v in matrix.values()]
    return [dict(zip(keys, combo)) for combo in itertools.product(*values)]


def load_scenarios(paths: list[str | Path], global_vars: dict[str, Any] | None = None) -> list[Scenario]:
    """Load scenario files. A file may contain `vars`, `before`, `after` and `scenarios`.

    Each scenario may carry its own `vars` and a `matrix`, which produces one
    scenario per combination of values. `global_vars` (config.yaml `vars`) are
    available to every file, e.g. the list of markets: `matrix: {market: "${markets}"}`.
    """
    result: list[Scenario] = []
    for p in paths:
        p = Path(p)
        doc = yaml.safe_load(p.read_text(encoding="utf-8")) or {}
        file_vars = {**(global_vars or {}), **(doc.get("vars", {}) or {})}
        before = doc.get("before", []) or []
        after = doc.get("after", []) or []
        for raw in doc.get("scenarios", []) or []:
            if raw.get("skip"):
                continue
            base_vars = {**file_vars, **(raw.get("vars") or {})}
            matrix = render(raw.get("matrix"), base_vars)
            for key, values in (matrix or {}).items():
                if isinstance(values, str) and "${" in values:
                    raise ValueError(f"{p}: matrix '{key}' uses an unknown variable: {values}")
            for combo in _expand_matrix(matrix):
                variables = {**base_vars, **combo}
                name = render(raw.get("name", p.stem), variables)
                if combo and "${" not in str(raw.get("name", "")):
                    label = lambda v: str(v.get("name", v)) if isinstance(v, dict) else str(v)
                    name = name + "[" + ",".join(label(v) for v in combo.values()) + "]"
                variables["scenario"] = name
                steps = render(before + (raw.get("steps") or []) + after, variables)
                result.append(Scenario(name=name, steps=steps, vars=variables,
                                       source=str(p), tags=raw.get("tags", []) or []))
    return result


def order_by_market(scenarios: list[Scenario], markets: list | None) -> list[Scenario]:
    """Run market by market (order of config.yaml `markets`): one country switch per market."""
    if not markets:
        return scenarios
    codes = [m.get("code") if isinstance(m, dict) else m for m in markets]

    def key(s: Scenario) -> int:
        m = s.vars.get("market")
        code = m.get("code") if isinstance(m, dict) else m
        return codes.index(code) if code in codes else -1

    return sorted(scenarios, key=key)  # stable: KPI order within a market is kept


def filter_scenarios(scenarios: list[Scenario], only: list[str] | None, tags: list[str] | None,
                     exclude: list[str] | None = None) -> list[Scenario]:
    out = scenarios
    if only:
        out = [s for s in out if any(fnmatch.fnmatch(s.name, pat) for pat in only)]
    if tags:
        out = [s for s in out if set(tags) & set(s.tags)]
    if exclude:
        out = [s for s in out if not any(fnmatch.fnmatch(s.name, pat) for pat in exclude)]
    return out
