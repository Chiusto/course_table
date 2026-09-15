"""ncu_sdk.models —— 数据模型。

字段命名与 docs/API_SPEC.md 的响应字段保持一致：
- Course 来自 gmsstu 课表接口（4.2）的 rows[jcid][z1..z7]
- Section 来自 portal getSection（3.4）或 gmsstu 的 mc 字段
- CalendarEvent 来自 portal getEvents（3.3）
- TeachingWeek 来自 portal getWeekOfTeaching（3.5）
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, time

from .weeks import WeekSpec

WEEKDAY_NAMES = ("周一", "周二", "周三", "周四", "周五", "周六", "周日")


@dataclass(frozen=True)
class Section:
    """一节课（节次）。jcid 1-13 对应第一节~第十三节。"""

    index: int  # jcid
    label: str = ""  # 中文序号，如 "三"
    start_time: str = ""  # "09:50"
    end_time: str = ""  # "10:30"

    @property
    def display(self) -> str:
        return f"第{self.label}节 {self.start_time}~{self.end_time}" if self.start_time else f"第{self.label}节"

    @property
    def start(self) -> time | None:
        return _parse_hm(self.start_time)

    @property
    def end(self) -> time | None:
        return _parse_hm(self.end_time)


@dataclass
class Course:
    """一门课的一次排课（已合并连续节次）。

    gmsstu 返回的是“每格一节课”，例如周一 6、7、8 节都是机器学习，
    解析时会纵向合并为 start_section=6, end_section=8 的一条记录。
    """

    name: str
    teacher: str = ""
    room: str = ""
    weekday: int = 1  # 1=周一 ... 7=周日（z1..z7）
    start_section: int = 1  # jcid
    end_section: int = 1
    weeks: WeekSpec = field(default_factory=WeekSpec)
    raw: str = ""  # 原始单元格文本，便于排查解析问题
    termcode: str = ""

    # ---------------------------------------------------------------- 派生属性
    @property
    def weekday_name(self) -> str:
        return WEEKDAY_NAMES[self.weekday - 1] if 1 <= self.weekday <= 7 else str(self.weekday)

    @property
    def section_count(self) -> int:
        return self.end_section - self.start_section + 1

    @property
    def section_range(self) -> str:
        return (
            f"{self.start_section}-{self.end_section}节"
            if self.end_section > self.start_section
            else f"{self.start_section}节"
        )

    def contains_week(self, week: int) -> bool:
        """第 week 周是否上这门课。周次以 gmsstu 的 weeks 字符串为准（6）。"""
        return self.weeks.contains(week)

    # ---------------------------------------------------------------- 展示
    def describe(self, sections: dict[int, Section] | None = None) -> str:
        """人类可读描述；传入节次表时可附带具体时间。"""
        time_part = ""
        if sections:
            s, e = sections.get(self.start_section), sections.get(self.end_section)
            if s and e and s.start_time and e.end_time:
                time_part = f" {s.start_time}-{e.end_time}"
        return f"{self.weekday_name} {self.section_range}{time_part} {self.name}（{self.teacher}）{self.room} [{self.weeks}]"

    def to_dict(self) -> dict:
        return {
            "termcode": self.termcode,
            "name": self.name,
            "teacher": self.teacher,
            "room": self.room,
            "weekday": self.weekday,
            "start_section": self.start_section,
            "end_section": self.end_section,
            "weeks_raw": self.weeks.raw,
            "raw": self.raw,
        }


@dataclass(frozen=True)
class CalendarEvent:
    """portal getEvents 返回的一条日程（3.3）。"""

    schedule_id: str = ""
    title: str = ""
    start_time: str = ""
    end_time: str = ""
    address: str = ""
    calendar_name: str = ""
    date: str = ""  # YYYY-MM-DD，来自 data.schedule 的键

    @classmethod
    def from_api(cls, date_key: str, item: dict) -> "CalendarEvent":
        return cls(
            schedule_id=str(item.get("scheduleId") or item.get("id") or ""),
            title=item.get("title", "") or "",
            start_time=item.get("startTime", "") or "",
            end_time=item.get("endTime", "") or "",
            address=item.get("address", "") or "",
            calendar_name=(item.get("calendarName") or "") if isinstance(item.get("calendarName"), str) else "",
            date=date_key,
        )


@dataclass(frozen=True)
class TeachingWeek:
    """portal getWeekOfTeaching 结果（3.5）。date 为 -1 表示未开学。"""

    semester: str = ""
    week: int = -1  # -1 = 未开学/假期
    raw_date: tuple = ()  # 接口原始 date 数组（可能一次查询多个日期）

    @property
    def started(self) -> bool:
        return self.week > 0


def _parse_hm(value: str) -> time | None:
    try:
        h, m = value.split(":")
        return time(int(h), int(m))
    except (ValueError, AttributeError):
        return None


def weekday_of(day: date) -> int:
    """date -> 1..7（周一为 1），与 z1..z7 对齐。"""
    return day.isoweekday()
