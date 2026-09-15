"""ncu_sdk.storage —— 本地 SQLite 持久化（docs/API_SPEC.md 第 6 节建议）。

设计要点：
- courses 按 (termcode, weekday, start_section, end_section) 建索引，
  直接对应“按 termcode-weekday-sections 索引”的建议。
- sections 表缓存 portal getSection 结果（节次表极少变动），带有效期。
- meta 表存 token / 学期起始日等零散键值。
"""

from __future__ import annotations

import json
import sqlite3
import time
from datetime import date
from pathlib import Path
from typing import Iterable

from .models import Course, Section
from .weeks import WeekSpec

_SCHEMA = """
CREATE TABLE IF NOT EXISTS courses (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    termcode       TEXT NOT NULL,
    name           TEXT NOT NULL,
    teacher        TEXT DEFAULT '',
    room           TEXT DEFAULT '',
    weekday        INTEGER NOT NULL,          -- 1=周一 ... 7=周日
    start_section  INTEGER NOT NULL,          -- jcid
    end_section    INTEGER NOT NULL,
    weeks_raw      TEXT DEFAULT '',           -- 原始周次串，如 "5-15周"
    parity         TEXT DEFAULT '',           -- ''/odd/even
    raw            TEXT DEFAULT '',           -- 原始单元格，便于排查
    updated_at     REAL NOT NULL,
    UNIQUE (termcode, name, teacher, room, weekday, start_section)
);
CREATE INDEX IF NOT EXISTS idx_course_lookup
    ON courses (termcode, weekday, start_section, end_section);

CREATE TABLE IF NOT EXISTS sections (
    term_index  INTEGER PRIMARY KEY,          -- jcid 1-13
    label       TEXT DEFAULT '',
    start_time  TEXT DEFAULT '',
    end_time    TEXT DEFAULT '',
    source      TEXT DEFAULT '',
    updated_at  REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS meta (
    key        TEXT PRIMARY KEY,
    value      TEXT DEFAULT '',
    updated_at REAL NOT NULL
);
"""


