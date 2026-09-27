"""Loading and merging of config.yaml and scenario files."""
from __future__ import annotations

import copy
import fnmatch
import itertools
import re
from dataclasses import dataclass, field
from pathlib import Path
from string import Template
from typing import Any

import yaml

DEFAULT_CONFIG: dict[str, Any] = {
    "base_url": "https://dashboard.weplananalytics.com",
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


_VAR_RE = re.compile(r"\$\{(\w+)\}")


def render(value: Any, variables: dict[str, Any]) -> Any:
    """Recursively substitute ${var} in strings."""
    if isinstance(value, str):
        # Keep native types when the whole string is one variable.
        m = _VAR_RE.fullmatch(value)
        if m and m.group(1) in variables:
            return variables[m.group(1)]
        return Template(value).safe_substitute({k: str(v) for k, v in variables.items()})
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


def load_scenarios(paths: list[str | Path]) -> list[Scenario]:
    """Load scenario files. A file may contain `vars`, `before`, `after` and `scenarios`.

    Each scenario may carry its own `vars` and a `matrix`, which produces one
    scenario per combination of values.
    """
    result: list[Scenario] = []
    for p in paths:
        p = Path(p)
        doc = yaml.safe_load(p.read_text(encoding="utf-8")) or {}
        file_vars = doc.get("vars", {}) or {}
        before = doc.get("before", []) or []
        after = doc.get("after", []) or []
        for raw in doc.get("scenarios", []) or []:
            if raw.get("skip"):
                continue
            for combo in _expand_matrix(raw.get("matrix")):
                variables = {**file_vars, **(raw.get("vars") or {}), **combo}
                name = render(raw.get("name", p.stem), variables)
                if combo and "${" not in str(raw.get("name", "")):
                    name = name + "[" + ",".join(f"{v}" for v in combo.values()) + "]"
                variables["scenario"] = name
                steps = render(before + (raw.get("steps") or []) + after, variables)
                result.append(Scenario(name=name, steps=steps, vars=variables,
                                       source=str(p), tags=raw.get("tags", []) or []))
    return result


def filter_scenarios(scenarios: list[Scenario], only: list[str] | None, tags: list[str] | None) -> list[Scenario]:
    out = scenarios
    if only:
        out = [s for s in out if any(fnmatch.fnmatch(s.name, pat) for pat in only)]
    if tags:
        out = [s for s in out if set(tags) & set(s.tags)]
    return out
