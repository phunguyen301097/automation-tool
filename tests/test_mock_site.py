"""End-to-end tests of the runner against a local mock of the dashboard."""
from __future__ import annotations

import csv
import io
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qsl, urlparse

import openpyxl
import pytest
import yaml

from weplan_export.config import load_config, load_scenarios
from weplan_export.runner import run_scenarios

MOCK_HTML = (Path(__file__).parent / "mock_site" / "index.html").read_bytes()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/export":
            q = dict(parse_qsl(url.query))
            fmt = q.pop("format")
            rows = [["key", "value"]] + [[k, v] for k, v in q.items()]
            if fmt == "xlsx":
                wb = openpyxl.Workbook()
                for r in rows:
                    wb.active.append(r)
                buf = io.BytesIO()
                wb.save(buf)
                body, ctype = buf.getvalue(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            else:
                buf = io.StringIO()
                csv.writer(buf).writerows(rows)
                body, ctype = buf.getvalue().encode(), "text/csv"
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Disposition", f'attachment; filename="table_export.{fmt}"')
        else:
            body = MOCK_HTML
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


@pytest.fixture(scope="module")
def server():
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{httpd.server_port}"
    httpd.shutdown()


@pytest.fixture
def config(server, tmp_path):
    cfg = load_config(None)
    cfg["base_url"] = server
    cfg["output_dir"] = str(tmp_path / "out")
    cfg["auth"]["required"] = False
    cfg["timeouts"].update(default=10_000, data_load=20_000, download=20_000)
    return cfg


def _run(tmp_path, config, doc):
    f = tmp_path / "s.yaml"
    f.write_text(yaml.safe_dump(doc, allow_unicode=True), encoding="utf-8")
    return run_scenarios(load_scenarios([f]), config)


def _xlsx_dict(path):
    wb = openpyxl.load_workbook(path)
    return {r[0]: r[1] for r in wb.active.iter_rows(min_row=2, values_only=True)}


def test_full_flow_xlsx_with_matrix(tmp_path, config):
    doc = {
        "vars": {"from": "max-30d", "to": "max"},
        "scenarios": [{
            "name": "cov_${country}_${network}",
            "matrix": {"country": ["Cambodia"], "network": ["METFONE", "SMART"]},
            "steps": [
                {"goto": "/app/bi/coverage"},
                {"open_menu": ["Latency", "Latency Mobile (Cellular)"]},
                {"select_country": "${country}"},
                {"set_date": {"from": "${from}", "to": "${to}"}},
                {"select_filter": {"id": "carrier_filter", "options": ["${network}"]}},
                {"filters": {"coverage_filter": ["4G"], "origin_filter": ["Weplan"]}},
                {"choose_view": "macro"},
                {"wait_for_table": {}},
                {"download_table": {"format": "xlsx", "filename": "lat/${country}_${network}_${date_from}_${date_to}"}},
            ],
        }],
    }
    results = _run(tmp_path, config, doc)
    assert [r.ok for r in results] == [True, True], [r.error for r in results]
    out = Path(config["output_dir"])
    for net in ("METFONE", "SMART"):
        f = out / "lat" / f"Cambodia_{net}_2026-08-24_2026-09-23.xlsx"
        assert f.exists()
        data = _xlsx_dict(f)
        assert data["country"] == "kh"
        assert data["kpi"] == "/app/bi/latencyMobile"
        assert data["date"] == "2026-08-24..2026-09-23"
        assert data["carrier"] == net
        assert data["coverage"] == "4G"
        assert data["origin"] == "Weplan"
        assert data["view"] == "byCountry"


def test_csv_and_requery_after_filter_change(tmp_path, config):
    doc = {"scenarios": [{
        "name": "csv_requery",
        "steps": [
            {"goto": "/app/bi/coverage"},
            {"set_date": {"from": "2026-08-01", "to": "2026-08-31"}},
            {"choose_view": {"text": "By provinces"}},
            {"wait_for_table": {}},
            {"select_filter": {"id": "group_selector", "options": ["Cellular network"]}},
            {"apply": {}},
            {"wait_for_table": {}},
            {"download_table": {"format": "csv"}},
        ],
    }]}
    results = _run(tmp_path, config, doc)
    assert results[0].ok, results[0].error
    f = Path(results[0].downloads[0]["file"])
    assert f.suffix == ".csv"
    content = dict(csv.reader(f.read_text().splitlines()[1:]))
    assert content["group"] == "carrier"
    assert content["view"] == "byRegions"
    assert content["date"] == "2026-08-01..2026-08-31"


def test_failure_is_reported_with_artifacts(tmp_path, config):
    doc = {"scenarios": [{
        "name": "bad_network",
        "steps": [
            {"goto": "/app/bi/coverage"},
            {"select_filter": {"id": "carrier_filter", "options": ["NOPE"], "wait_options_ms": 1500}},
        ],
    }]}
    results = _run(tmp_path, config, doc)
    r = results[0]
    assert not r.ok
    assert "NOPE" in r.error and "ECONET" in r.error
    assert r.failed_step == "2. select_filter"
    assert any(a.endswith(".png") for a in r.artifacts)


def test_dashboard_error_fails_fast(tmp_path, config):
    doc = {"scenarios": [{
        "name": "no_date",
        "steps": [{"goto": "/app/bi/coverage"}, {"choose_view": "macro"}, {"wait_for_table": {}}],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert not r.ok
    assert "No date selected" in r.error


def test_date_widget_without_input_uses_popup_inputs(tmp_path, config):
    doc = {"scenarios": [{
        "name": "popup_date",
        "steps": [
            {"goto": "/app/bi/coverage?dp=popup"},
            {"set_date": {"from": "2026-08-01", "to": "2026-08-31"}},
            {"choose_view": "macro"},
            {"wait_for_table": {}},
            {"download_table": {"format": "csv"}},
        ],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert r.ok, r.error
    content = dict(csv.reader(Path(r.downloads[0]["file"]).read_text().splitlines()[1:]))
    assert content["date"] == "2026-08-01..2026-08-31"


def test_unknown_date_widget_dumps_html_and_skip_mode_works(tmp_path, config):
    doc = {"scenarios": [
        {"name": "unknown_date", "steps": [
            {"goto": "/app/bi/coverage?dp=none"},
            {"set_date": {"from": "2026-08-01", "to": "2026-08-31"}},
        ]},
    ]}
    r = _run(tmp_path, config, doc)[0]
    assert not r.ok
    dump = Path(config["output_dir"]) / "_debug" / "datepicker_unknown_date.html"
    assert str(dump) in r.error
    assert "reportrange-text" in dump.read_text()


def test_login_style_manual_download_is_kept(tmp_path, config, server):
    from playwright.sync_api import sync_playwright
    from weplan_export.runner import _keep_manual_downloads, launch_browser, new_context

    with sync_playwright() as pw:
        browser = launch_browser(pw, config)
        ctx = new_context(browser, config, use_state=False)
        _keep_manual_downloads(ctx, config)
        page = ctx.new_page()
        with page.expect_download() as dl:
            page.goto(server + "/app/bi/coverage")
            page.evaluate("u => { const a = document.createElement('a'); a.href = u; document.body.appendChild(a); a.click(); }",
                          server + "/export?format=csv&x=1")
        dl.value.path()  # wait until finished
        page.wait_for_timeout(500)
        browser.close()
    assert (Path(config["output_dir"]) / "manual" / "table_export.csv").exists()


def test_date_text_input_variant(tmp_path, config):
    doc = {"scenarios": [{
        "name": "input_date",
        "steps": [
            {"goto": "/app/bi/coverage?dp=input"},
            {"set_date": {"from": "2026-07-15", "to": "2026-08-02"}},
            {"choose_view": "macro"},
            {"wait_for_table": {}},
            {"download_table": {"format": "json"}},
        ],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert r.ok, r.error
    assert r.downloads[0]["file"].endswith(".json")
    assert "date,2026-07-15..2026-08-02" in Path(r.downloads[0]["file"]).read_text()


def test_calendar_day_after_max_date_fails_clearly(tmp_path, config):
    doc = {"scenarios": [{
        "name": "future_date",
        "steps": [
            {"goto": "/app/bi/coverage"},
            {"set_date": {"from": "2026-09-01", "to": "2026-09-25"}},
        ],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert not r.ok
    assert "2026-09-25 is not selectable" in r.error


def test_video_like_calendar_navigates_months(tmp_path, config):
    """Default mock widget reproduces the recorded dashboard picker (no known class names)."""
    doc = {"scenarios": [{
        "name": "nav_months",
        "steps": [
            {"goto": "/app/bi/coverage"},
            {"set_date": {"from": "2026-05-31", "to": "2026-07-01"}},
            {"choose_view": "macro"},
            {"wait_for_table": {}},
            {"download_table": {"format": "csv"}},
        ],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert r.ok, r.error
    assert "date,2026-05-31..2026-07-01" in Path(r.downloads[0]["file"]).read_text()


def test_daterangepicker_variant(tmp_path, config):
    doc = {"scenarios": [{
        "name": "drp",
        "steps": [
            {"goto": "/app/bi/coverage?dp=drp"},
            {"set_date": {"from": "2026-08-03", "to": "2026-09-14"}},
            {"choose_view": "macro"},
            {"wait_for_table": {}},
            {"download_table": {"format": "csv"}},
        ],
    }]}
    r = _run(tmp_path, config, doc)[0]
    assert r.ok, r.error
    assert "date,2026-08-03..2026-09-14" in Path(r.downloads[0]["file"]).read_text()
