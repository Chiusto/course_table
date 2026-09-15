"""gmsstu 响应解析测试（docs/API_SPEC.md 4.2 + 3.4 样例）。"""

import unittest

from _path import SDK_DIR, load_fixture  # noqa: F401
from ncu_sdk.demo import raw_response_sample, sample_courses, sample_rows
from ncu_sdk.models import Course
from ncu_sdk.parser import parse_cell, parse_mc, parse_rows, parse_sections_payload
from ncu_sdk.weeks import WeekSpec


class TestParseCell(unittest.TestCase):
    def test_document_regex_sample(self):
        # 4.2：组合数学[5-15周] 幸玮[前湖北校区研究生院316]（教师前带空格）
        name, weeks, teacher, room = parse_cell("组合数学[5-15周] 幸玮[前湖北校区研究生院316]")
        self.assertEqual((name, teacher, room), ("组合数学", "幸玮", "前湖北校区研究生院316"))
        self.assertEqual(weeks.raw, "5-15周")

    def test_no_space_variant(self):
        # 4.2：工程伦理[6-6周]刘韬[基础实验大楼A106]
        name, weeks, teacher, room = parse_cell("工程伦理[6-6周]刘韬[基础实验大楼A106]")
        self.assertEqual((name, teacher, room), ("工程伦理", "刘韬", "基础实验大楼A106"))
        self.assertTrue(weeks.contains(6))

    def test_empty(self):
        with self.assertRaises(Exception):
            parse_cell("")


class TestParseMc(unittest.TestCase):
    def test_document_mc(self):
        # 4.2：mc 字段样例
        s = parse_mc("第三节 09:50~10:30", jcid="3")
        self.assertEqual(s.index, 3)
        self.assertEqual(s.label, "三")
        self.assertEqual((s.start_time, s.end_time), ("09:50", "10:30"))

    def test_missing_mc(self):
        s = parse_mc(None, jcid=6)
        self.assertEqual(s.index, 6)


class TestParseRows(unittest.TestCase):
    def test_document_response_sample(self):
        """4.2 原文响应片段：逐字段断言。"""
        courses = parse_rows(load_fixture("gmsstu_rows.json")["rows"], termcode="202620271")
        by_key = {(c.weekday, c.start_section): c for c in courses}

        # z5/jcid=3 -> 组合数学
        c = by_key[(5, 3)]
        self.assertEqual(c.name, "组合数学")
        self.assertEqual(c.teacher, "幸玮")
        self.assertEqual(c.room, "前湖北校区研究生院316")
        self.assertEqual(c.weeks.raw, "5-15周")
        self.assertEqual(c.termcode, "202620271")

        # z6/jcid=3 -> 工程伦理（6-6 周）
        c = by_key[(6, 3)]
        self.assertEqual(c.name, "工程伦理")
        self.assertEqual(c.weeks.raw, "6-6周")

        # z1/jcid=6 -> 机器学习；z3/jcid=6 -> 高级计算机系统结构
        self.assertEqual(by_key[(1, 6)].name, "机器学习")
        self.assertEqual(by_key[(3, 6)].name, "高级计算机系统结构")
        self.assertEqual(by_key[(1, 6)].teacher, "胡书凡")

    def test_merge_consecutive_sections(self):
        """纵向合并：文档实测“周一 6-8 节机器学习”应合并为一条。"""
        courses = sample_courses()
        ml = [c for c in courses if c.name == "机器学习"]
        self.assertEqual(len(ml), 1)
        self.assertEqual((ml[0].weekday, ml[0].start_section, ml[0].end_section), (1, 6, 8))

    def test_timetable_matches_document(self):
        """端到端：样例 rows 解析结果与 4.2“实测 2026-2027-1 你的课表”一致。"""
        courses = sample_courses()

        def find(name, weekday):
            hits = [c for c in courses if c.name == name and c.weekday == weekday]
            self.assertEqual(len(hits), 1, f"{name} 周三应只有一条合并记录")
            return hits[0]

        self.assertEqual(find("高级计算机系统结构", 3).section_range, "3-4节")
        self.assertEqual(find("数据科学与工程", 3).section_range, "8-10节")
        self.assertEqual(find("最优化", 3).section_range, "11-13节")
        self.assertEqual(find("自然辩证法", 4).section_range, "4节")
        self.assertEqual(find("组合数学", 5).section_range, "3-5节")
        # 工程伦理在周六 3-5 节、周日 6-10 节
        sat = [c for c in courses if c.name == "工程伦理" and c.weekday == 6][0]
        sun = [c for c in courses if c.name == "工程伦理" and c.weekday == 7][0]
        self.assertEqual((sat.section_range, sun.section_range), ("3-5节", "6-10节"))

    def test_rows_shape_matches_api(self):
        """样例 rows 的结构必须与 4.2 接口一致：jcid 为字符串 1-13，z1..z7。"""
        for row in sample_rows():
            self.assertIn(int(row["jcid"]), range(1, 14))
            for k in ("z1", "z2", "z3", "z4", "z5", "z6", "z7"):
                self.assertIn(k, row)

    def test_unparseable_cell_kept_as_raw(self):
        rows = [{"jcid": "2", "mc": "第二节 08:50~09:30", "z1": "不明数据"}]
        courses = parse_rows(rows)
        self.assertEqual(len(courses), 1)
        self.assertEqual(courses[0].raw, "不明数据")

    def test_merge_disabled(self):
        # 每节课一条：3(ML)+2(高级)+3(数据)+3(最优化)+1(自然辩证法)+3(组合)
        #                    +3(工程周六)+5(工程周日) = 23 条
        courses = parse_rows(sample_rows(), merge=False)
        self.assertEqual(len(courses), 23)


class TestParseSectionsPayload(unittest.TestCase):
    def test_document_response(self):
        """3.4 getSection 完整响应 -> 13 个节次，时间与文档一致。"""
        sections = parse_sections_payload(load_fixture("portal_getSection.json"))
        self.assertEqual(len(sections), 13)
        self.assertEqual(sections[0].index, 1)
        self.assertEqual((sections[0].start_time, sections[0].end_time), ("08:00", "08:40"))
        self.assertEqual((sections[12].start_time, sections[12].end_time), ("20:40", "21:20"))
        self.assertEqual(sections[2].label, "三")


if __name__ == "__main__":
    unittest.main()
