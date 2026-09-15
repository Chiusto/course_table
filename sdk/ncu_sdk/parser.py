"""ncu_sdk.parser —— gmsstu 响应解析。

docs/API_SPEC.md 4.2 的响应结构：
    {"rows": [{"jcid": "3", "mc": "第三节 09:50~10:30", "sjbz": "上午",
               "z1": null, "z5": "组合数学[5-15周] 幸玮[前湖北校区研究生院316]"}]}
    jcid 1-13 = 第一至第十三节；z1-z7 = 周一至周日。

两个关键处理：
1. 单元格解析：文档给出正则 (.+?)\\[(.+?)\\]\\s*(\\S+)\\[(.+?)\\] -> name/weeks/teacher/room
   该正则要求教师名不含空格，实际数据偶有例外，因此先严格匹配，失败再走宽松回退。
2. 节次合并：rows 中“一节课一行”，连续的 jcid 且单元格文本相同属于同一门课，
   需要合并为 start_section..end_section（文档实测：周一 6-8 节机器学习）。
"""

from __future__ import annotations

import re
from typing import Iterable

from .config import SECTION_MAX, SECTION_MIN, WEEKDAY_KEYS
from .errors import ParseError
from .models import Course, Section
from .weeks import WeekSpec

# 文档 4.2 给出的标准正则：课程名[周次]教师[教室]
_STRICT_RE = re.compile(r"(.+?)\[(.+?)\]\s*(\S+)\[(.+?)\]")
# 回退正则：允许教师名含空格/全角空格
_LOOSE_RE = re.compile(r"(.+?)\s*[\[［](.+?)[\]］]\s*(.+?)\s*[\[［](.+?)[\]］]")
# mc 字段："第三节 09:50~10:30"
_MC_RE = re.compile(r"第\s*(\S+?)\s*节\s*(\d{1,2}:\d{2})\s*[~～\-—]\s*(\d{1,2}:\d{2})")

_CN_DIGITS = {"一": 1, "二": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7,
              "八": 8, "九": 9, "十": 10, "十一": 11, "十二": 12, "十三": 13,
              "十四": 14, "十五": 15}


def parse_cell(cell: str) -> tuple[str, WeekSpec, str, str]:
    """解析单个课程单元格 -> (课程名, 周次, 教师, 教室)。

    >>> name, weeks, teacher, room = parse_cell("组合数学[5-15周] 幸玮[前湖北校区研究生院316]")
    """
    if not cell or not str(cell).strip():
        raise ParseError("单元格为空")
    text = str(cell).strip()

    m = _STRICT_RE.match(text) or _LOOSE_RE.match(text)
    if not m:
        raise ParseError(f"无法解析课程单元格：{text!r}")
    name, weeks_raw, teacher, room = (g.strip() for g in m.groups())
    # 回退正则可能把课程名里的空白留下，统一压缩
    name = re.sub(r"\s{2,}", " ", name)
    return name, WeekSpec.parse(weeks_raw), teacher, room


def parse_mc(mc: str, jcid: int | str | None = None) -> Section:
    """解析 rows[].mc（如 "第三节 09:50~10:30"）为 Section。

    mc 缺失或格式变化时退化为仅序号（时间留空，可由 portal getSection 补全）。
    """
    idx = _to_int(jcid) if jcid is not None else 0
    if not mc:
        return Section(index=idx or 0, label="")
    m = _MC_RE.search(str(mc))
    if not m:
        # 退化：只取 "第三节"
        lm = re.search(r"第\s*(\S+?)\s*节", str(mc))
        label = lm.group(1) if lm else ""
        return Section(index=idx or _CN_DIGITS.get(label, 0), label=label, start_time="", end_time="")
    label, start_time, end_time = m.groups()
    return Section(
        index=idx or _CN_DIGITS.get(label, 0),
        label=label,
        start_time=start_time,
        end_time=end_time,
    )


def parse_rows(rows: Iterable[dict], termcode: str = "", merge: bool = True) -> list[Course]:
    """把 gmsstu rows 解析为 Course 列表。

    Args:
        rows: /rows 数组（元素含 jcid/mc/z1..z7）
        termcode: 学期代码，写入 Course.termcode
        merge: 是否合并连续节次（默认 True）

    Returns:
        Course 列表，按 (weekday, start_section) 排序
    """
    raw_courses: list[Course] = []
    sections: dict[int, Section] = {}

    for row in rows or []:
        jcid = _to_int(row.get("jcid"))
        if jcid is None or not (SECTION_MIN <= jcid <= SECTION_MAX):
            continue
        sections[jcid] = parse_mc(row.get("mc"), jcid)

        for weekday, key in enumerate(WEEKDAY_KEYS, start=1):
            cell = row.get(key)
            if not cell or not str(cell).strip():
                continue
            try:
                name, weeks, teacher, room = parse_cell(cell)
            except ParseError:
                # 保留无法解析的条目，raw 里有原文，避免静默丢课
                name, weeks, teacher, room = str(cell).strip(), WeekSpec(), "", ""
            raw_courses.append(
                Course(
                    name=name,
                    teacher=teacher,
                    room=room,
                    weekday=weekday,
                    start_section=jcid,
                    end_section=jcid,
                    weeks=weeks,
                    raw=str(cell).strip(),
                    termcode=termcode,
                )
            )

    courses = _merge_sections(raw_courses) if merge else raw_courses
    courses.sort(key=lambda c: (c.weekday, c.start_section, c.name))
    return courses


def _merge_sections(courses: list[Course]) -> list[Course]:
    """纵向合并：同一天、同一课程（原始文本相同）且 jcid 连续 -> 一条记录。

    文档实测样例中“周三 11-13 节 最优化”在 rows 里是 jcid 11/12/13 三行。
    """
    merged: list[Course] = []
    # 按 (周几, 课程名, 原始文本) 分组，保证不会把两门同名课错误合并
    buckets: dict[tuple[int, str, str], list[Course]] = {}
    for c in courses:
        buckets.setdefault((c.weekday, c.name, c.raw), []).append(c)

    for (_wd, _name, _raw), group in buckets.items():
        group.sort(key=lambda c: c.start_section)
        current: Course | None = None
        for c in group:
            if current is not None and c.start_section == current.end_section + 1:
                # 连续一节 -> 扩展结束节次（周次应一致，取范围更全的）
                current.end_section = c.end_section
                if not current.weeks.ranges and c.weeks.ranges:
                    current.weeks = c.weeks
                continue
            if current is not None:
                merged.append(current)
            current = Course(**{**c.__dict__})  # 浅拷贝，避免改动原对象
        if current is not None:
            merged.append(current)
    return merged


def parse_sections_payload(payload: dict) -> list[Section]:
    """解析 portal getSection 响应（3.4）：data.data[] -> Section 列表。"""
    data = (payload or {}).get("data") or {}
    items = data.get("data") if isinstance(data, dict) else data
    if not isinstance(items, list):
        raise ParseError(f"getSection 响应结构异常：{str(payload)[:200]}")
    sections: list[Section] = []
    for i, item in enumerate(items, start=1):
        label = str(item.get("section") or "").strip()
        sections.append(
            Section(
                index=_CN_DIGITS.get(label, i),
                label=label,
                start_time=str(item.get("startTime") or ""),
                end_time=str(item.get("endTime") or ""),
            )
        )
    return sections


def _to_int(value) -> int | None:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return None
