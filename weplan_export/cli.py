"""Command line interface: python -m weplan_export <command>."""
from __future__ import annotations

import argparse
import glob
import sys

from .actions import STEPS
from .config import filter_scenarios, load_config, load_scenarios, order_by_market


def _scenario_files(patterns: list[str]) -> list[str]:
    files: list[str] = []
    for p in patterns or ["scenarios/*.yaml"]:
        matched = sorted(glob.glob(p)) or ([p] if not any(c in p for c in "*?[") else [])
        files += matched
    if not files:
        sys.exit("No scenario files found")
    return files


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="weplan_export", description="Automated table export for the Weplan dashboard")
    ap.add_argument("-c", "--config", default="config.yaml", help="config file (default: config.yaml)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("login", help="open a browser, log in manually and save the session")

    ins = sub.add_parser("inspect", help="save the rendered HTML of the date widget and result table")
    ins.add_argument("--headed", action="store_true")
    ins.add_argument("--view", default="macro", help="visualization to open (default: macro)")

    r = sub.add_parser("run", help="run scenarios")
    r.add_argument("files", nargs="*", help="scenario YAML files (default: scenarios/*.yaml)")
    r.add_argument("-k", "--only", action="append", help="run only scenarios whose name matches (glob, repeatable)")
    r.add_argument("-e", "--exclude", action="append", help="skip scenarios whose name matches (glob, repeatable)")
    r.add_argument("-t", "--tag", action="append", help="run only scenarios with this tag (repeatable)")
    r.add_argument("--headed", action="store_true", help="show the browser")
    r.add_argument("--slow-mo", type=int, default=None, help="slow down each action (ms)")
    r.add_argument("--trace", action="store_true", help="record a Playwright trace per scenario")
    r.add_argument("-x", "--stop-on-fail", action="store_true")
    r.add_argument("--isolated", action="store_true",
                   help="fresh browser context per scenario (default: all scenarios share one page)")
    r.add_argument("--dry-run", action="store_true", help="only print the expanded scenarios")
    r.add_argument("--resume", nargs="?", const="latest", default=None, metavar="RUN",
                   help="skip scenarios already finished in the latest run (or in downloads/_runs/RUN) "
                        "and run the rest")
    r.add_argument("--order", choices=["page", "market"], default=None,
                   help="page: each page for all markets, then the next page (default); "
                        "market: all pages of one market, then the next market")

    ls = sub.add_parser("list", help="list scenarios")
    ls.add_argument("files", nargs="*")

    sub.add_parser("steps", help="list available step types")

    args = ap.parse_args(argv)
    config = load_config(args.config)

    if args.cmd == "login":
        from .runner import interactive_login
        interactive_login(config)
        return 0

    if args.cmd == "inspect":
        from .runner import inspect_page
        inspect_page(config, headed=args.headed, view=args.view)
        return 0

    if args.cmd == "steps":
        for name, fn in sorted(STEPS.items()):
            doc = (fn.__doc__ or "").strip().splitlines()
            print(f"{name:16} {doc[0] if doc else ''}")
        return 0

    scenarios = load_scenarios(_scenario_files(args.files), config.get("vars"))
    if (getattr(args, "order", None) or config.get("run_order", "page")) == "market":
        scenarios = order_by_market(scenarios, (config.get("vars") or {}).get("markets"))

    if args.cmd == "list":
        for s in scenarios:
            print(f"{s.name:50} {','.join(s.tags):20} {s.source}")
        return 0

    scenarios = filter_scenarios(scenarios, args.only, args.tag, args.exclude)
    if not scenarios:
        sys.exit("No scenario matched")
    done: list[str] = []
    if args.resume:
        from .runner import completed_in_last_run
        report, finished = completed_in_last_run(config["output_dir"], args.resume)
        if report is None:
            sys.exit(f"--resume: no earlier run report found in {config['output_dir']}/_runs")
        done = [s.name for s in scenarios if s.name in finished]
        scenarios = [s for s in scenarios if s.name not in finished]
        print(f"Resuming from {report}: {len(done)} scenario(s) already done, {len(scenarios)} left.")
        if not scenarios:
            print("Nothing left to run.")
            return 0

    if args.dry_run:
        import yaml
        for s in scenarios:
            print(f"# {s.name}  ({s.source})")
            print(yaml.safe_dump(s.steps, allow_unicode=True, sort_keys=False))
        return 0

    print(f"Running {len(scenarios)} scenario(s):")
    for s in scenarios:
        print(f"  - {s.name}")
    print()
    from .runner import run_scenarios
    try:
        results = run_scenarios(scenarios, config, headed=True if args.headed else None,
                            slow_mo=args.slow_mo, trace=args.trace, stop_on_fail=args.stop_on_fail,
                            isolated=True if args.isolated else None, done=done)
    except KeyboardInterrupt:
        return 130
    return 0 if all(r.ok for r in results) else 1