class ScheduleStore:
    """课程表本地存储。db_path 为 ':memory:' 时使用内存库（测试默认）。"""

    def __init__(self, db_path: str | Path = "ncu_schedule.db") -> None:
        self.db_path = str(db_path)
        self.conn = sqlite3.connect(self.db_path)
        self.conn.row_factory = sqlite3.Row
        self.conn.executescript(_SCHEMA)
        self.conn.commit()

    # ---------------------------------------------------------------- 生命周期
    def close(self) -> None:
        self.conn.close()

    def __enter__(self) -> "ScheduleStore":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    # ---------------------------------------------------------------- 课程写入
    def replace_term_courses(self, termcode: str, courses: Iterable[Course]) -> int:
        """整学期替换：先删旧课再批量写入（单事务，保证同步原子性）。"""
        rows = [
            (
                termcode,
                c.name,
                c.teacher,
                c.room,
                c.weekday,
                c.start_section,
                c.end_section,
                c.weeks.raw,
                c.weeks.parity or "",
                c.raw,
                time.time(),
            )
            for c in courses
        ]
        with self.conn:  # 事务
            self.conn.execute("DELETE FROM courses WHERE termcode = ?", (termcode,))
            self.conn.executemany(
                """INSERT INTO courses (termcode, name, teacher, room, weekday,
                       start_section, end_section, weeks_raw, parity, raw, updated_at)
                   VALUES (?,?,?,?,?,?,?,?,?,?,?)
                   ON CONFLICT (termcode, name, teacher, room, weekday, start_section)
                   DO UPDATE SET end_section = excluded.end_section,
                                 weeks_raw   = excluded.weeks_raw,
                                 parity      = excluded.parity,
                                 raw         = excluded.raw,
                                 updated_at  = excluded.updated_at""",
                rows,
            )
        return len(rows)

    def upsert_courses(self, courses: Iterable[Course]) -> int:
        """增量写入（不做删除），返回写入条数。"""
        rows = [
            (
                c.termcode,
                c.name,
                c.teacher,
                c.room,
                c.weekday,
                c.start_section,
                c.end_section,
                c.weeks.raw,
                c.weeks.parity or "",
                c.raw,
                time.time(),
            )
            for c in courses
        ]
        if not rows:
            return 0
        with self.conn:
            self.conn.executemany(
                """INSERT INTO courses (termcode, name, teacher, room, weekday,
                       start_section, end_section, weeks_raw, parity, raw, updated_at)
                   VALUES (?,?,?,?,?,?,?,?,?,?,?)
                   ON CONFLICT (termcode, name, teacher, room, weekday, start_section)
                   DO UPDATE SET end_section = excluded.end_section,
                                 weeks_raw   = excluded.weeks_raw,
                                 parity      = excluded.parity,
                                 raw         = excluded.raw,
                                 updated_at  = excluded.updated_at""",
                rows,
            )
        return len(rows)

    # ---------------------------------------------------------------- 课程查询
    def get_courses(
        self,
        termcode: str | None = None,
        weekday: int | None = None,
        week: int | None = None,
    ) -> list[Course]:
        """按学期/星期查询；week 非空时再按周次规则过滤。"""
        sql = "SELECT * FROM courses"
        clauses, args = [], []
        if termcode:
            clauses.append("termcode = ?")
            args.append(termcode)
        if weekday:
            clauses.append("weekday = ?")
            args.append(weekday)
        if clauses:
            sql += " WHERE " + " AND ".join(clauses)
        sql += " ORDER BY weekday, start_section"

        courses = [self._row_to_course(r) for r in self.conn.execute(sql, args)]
        if week is not None:
            courses = [c for c in courses if c.contains_week(week)]
        return courses

    def termcodes(self) -> list[str]:
        rows = self.conn.execute("SELECT DISTINCT termcode FROM courses ORDER BY termcode").fetchall()
        return [r["termcode"] for r in rows]

    def delete_term(self, termcode: str) -> int:
        with self.conn:
            cur = self.conn.execute("DELETE FROM courses WHERE termcode = ?", (termcode,))
        return cur.rowcount

    # ---------------------------------------------------------------- 节次表
    def save_sections(self, sections: Iterable[Section], source: str = "portal") -> int:
        now = time.time()
        rows = [(s.index, s.label, s.start_time, s.end_time, source, now) for s in sections if s.index]
        with self.conn:
            self.conn.executemany(
                """INSERT INTO sections (term_index, label, start_time, end_time, source, updated_at)
                   VALUES (?,?,?,?,?,?)
                   ON CONFLICT (term_index) DO UPDATE SET
                       label = excluded.label,
                       start_time = excluded.start_time,
                       end_time = excluded.end_time,
                       source = excluded.source,
                       updated_at = excluded.updated_at""",
                rows,
            )
        return len(rows)

    def get_sections(self, max_age_days: int | None = None) -> dict[int, Section]:
        """读取节次表；max_age_days 非空时，缓存过期返回空字典（促使重新拉取）。"""
        rows = self.conn.execute("SELECT * FROM sections ORDER BY term_index").fetchall()
        if not rows:
            return {}
        if max_age_days is not None:
            oldest = min(r["updated_at"] for r in rows)
            if time.time() - oldest > max_age_days * 86400:
                return {}
        return {
            r["term_index"]: Section(
                index=r["term_index"],
                label=r["label"],
                start_time=r["start_time"],
                end_time=r["end_time"],
            )
            for r in rows
        }

    # ---------------------------------------------------------------- meta
    def get_meta(self, key: str, default: str = "") -> str:
        row = self.conn.execute("SELECT value FROM meta WHERE key = ?", (key,)).fetchone()
        return row["value"] if row else default

    def set_meta(self, key: str, value: str) -> None:
        with self.conn:
            self.conn.execute(
                "INSERT INTO meta (key, value, updated_at) VALUES (?,?,?) "
                "ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                (key, value, time.time()),
            )

    def get_json(self, key: str, default=None):
        raw = self.get_meta(key)
        if not raw:
            return default
        try:
            return json.loads(raw)
        except ValueError:
            return default

    def set_json(self, key: str, value) -> None:
        self.set_meta(key, json.dumps(value, ensure_ascii=False))

    # ---------------------------------------------------------------- 内部
    @staticmethod
    def _row_to_course(row: sqlite3.Row) -> Course:
        weeks_raw = row["weeks_raw"] or ""
        try:
            weeks = WeekSpec.parse(weeks_raw) if weeks_raw else WeekSpec()
        except Exception:  # 解析失败不应影响读取
            weeks = WeekSpec(raw=weeks_raw)
        return Course(
            name=row["name"],
            teacher=row["teacher"],
            room=row["room"],
            weekday=row["weekday"],
            start_section=row["start_section"],
            end_section=row["end_section"],
            weeks=weeks,
            raw=row["raw"],
            termcode=row["termcode"],
        )


def monday_of(day: date) -> date:
    """返回所在周的周一。"""
    return day.fromordinal(day.toordinal() - day.weekday())
