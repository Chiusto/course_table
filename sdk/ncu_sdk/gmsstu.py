"""ncu_sdk.gmsstu —— 研究生系统真课表（docs/API_SPEC.md 第 4 节）。

做课程表软件必须接 gmsstu：ScheduleCenter 只能做节次表/教学周辅助（第 1 节）。

认证（4.1）：iframe 需 CAS SSO Cookie cn_com_southsoft_gmis_stu。
接口（4.2）：POST 混淆 URL，Content-Type: application/x-www-form-urlencoded，
            body kblx=xs&termcode=202620271，头带 X-Requested-With: XMLHttpRequest。

注意：混淆路径并非静态——实测为「固定前缀 + 固定 hex 头 + 随登录会话轮换的 hex 尾」，
旧路径在任何新会话里只会得到 {"code":500} 或 404。因此静态路径失败时自动执行
会话内端点发现（拉首页 → 逐页找含 kblx 的课表页 → 提取其 POST url）。
"""

from __future__ import annotations

import re
from typing import Any

import requests

from .config import GMSSTU_KBLX, GMSSTU_COOKIE, Settings
from .errors import EndpointOutdatedError, GmsstuSessionError
from .models import Course
from .parser import parse_rows

# 从配置的混淆路径推导“固定前缀”（去掉末尾的 hex 串），用于在首页里找同族端点
_HEX_TAIL_RE = re.compile(r"^(.*?)([0-9A-Fa-f]+)$")
# 课表页里课表查询的 ajax 声明：url: '/TXlIZWFyd...' , data: { 'kblx': 'xs', ... }
_KB_ENDPOINT_RE = re.compile(
    r"url\s*:\s*['\"]([^'\"]+)['\"]\s*,[^{}]{0,160}?data\s*:\s*\{[^}]*?['\"]kblx['\"]",
    re.I | re.S,
)


class GmsstuClient:
    """研究生系统课表客户端。"""

    def __init__(
        self,
        settings: Settings | None = None,
        session: requests.Session | None = None,
    ) -> None:
        self.settings = settings or Settings()
        self.session = session or requests.Session()
        self._kb_url: str | None = None  # 本次会话发现的真实课表端点

    # ---------------------------------------------------------------- 认证
    def login(self, username: str, password: str) -> requests.Session:
        """CAS SSO 登录 gmsstu，建立 cn_com_southsoft_gmis_stu Cookie。"""
        from .cas import CASClient

        session = CASClient(self.settings, self.session).login_and_open(
            username, password, self.settings.gmsstu_index
        )
        self.session = session
        self.require_session()
        return session

    def has_session(self) -> bool:
        return any(c.name == GMSSTU_COOKIE for c in self.session.cookies)

    def require_session(self) -> None:
        if not self.has_session():
            raise GmsstuSessionError(
                f"缺少 Cookie {GMSSTU_COOKIE}，请先调用 login() 或传入已登录的 Session"
            )

    # ---------------------------------------------------------------- 课表
    def fetch_rows(self, termcode: str, kblx: str = GMSSTU_KBLX) -> list[dict]:
        """4.2 原始 rows。返回 [{"jcid","mc","sjbz","z1".."z7"}, ...]。

        先用已知端点（上次发现的或配置里的静态路径）请求；响应不是含 rows 的
        JSON 时按会话重新发现端点再试一次，仍失败才抛 EndpointOutdatedError。
        """
        self.require_session()
        rows = self._try_rows(self._kb_url or self.settings.gmsstu_kb_url, termcode, kblx)
        if rows is None:
            self._kb_url = self.discover_kb_url()
            rows = self._try_rows(self._kb_url, termcode, kblx)
        if rows is None:
            raise EndpointOutdatedError(
                "gmsstu 课表端点不可用：混淆路径随会话轮换且自动发现失败。"
                "请重新登录后再试；若仍失败请抓包更新 Settings.gmsstu_obfuscated_path"
                "（docs 4.2：POST .../01E8D44...）"
            )
        return rows

    def _try_rows(self, url: str | None, termcode: str, kblx: str) -> list[dict] | None:
        """POST 课表查询并解析 rows；任何异常/结构不符都返回 None（交给上层兜底）。"""
        if not url:
            return None
        try:
            resp = self.session.post(
                url,
                data={"kblx": kblx, "termcode": termcode},
                headers={
                    "Content-Type": "application/x-www-form-urlencoded",
                    "X-Requested-With": "XMLHttpRequest",
                    "User-Agent": self.settings.user_agent,
                    "Referer": f"{self.settings.gmsstu_base.rstrip('/')}/index",
                },
                timeout=self.settings.timeout,
                verify=self.settings.verify_ssl,
            )
            resp.raise_for_status()
            payload = resp.json()
        except (requests.RequestException, ValueError):
            return None
        rows = payload.get("rows") if isinstance(payload, dict) else payload
        return rows if isinstance(rows, list) else None

    def discover_kb_url(self) -> str | None:
        """会话内重新发现课表查询端点；失败返回 None。

        路径：GET 已登录首页 → 抽取全部混淆路径 → 逐个 GET 找到含 kblx 的课表页
        → 从其 ajax 声明里提取 POST url。
        """
        base = self.settings.gmsstu_base.rstrip("/")
        try:
            resp = self.session.get(
                self.settings.gmsstu_index,
                timeout=self.settings.timeout,
                verify=self.settings.verify_ssl,
            )
            resp.raise_for_status()
        except requests.RequestException:
            return None
        if "cas/login" in resp.url:  # 会话已失效，被重定向回登录页
            raise GmsstuSessionError(
                f"gmsstu 会话已失效（首页跳回 CAS 登录页），请重新调用 login()"
            )
        match = _HEX_TAIL_RE.match(self.settings.gmsstu_obfuscated_path)
        prefix = match.group(1) if match else self.settings.gmsstu_obfuscated_path
        candidates = sorted(set(
            re.findall(rf"/{re.escape(prefix)}[0-9A-Fa-f]+", resp.text)
        ))
        for path in candidates:
            try:
                page = self.session.get(
                    f"{base}{path}",
                    timeout=self.settings.timeout,
                    verify=self.settings.verify_ssl,
                )
            except requests.RequestException:
                continue
            if "kblx" not in page.text:
                continue
            hit = _KB_ENDPOINT_RE.search(page.text)
            if not hit:
                continue
            url = hit.group(1)
            return url if url.startswith("http") else f"{base}{url}"
        return None

    def fetch_courses(self, termcode: str, kblx: str = GMSSTU_KBLX) -> list[Course]:
        """拉取并解析为 Course 列表（已合并连续节次）。"""
        return parse_rows(self.fetch_rows(termcode, kblx), termcode=termcode)

    def fetch_sections(self, termcode: str) -> dict[int, Any]:
        """从 rows[].mc 提取节次时间表（portal getSection 不可用时的兜底）。"""
        from .parser import parse_mc

        sections = {}
        for row in self.fetch_rows(termcode):
            section = parse_mc(row.get("mc"), row.get("jcid"))
            if section.index:
                sections[section.index] = section
        return sections
