"""CAS 登录测试（docs/API_SPEC.md 第 2 节）。

重点覆盖南昌大学 mf 主题的“一页多表单”登录页：整页拍平合并隐藏域会取到
免密表单的 _eventId=submitPasswordlessToken，导致账号密码流程永远 401。
"""

from __future__ import annotations

import unittest
from pathlib import Path

import requests

from _path import FakeResponse, FakeSession, FIXTURES  # noqa: F401
from ncu_sdk.cas import CASClient, _extract_error, _pick_login_form_fields
from ncu_sdk.config import Settings
from ncu_sdk.errors import LoginError

MULTIFORM_HTML = (FIXTURES / "cas_login_multiform.html").read_text(encoding="utf-8")


class TestPickLoginFormFields(unittest.TestCase):
    def test_picks_password_form_not_passwordless(self):
        """必须选 fm1（_eventId=submit）而不是 fm2（submitPasswordlessToken）。"""
        fields = _pick_login_form_fields(MULTIFORM_HTML)
        self.assertEqual(fields["execution"], "EXEC-FM1-222")
        self.assertEqual(fields["_eventId"], "submit")

    def test_old_single_form_page_still_works(self):
        """老版单表单页面：无 username/password 输入名单独成表时回退拍平结果。"""
        html = (
            '<form method="post" action="login">'
            '<input type="hidden" name="execution" value="E-OLD"/>'
            '<input type="hidden" name="_eventId" value="submit"/>'
            '<input name="username"/><input name="password" type="password"/>'
            "</form>"
        )
        fields = _pick_login_form_fields(html)
        self.assertEqual(fields["execution"], "E-OLD")
        self.assertEqual(fields["_eventId"], "submit")

    def test_no_exec_form_falls_back_to_flat(self):
        """页面里连 execution 都没有时不抛错，返回拍平结果（后续报错更可读）。"""
        html = '<div>not a cas page</div>'
        self.assertEqual(_pick_login_form_fields(html), {})


class TestExtractError(unittest.TestCase):
    def test_el_alert_message(self):
        """mf 主题用 <el-alert title="..."> 渲染“账号或密码错误”。"""
        msg = _extract_error(MULTIFORM_HTML)
        self.assertEqual(msg, "账号或密码错误。")

    def test_legacy_msg_div(self):
        html = '<div id="msg">Invalid credentials.</div>'
        self.assertEqual(_extract_error(html), "Invalid credentials.")


class TestCASLogin(unittest.TestCase):
    def _client(self, session):
        settings = Settings(cas_base="https://cas.example.test/cas")
        return CASClient(settings=settings, session=session)

    def test_login_success_returns_ticket(self):
        """POST 302 到 service?ticket=ST-xxx -> 返回 ticket。"""
        session = FakeSession()
        session.on("GET", "svc.example.test", FakeResponse(
            text=MULTIFORM_HTML, url="https://cas.example.test/cas/login?service=https://svc.example.test/x"))
        session.on("POST", "cas.example.test", FakeResponse(
            status_code=302, headers={"Location": "https://svc.example.test/x?ticket=ST-123"}))
        client = self._client(session)
        ticket = client.login("u", "p", "https://svc.example.test/x")
        self.assertEqual(ticket, "ST-123")
        # 提交体必须带上 fm1 的 execution 与 _eventId=submit
        post = next(r for r in session.requests if r["method"] == "POST")
        self.assertEqual(post["data"]["execution"], "EXEC-FM1-222")
        self.assertEqual(post["data"]["_eventId"], "submit")
        self.assertEqual(post["data"]["username"], "u")

    def test_login_failure_surfaces_page_message(self):
        """401 + el-alert：抛 LoginError 且消息为页面真实提示，而不是裸 HTTP 码。"""
        session = FakeSession()
        session.on("GET", "svc.example.test", FakeResponse(
            text=MULTIFORM_HTML, url="https://cas.example.test/cas/login?service=https://svc.example.test/x"))
        session.on("POST", "cas.example.test", FakeResponse(
            status_code=401, text=MULTIFORM_HTML))
        client = self._client(session)
        with self.assertRaises(LoginError) as ctx:
            client.login("u", "bad-pass", "https://svc.example.test/x")
        self.assertIn("账号或密码错误", str(ctx.exception))

    def test_already_authenticated_short_circuits(self):
        """service 直接 200（未跳 cas/login）视为已登录，返回空 ticket。"""
        session = FakeSession()
        session.on("GET", "svc.example.test", FakeResponse(text="ok", url="https://svc.example.test/x"))
        client = self._client(session)
        self.assertEqual(client.login("u", "p", "https://svc.example.test/x"), "")


if __name__ == "__main__":
    unittest.main()
