import datetime as dt

import yaml

from weplan_export.actions import parse_date
from weplan_export.config import filter_scenarios, load_scenarios


class FakeCtx:
    class page:
        @staticmethod
        def evaluate(_):
            return {"maxDate": "20260923", "minDate": "20250923"}


def test_parse_date():
    assert parse_date(FakeCtx, "2026-08-01") == dt.date(2026, 8, 1)
    assert parse_date(FakeCtx, "01/08/2026") == dt.date(2026, 8, 1)
    assert parse_date(FakeCtx, "max") == dt.date(2026, 9, 23)
    assert parse_date(FakeCtx, "max-30d") == dt.date(2026, 8, 24)
    assert parse_date(FakeCtx, "max-1m") == dt.date(2026, 8, 23)
    assert parse_date(FakeCtx, "min+1w") == dt.date(2025, 9, 30)
    assert parse_date(FakeCtx, dt.date(2026, 1, 1)) == dt.date(2026, 1, 1)


def test_matrix_vars_before_after(tmp_path):
    f = tmp_path / "s.yaml"
    f.write_text(yaml.safe_dump({
        "vars": {"fmt": "xlsx"},
        "before": [{"goto": "/x"}],
        "after": [{"screenshot": "end"}],
        "scenarios": [
            {"name": "a_${c}", "tags": ["t1"], "matrix": {"c": ["X", "Y"]},
             "steps": [{"select_country": "${c}"}, {"download_table": {"format": "${fmt}", "filename": "${c}_${date_from}"}}]},
            {"name": "skipped", "skip": True, "steps": []},
            {"name": "plain", "matrix": {"n": [1, 2]}, "steps": []},
        ],
    }))
    sc = load_scenarios([f])
    assert [s.name for s in sc] == ["a_X", "a_Y", "plain[1]", "plain[2]"]
    assert sc[1].steps == [
        {"goto": "/x"},
        {"select_country": "Y"},
        {"download_table": {"format": "xlsx", "filename": "Y_${date_from}"}},
        {"screenshot": "end"},
    ]
    assert [s.name for s in filter_scenarios(sc, ["a_*"], None)] == ["a_X", "a_Y"]
    assert [s.name for s in filter_scenarios(sc, None, ["t1"])] == ["a_X", "a_Y"]


def test_matrix_values_can_be_mappings(tmp_path):
    f = tmp_path / "s.yaml"
    f.write_text(yaml.safe_dump({
        "vars": {"kpi": "Coverage time"},
        "scenarios": [
            {"name": "${kpi}_${level.name}", "matrix": {"level": [{"name": "Net", "opts": ["4G", "3G"]}]},
             "steps": [{"select_filter": {"id": "x", "options": "${level.opts}"}},
                       {"download_table": {"filename": "${kpi}_${level.name}_${year}"}}]},
            {"name": "plain", "matrix": {"t": [{"name": "5G"}]}, "steps": []},
        ],
    }))
    sc = load_scenarios([f])
    assert [s.name for s in sc] == ["Coverage time_Net", "plain[5G]"]
    assert sc[0].steps[0]["select_filter"]["options"] == ["4G", "3G"]
    # ${year} is only known at run time and stays for later.
    assert sc[0].steps[1]["download_table"]["filename"] == "Coverage time_Net_${year}"


def test_markets_from_global_vars(tmp_path):
    import pytest
    from weplan_export.config import order_by_market
    f = tmp_path / "s.yaml"
    f.write_text(yaml.safe_dump({"scenarios": [
        {"name": "${market.code}_${t}", "matrix": {"market": "${markets}", "t": ["a", "b"]},
         "steps": [{"select_country": "${market.country}"}]}]}))
    markets = [{"code": "VTC", "country": "kh"}, {"code": "VTB", "country": "bi"}]
    sc = load_scenarios([f], {"markets": markets})
    assert [s.name for s in sc] == ["VTC_a", "VTC_b", "VTB_a", "VTB_b"]
    assert sc[2].steps == [{"select_country": "bi"}]
    reordered = order_by_market(list(reversed(sc)), markets)
    assert [s.name for s in reordered] == ["VTC_b", "VTC_a", "VTB_b", "VTB_a"]
    with pytest.raises(ValueError, match="unknown variable"):
        load_scenarios([f])  # no markets defined


def test_exclude_filter(tmp_path):
    f = tmp_path / "s.yaml"
    f.write_text(yaml.safe_dump({"scenarios": [
        {"name": "${m}_${k}", "matrix": {"m": ["VTB", "VTC"], "k": ["Coverage time", "Sample"]}, "steps": []}]}))
    sc = load_scenarios([f])
    assert [s.name for s in filter_scenarios(sc, None, None, ["VTB_Sample"])] == \
        ["VTB_Coverage time", "VTC_Coverage time", "VTC_Sample"]
    assert [s.name for s in filter_scenarios(sc, ["VTC_*"], None, ["*_Sample"])] == ["VTC_Coverage time"]
    assert [s.name for s in filter_scenarios(sc, None, None, ["VTB_*", "*_Sample"])] == ["VTC_Coverage time"]


def test_default_order_is_page_then_market(tmp_path):
    """Load order = file (page) by file, all markets of a page before the next page."""
    markets = [{"code": "VTC", "country": "kh"}, {"code": "VTB", "country": "bi"}]
    files = []
    for kpi in ("coverage", "sample"):
        f = tmp_path / f"{kpi}.yaml"
        f.write_text(yaml.safe_dump({"scenarios": [
            {"name": "${market.code}_" + kpi + "_${lvl}", "matrix": {"market": "${markets}", "lvl": ["Net", "Province"]},
             "steps": []}]}, sort_keys=False))
        files.append(f)
    names = [s.name for s in load_scenarios(files, {"markets": markets})]
    assert names == ["VTC_coverage_Net", "VTC_coverage_Province", "VTB_coverage_Net", "VTB_coverage_Province",
                     "VTC_sample_Net", "VTC_sample_Province", "VTB_sample_Net", "VTB_sample_Province"]
