"""ncu_sdk.ics —— 把课表导出为 iCalendar（.ics），便于导入系统日历/手机。

周次来自 gmsstu 的 weeks 字符串（docs/API_SPEC.md 6），逐周展开为 VEVENT，
避免依赖各日历客户端对复杂 RRULE 的兼容性。
"""

from __future__ import annotations

from datetime import date, datetime, timedelta

from .models import Course, Section
from .storage import monday_of

_VCAL_HEADER = """BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//ncu_sdk//CourseTable//CN
CALSCALE:GREGORIAN
METHOD:PUBLISH
X-WR-CALNAME:{calname}
X-WR-TIMEZONE:Asia/Shanghai
"""

_VEVENT = """BEGIN:VEVENT
UID:{uid}
SUMMARY:{summary}
DTSTART:{dtstart}
DTEND:{dtend}
LOCATION:{location}
DESCRIPTION:{description}
END:VEVENT
"""


def _fold(text: str) -> str:
    """RFC 5545 折行：单行不超过 75 字节。"""
    raw = text.encode("utf-8")
    if len(raw) <= 73:
        return text
    out, line = [], b""
    for ch in text:
        enc = ch.encode("utf-8")
        if len(line) + len(enc) > 73:
            out.append(line)
            line = b" " + enc
        else:
            line += enc
    out.append(line)
    return "\r\n".join(part.decode("utf-8") for part in out)


def _escape(text: str) -> str:
    return (
        str(text)
        .replace("\\", "\\\\")
        .replace(";", r"\;")
        .replace(",", r"\,")
        .replace("\n", r"\n")
    )


def build_ics(
    courses: list[Course],
    term_start: date,
    sections: dict[int, Section] | None = None,
    calendar_name: str = "南昌大学课表",
    weeks_limit: int = 30,
) -> str:
    """生成 ics 文本。

    Args:
        courses: 课程列表
        term_start: 第一周周一（用于把周次换算成日期）
        sections: 节次表，缺失时按每节 40 分钟估算（与 3.4 的节次时长一致）
        weeks_limit: 展开的最大周数
    """
    term_start = monday_of(term_start)
    lines = [_VCAL_HEADER.format(calname=_escape(calendar_name)).strip()]

    for course in courses:
        start_section = sections.get(course.start_section) if sections else None
        end_section = sections.get(course.end_section) if sections else None
        start_t = _parse(start_section.start_time if start_section else None, "08:00")
        end_t = _parse(end_section.end_time if end_section else None, "08:40")

        for week in course.weeks.weeks(limit=weeks_limit):
            day = term_start + timedelta(weeks=week - 1, days=course.weekday - 1)
            dtstart = datetime.combine(day, start_t)
            dtend = datetime.combine(day, end_t)
            if dtend <= dtstart:
                dtend = dtstart + timedelta(minutes=40)
            lines.append(
                _VEVENT.format(
                    uid=f"{course.termcode}-{course.weekday}-{course.start_section}-{course.name}-{week}@ncu.edu.cn",
                    summary=_escape(course.name),
                    dtstart=dtstart.strftime("%Y%m%dT%H%M%S"),
                    dtend=dtend.strftime("%Y%m%dT%H%M%S"),
                    location=_escape(course.room),
                    description=_escape(
                        f"{course.name} / 教师：{course.teacher or '未知'} / "
                        f"第{week}周（{course.weeks}） / 第{course.start_section}-{course.end_section}节"
                    ),
                ).strip()
            )

    lines.append("END:VCALENDAR")
    # 先拼成普通换行文本，再对每条“物理行”单独折行，避免把多行 VEVENT 块整体折叠
    body = "\n".join(lines)
    return "\r\n".join(_fold(ln) for ln in body.split("\n") if ln.strip()) + "\r\n"


def _parse(value: str | None, default: str) -> datetime.time:
    from datetime import time as _time

    if not value:
        value = default
    try:
        h, m = value.split(":")
        return _time(int(h), int(m))
    except (ValueError, AttributeError):
        return _time(8, 0)
