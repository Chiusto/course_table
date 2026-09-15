"""ncu_sdk.schedule —— 顶层编排。

把 CAS 登录、gmsstu 真课表、portal 节次表/教学周、SQLite 存储串起来，
对外只暴露“登录 -> 同步 -> 查询/导出”三步。

教学周判定遵循 docs/API_SPEC.md 6：
    优先用 gmsstu 的 weeks 字符串，portal 的 getWeekOfTeaching 仅用于校准。
即：课程是否在某周上课由 Course.weeks 决定；当前是第几周优先问 portal，
portal 不可用（-1/未带 token）时退回本地学期起始日推算。
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from datetime import date, timedelta
from typing import Iterable

import requests

from .config import Settings, termcode_of
from .errors import ConfigError
from .gmsstu import GmsstuClient
from .models import Course, Section
from .portal import PortalClient
from .storage import ScheduleStore, monday_of


@dataclass
class SyncResult:
    """一次同步的结果摘要，便于日志/CLI 展示。"""

    termcode: str
    courses: int = 0
    sections: int = 0
    section_source: str = ""
    week: int = -1

    def __str__(self) -> str:
        week = f"第{self.week}周" if self.week > 0 else "未开学"
        return (
            f"[{self.termcode}] 同步 {self.courses} 门课，节次表 {self.sections} 条"
            f"（来源 {self.section_source or '无'}），{week}"
        )


class NCUClient:
    """南昌大学课程表 SDK 主入口。

    典型用法：
        client = NCUClient(username="...", password="...")
        client.login()
        client.sync("202620271")
        for c in client.week_schedule("202620271", week=3):
            print(c.describe(client.sections()))
    """

    def __init__(
        self,
        username: str | None = None,
        password: str | None = None,
        settings: Settings | None = None,
        store: ScheduleStore | None = None,
        session: requests.Session | None = None,
        token: str | None = None,
    ) -> None:
        self.settings = settings or Settings()
        # 凭据优先取参数，其次环境变量，避免把密码写进代码
        self.username = username or os.getenv("NCU_USERNAME", "")
        self.password = password or os.getenv("NCU_PASSWORD", "")
        self.session = session or requests.Session()
        self.store = store if store is not None else ScheduleStore(self.settings.db_path)
        self.portal = PortalClient(token=token, settings=self.settings, session=self.session)
        self.gmsstu = GmsstuClient(settings=self.settings, session=self.session)
        self._logged_in = False

    # ---------------------------------------------------------------- 登录
    def login(self, portal: bool = True, gmsstu: bool = True) -> None:
        """一次 CAS 登录，两个系统共享同一个 Session。

        portal 换 token 失败不会中断（课表主体在 gmsstu），只记录警告。
        """
        if not self.username or not self.password:
            raise ConfigError("缺少用户名/密码：传入参数或设置 NCU_USERNAME / NCU_PASSWORD 环境变量")

        if gmsstu:
            self.gmsstu.login(self.username, self.password)
        if portal:
            try:
                self.portal.login(self.username, self.password)
                self.store.set_meta("portal_token", self.portal.token or "")
            except Exception as exc:  # 门户登录失败不影响课表抓取
                print(f"[warn] 融合门户登录未完成（{exc}），课表同步仍可继续")
        self._logged_in = True

    # ---------------------------------------------------------------- 同步
    def sync(self, termcode: str | None = None, refresh_sections: bool = False) -> SyncResult:
        """拉取 gmsstu 课表并落库，顺带缓存节次表。

        Args:
            termcode: 学期代码，缺省按当前日期推算
            refresh_sections: True 时忽略本地缓存重新拉取节次表
        """
        if not self._logged_in and not self.gmsstu.has_session():
            self.login()
        termcode = termcode or termcode_of(date.today())

        # 1) 真课表（gmsstu）
        courses = self.gmsstu.fetch_courses(termcode)
        self.store.replace_term_courses(termcode, courses)

        # 2) 节次表：优先本地缓存；无缓存则取 portal，仍失败则用 gmsstu 的 mc 兜底
        sections = self.store.get_sections(max_age_days=None if not refresh_sections else 0)
        source = "cache"
        if refresh_sections or not sections:
            sections = self._fetch_sections(termcode)
            source = "portal" if sections and sections.get(1, Section(1)).start_time else "gmsstu-mc"

        return SyncResult(
            termcode=termcode,
            courses=len(courses),
            sections=len(sections),
            section_source=source,
            week=self.resolve_week(termcode, date.today()),
        )

    def _fetch_sections(self, termcode: str) -> dict[int, Section]:
        """节次表获取：portal getSection -> 落库；失败回退 gmsstu rows[].mc。"""
        try:
            if not self.portal.token:
                self.portal.token = self.store.get_meta("portal_token") or None
            if self.portal.token:
                section_list = self.portal.get_section()
                self.store.save_sections(section_list, source="portal")
                return {s.index: s for s in section_list}
        except Exception:
            pass
        try:
            fallback = self.gmsstu.fetch_sections(termcode)
            self.store.save_sections(fallback.values(), source="gmsstu-mc")
            return fallback
        except Exception:
            return {}

    def sections(self, refresh: bool = False) -> dict[int, Section]:
        """节次表（本地缓存优先，见 6：getSection 本地缓存）。"""
        sections = self.store.get_sections(max_age_days=None if refresh else self.settings.section_cache_days)
        if sections and not refresh:
            return sections
        term = self.store.termcodes()[0] if self.store.termcodes() else termcode_of(date.today())
        return self._fetch_sections(term) or sections

    # ---------------------------------------------------------------- 查询
    def all_courses(self, termcode: str) -> list[Course]:
        """整学期课程（优先本地库，本地为空时回源）。"""
        courses = self.store.get_courses(termcode=termcode)
        if not courses:
            courses = self.gmsstu.fetch_courses(termcode)
            self.store.replace_term_courses(termcode, courses)
        return courses

    def week_schedule(self, termcode: str, week: int) -> list[Course]:
        """第 week 周的课程表（按周次规则过滤）。"""
        return self.store.get_courses(termcode=termcode, week=week) or [
            c for c in self.all_courses(termcode) if c.contains_week(week)
        ]

    def day_schedule(self, termcode: str, week: int, weekday: int) -> list[Course]:
        """某周某天的课程，按节次排序。"""
        return sorted(
            [c for c in self.week_schedule(termcode, week) if c.weekday == weekday],
            key=lambda c: c.start_section,
        )

    def today_schedule(self, termcode: str | None = None, day: date | None = None) -> tuple[int, list[Course]]:
        """某日期当天的课 -> (第几周, 课程列表)。week<=0 表示未开学/假期。"""
        day = day or date.today()
        termcode = termcode or termcode_of(day)
        week = self.resolve_week(termcode, day)
        return week, self.day_schedule(termcode, week, day.isoweekday())

    # ---------------------------------------------------------------- 教学周
    def resolve_week(self, termcode: str, day: date | None = None) -> int:
        """判定 day 属于第几教学周，返回 -1 表示未开学/假期。

        策略（6：portal 仅校准）：
        1. portal getWeekOfTeaching 可用且返回正数 -> 直接使用
        2. 否则用 Settings.term_start_dates[termcode]（第一周周一）推算
        """
        day = day or date.today()
        if self.settings.use_portal_week_calibration and self.portal.token:
            try:
                teaching_week = self.portal.get_week_of_teaching(day)
                if teaching_week.week > 0:
                    return teaching_week.week
            except Exception:
                pass  # 门户不可用，退回本地推算

        start = self.settings.term_start_dates.get(termcode)
        if start:
            start_date = date.fromisoformat(start)
            delta = (monday_of(day) - monday_of(start_date)).days
            if delta < 0:
                return -1
            return delta // 7 + 1
        return -1

    def date_of_week(self, termcode: str, week: int, weekday: int = 1) -> date | None:
        """第 week 周 weekday（1=周一）对应的日期，依赖学期起始日配置。"""
        start = self.settings.term_start_dates.get(termcode)
        if not start:
            return None
        return date.fromisoformat(start) + timedelta(weeks=week - 1, days=weekday - 1)

    # ---------------------------------------------------------------- 导出
    def to_dict(self, termcode: str, week: int | None = None) -> list[dict]:
        courses = self.week_schedule(termcode, week) if week else self.all_courses(termcode)
        return [c.to_dict() for c in courses]

    def iter_courses(self, termcode: str) -> Iterable[Course]:
        return self.all_courses(termcode)
