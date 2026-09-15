"""ncu_sdk.cli —— 命令行入口。

    python -m ncu_sdk.cli login
    python -m ncu_sdk.cli sync --term 202620271
    python -m ncu_sdk.cli show --term 202620271 --week 3
    python -m ncu_sdk.cli today
    python -m ncu_sdk.cli sections
    python -m ncu_sdk.cli export --out ncu.ics

凭据通过 --username/--password 或环境变量 NCU_USERNAME / NCU_PASSWORD 提供。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import date

from .config import Settings, termcode_of
from .ics import build_ics
from .schedule import NCUClient
from .storage import ScheduleStore

WEEKDAY_NAMES = ("周一", "周二", "周三", "周四", "周五", "周六", "周日")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="ncu_sdk", description="南昌大学课程表 SDK 命令行")
    parser.add_argument("-u", "--username", default=os.getenv("NCU_USERNAME"))
    parser.add_argument("-p", "--password", default=os.getenv("NCU_PASSWORD"))
    parser.add_argument("--db", default="ncu_schedule.db", help="SQLite 路径")
    parser.add_argument("--token", default=os.getenv("NCU_ID_TOKEN"), help="手动提供 x-id-token")
    parser.add_argument("--term-start", action="append", default=[],
                        metavar="TERMCODE=YYYY-MM-DD",
                        help="学期第一周周一，可重复；例：202620271=2026-08-31")
    parser.add_argument("--timeout", type=float, default=15)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("login", help="登录并保存 token")
    p_sync = sub.add_parser("sync", help="同步课表到本地 SQLite")
    p_sync.add_argument("--term", help="termcode，如 202620271")
    p_sync.add_argument("--refresh-sections", action="store_true")

    p_show = sub.add_parser("show", help="查看课表")
    p_show.add_argument("--term")
    p_show.add_argument("--week", type=int)
    p_show.add_argument("--day", type=int, choices=range(1, 8), help="1=周一")
    p_show.add_argument("--json", action="store_true")

    sub.add_parser("today", help="今日课程")
    p_sec = sub.add_parser("sections", help="节次时间表")
    p_sec.add_argument("--refresh", action="store_true")

    p_exp = sub.add_parser("export", help="导出课表")
    p_exp.add_argument("--term")
    p_exp.add_argument("--format", choices=("ics", "json"), default="ics")
    p_exp.add_argument("--out", default="ncu_schedule.ics")

    sub.add_parser("demo", help="用内置样例离线演示（无需登录）")
    return parser


def _make_client(args) -> NCUClient:
    term_starts = {}
    for item in getattr(args, "term_start", []) or []:
        if "=" in item:
            k, v = item.split("=", 1)
            term_starts[k.strip()] = v.strip()
    store = ScheduleStore(args.db)
    settings = Settings(db_path=args.db, timeout=args.timeout, term_start_dates=term_starts)
    return NCUClient(username=args.username, password=args.password,
                     settings=settings, store=store, token=args.token)


def _print_courses(courses, sections: dict, as_json: bool = False) -> None:
    if as_json:
        print(json.dumps([c.to_dict() for c in courses], ensure_ascii=False, indent=2))
        return
    if not courses:
        print("（无课程）")
        return
    current_day = None
    for c in sorted(courses, key=lambda x: (x.weekday, x.start_section)):
        if c.weekday != current_day:
            current_day = c.weekday
            print(f"\n{WEEKDAY_NAMES[current_day - 1]}")
        print("  " + c.describe(sections))


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    client = _make_client(args)
    term = getattr(args, "term", None) or termcode_of(date.today())

    try:
        if args.command == "login":
            client.login()
            print(f"登录成功，token 已保存到 {args.db}")
            return 0

        if args.command == "sync":
            result = client.sync(term, refresh_sections=args.refresh_sections)
            print(result)
            return 0

        if args.command == "show":
            if args.week is None:
                week = client.resolve_week(term)
                courses = client.all_courses(term)
            else:
                week = args.week
                courses = client.week_schedule(term, week)
            if args.day:
                courses = [c for c in courses if c.weekday == args.day]
            if not args.json:
                print(f"学期 {term} 第{week if week > 0 else '?'}周")
            _print_courses(courses, client.sections(), as_json=args.json)
            return 0

        if args.command == "today":
            week, courses = client.today_schedule(term)
            print(f"{date.today()}（{WEEKDAY_NAMES[date.today().isoweekday() - 1]}）第{week}周")
            _print_courses(courses, client.sections())
            return 0

        if args.command == "sections":
            sections = client.sections(refresh=args.refresh)
            for idx in sorted(sections):
                print(f"  第{sections[idx].label}节  {sections[idx].start_time}-{sections[idx].end_time}")
            return 0

        if args.command == "export":
            courses = client.all_courses(term)
            if args.format == "json":
                payload = json.dumps(client.to_dict(term), ensure_ascii=False, indent=2)
                with open(args.out, "w", encoding="utf-8") as f:
                    f.write(payload)
            else:
                start = client.settings.term_start_dates.get(term)
                if not start:
                    print("导出 ics 需要学期起始日：请用 --term-start 202620271=2026-08-31 指定",
                          file=sys.stderr)
                    return 2
                ics_text = build_ics(courses, date.fromisoformat(start), client.sections())
                with open(args.out, "w", encoding="utf-8", newline="") as f:
                    f.write(ics_text)
            print(f"已导出 {len(courses)} 门课 -> {args.out}")
            return 0

        if args.command == "demo":
            return _demo(client)

    except Exception as exc:  # CLI 边界：统一打印错误
        print(f"[error] {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1
    return 0


def _demo(client: NCUClient) -> int:
    """离线演示：使用 docs/API_SPEC.md 中记录的样例数据。"""
    from . import demo

    courses = demo.sample_courses(termcode="202620271")
    client.store.replace_term_courses("202620271", courses)
    client.store.save_sections(demo.sample_sections(), source="demo")
    print("[离线演示] docs/API_SPEC.md 4.2 实测课表：")
    _print_courses(courses, client.store.get_sections())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
