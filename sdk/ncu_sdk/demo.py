"""ncu_sdk.demo —— 内置样例数据（直接取自 docs/API_SPEC.md）。

用途：
- 无网络环境下的演示与测试（python -m ncu_sdk.cli demo）
- 单元测试的基准数据，保证解析结果与文档记载一致

数据说明：
- rows 结构、字段含义完全照抄 4.2
- 课程清单来自 4.2 末尾的“实测 2026-2027-1 你的课表”
- 文档未给出的周次/教室信息，用 (示例) 标注的占位值补齐，仅用于演示
"""

from __future__ import annotations

from .models import Section
from .parser import parse_rows

# ---------------------------------------------------------------- 3.4 节次表
_SECTION_DATA = [
    ("一", "08:00", "08:40"), ("二", "08:50", "09:30"), ("三", "09:50", "10:30"),
    ("四", "10:40", "11:20"), ("五", "11:30", "12:10"), ("六", "14:00", "14:40"),
    ("七", "14:50", "15:30"), ("八", "15:50", "16:30"), ("九", "16:40", "17:20"),
    ("十", "17:30", "18:10"), ("十一", "19:00", "19:40"), ("十二", "19:50", "20:30"),
    ("十三", "20:40", "21:20"),
]

# ---------------------------------------------------------------- 4.2 实测课表
# (weekday, 起止节次, 单元格文本)
_TIMETABLE = [
    (1, (6, 8), "机器学习[1-11周]胡书凡[前湖北校区研究生院316]"),
    (3, (3, 4), "高级计算机系统结构[1-16周]张宇成[前湖北校区研究生院215]"),
    (3, (8, 10), "数据科学与工程[1-16周]王洋洋[前湖北校区研究生院316]"),  # 周次/教室为示例值
    (3, (11, 13), "最优化[1-16周]肖艳阳[前湖北校区研究生院316]"),          # 周次/教室为示例值
    (4, (4, 4), "自然辩证法[1-8周]康琳[前湖北校区研究生院316]"),            # 周次/教室为示例值
    (5, (3, 5), "组合数学[5-15周] 幸玮[前湖北校区研究生院316]"),           # 4.2 原文格，教师前带空格
    (6, (3, 5), "工程伦理[6-6周]刘韬[基础实验大楼A106]"),                  # 周六 3-5 节
    (7, (6, 10), "工程伦理[6-6周]刘韬[基础实验大楼A106]"),                 # 周日 6-10 节
]


def sample_sections() -> list[Section]:
    """3.4 getSection 的 13 条节次数据。"""
    return [
        Section(index=i, label=label, start_time=start, end_time=end)
        for i, (label, start, end) in enumerate(_SECTION_DATA, start=1)
    ]


def sample_rows() -> list[dict]:
    """把实测课表还原成 gmsstu rows 结构（每节课一行，与接口一致）。"""
    rows: dict[int, dict] = {}
    for weekday, (start, end), cell in _TIMETABLE:
        for jcid in range(start, end + 1):
            row = rows.setdefault(
                jcid,
                {
                    "jcid": str(jcid),
                    "mc": f"第{_SECTION_DATA[jcid - 1][0]}节 "
                          f"{_SECTION_DATA[jcid - 1][1]}~{_SECTION_DATA[jcid - 1][2]}",
                    "sjbz": "上午" if jcid <= 5 else ("下午" if jcid <= 10 else "晚上"),
                },
            )
            for k in range(1, 8):
                row.setdefault(f"z{k}", None)
            row[f"z{weekday}"] = cell
    return [rows[k] for k in sorted(rows)]


def sample_courses(termcode: str = "202620271"):
    """用解析器处理样例 rows，得到合并后的 Course 列表。"""
    return parse_rows(sample_rows(), termcode=termcode)


def raw_response_sample() -> dict:
    """4.2 文档原文给出的两行示例（ regression fixture，保证向后兼容）。"""
    return {
        "rows": [
            {"jcid": "3", "mc": "第三节 09:50~10:30", "sjbz": "上午", "z1": None,
             "z5": "组合数学[5-15周] 幸玮[前湖北校区研究生院316]",
             "z6": "工程伦理[6-6周]刘韬[基础实验大楼A106]"},
            {"jcid": "6", "mc": "第六节 14:00~14:40", "sjbz": "下午",
             "z1": "机器学习[1-11周]胡书凡[前湖北校区研究生院316]",
             "z3": "高级计算机系统结构[1-16周]张宇成[前湖北校区研究生院215]"},
        ]
    }
