"""ICS 导出与 CLI 离线演示测试。"""

import datetime
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

from _path import SDK_DIR  # noqa: F401
from ncu_sdk.demo import sample_courses, sample_sections
from ncu_sdk.ics import build_ics
from ncu_sdk.cli import main


class TestICS(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.term_start = datetime.date(2026, 8, 31)
        cls.sections = {s.index: s for s in sample_sections()}
        cls.ics = build_ics(sample_courses(), cls.term_start, cls.sections,
                            calendar_name="测试课表")

    def test_header(self):
        self.assertIn("BEGIN:VCALENDAR", self.ics)
        self.assertIn("X-WR-CALNAME:测试课表", self.ics)
        self.assertTrue(self.ics.rstrip().endswith("END:VCALENDAR"))

    def test_vevent_for_single_week_course(self):
        """工程伦理只在第 6 周（6-6 周）上：周六/周日各 1 条 VEVENT。"""
        self.assertEqual(self.ics.count("SUMMARY:工程伦理"), 2)

    def test_dtstart_of_ml_monday(self):
        """机器学习 第 1 周周一 6-8 节：2026-08-31 14:00-16:30。"""
        self.assertIn("DTSTART:20260831T140000", self.ics)
        self.assertIn("DTEND:20260831T163000", self.ics)

    def test_dtstart_next_week_shift(self):
        """机器学习 1-11 周 -> 第 2 周周一（09-07）也有记录。"""
        self.assertIn("DTSTART:20260907T140000", self.ics)

    def test_fold_line_length(self):
        for raw_line in self.ics.split("\r\n"):
            self.assertLessEqual(len(raw_line.encode("utf-8")), 75)


class TestCLIDemo(unittest.TestCase):
    def test_demo_command(self):
        """python -m ncu_sdk.cli demo 离线输出课表（全局参数置于子命令前）。"""
        buf = io.StringIO()
        with redirect_stdout(buf):
            code = main(["--db", ":memory:", "demo"])
        self.assertEqual(code, 0)
        out = buf.getvalue()
        self.assertIn("机器学习", out)
        self.assertIn("6-8节", out)
        self.assertIn("组合数学", out)

    def test_export_json_demo(self):
        """先 demo 落库到临时 db，再导出 JSON。"""
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            tmp_db = Path(tmp) / "demo.db"
            out_file = Path(tmp) / "demo.json"
            with redirect_stdout(io.StringIO()):
                code = main(["--db", str(tmp_db), "demo"])
            self.assertEqual(code, 0)
            with redirect_stdout(io.StringIO()):
                code = main(["--db", str(tmp_db), "export", "--term", "202620271",
                             "--format", "json", "--out", str(out_file)])
            self.assertEqual(code, 0)
            data = json.loads(out_file.read_text(encoding="utf-8"))
            self.assertEqual(len(data), 8)

    def test_show_json_after_demo(self):
        buf = io.StringIO()
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            tmp_db = Path(tmp) / "show.db"
            with redirect_stdout(io.StringIO()):
                main(["--db", str(tmp_db), "demo"])
            with redirect_stdout(buf):
                code = main(["--db", str(tmp_db), "show", "--term", "202620271",
                             "--week", "10", "--json"])
            self.assertEqual(code, 0)
            names = {item["name"] for item in json.loads(buf.getvalue())}
            self.assertIn("机器学习", names)
            self.assertIn("组合数学", names)


if __name__ == "__main__":
    unittest.main()
