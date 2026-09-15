"""测试辅助：把 sdk/ 加入 sys.path，并提供可注入的假 Session。"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parents[1]
SDK_DIR = ROOT / "sdk"
FIXTURES = Path(__file__).resolve().parent / "fixtures"

if str(SDK_DIR) not in sys.path:
    sys.path.insert(0, str(SDK_DIR))


def load_fixture(name: str):
    """读取 tests/fixtures 下的样例响应（内容取自 docs/API_SPEC.md）。"""
    with open(FIXTURES / name, "r", encoding="utf-8") as f:
        return json.load(f)


class FakeResponse:
    """最小 requests.Response 替身。"""

    def __init__(self, payload=None, text: str = "", status_code: int = 200,
                 headers: dict | None = None, url: str = "") -> None:
        self._payload = payload
        self.text = text if text else (json.dumps(payload, ensure_ascii=False) if payload is not None else "")
        self.status_code = status_code
        self.headers = headers or {}
        self.url = url

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise requests.HTTPError(f"HTTP {self.status_code}")

    def json(self):
        if self._payload is None:
            raise ValueError("no json")
        return self._payload


class FakeSession:
    """可预设响应的 Session 替身，记录请求以便断言（如必须携带的请求头）。"""

    def __init__(self, cookie_name=None, cookie_value="dummy") -> None:
        self.headers = {}  # 与 requests.Session 对齐（CASClient 会写入 User-Agent）
        self.cookies = requests.cookies.RequestsCookieJar()
        if cookie_name:
            self.cookies.set(cookie_name, cookie_value)
        self.requests: list[dict] = []
        self._handlers: list = []

    # ---- 预设 ----
    def on(self, method: str, url_contains: str, response: FakeResponse) -> "FakeSession":
        self._handlers.append((method.upper(), url_contains, response))
        return self

    def _match(self, method: str, url: str) -> FakeResponse | None:
        for m, frag, resp in self._handlers:
            if m == method.upper() and frag in url:
                return resp
        return None

    # ---- requests.Session 接口 ----
    def request(self, method, url, **kw):
        self.requests.append({"method": method, "url": url, **kw})
        resp = self._match(method, url)
        if resp is None:
            return FakeResponse(payload={"code": 0, "data": {}}, url=url)
        return resp

    def get(self, url, **kw):
        return self.request("GET", url, **kw)

    def post(self, url, **kw):
        return self.request("POST", url, **kw)

    @property
    def last_headers(self) -> dict:
        return self.requests[-1].get("headers", {}) if self.requests else {}
