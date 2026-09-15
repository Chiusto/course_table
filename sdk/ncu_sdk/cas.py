"""ncu_sdk.cas —— 统一身份认证（docs/API_SPEC.md 第 2 节）。

流程：
    GET  service(my/gmsstu) -> 302 -> https://cas.ncu.edu.cn:8443/cas/login?service=...
    GET  /cas/jwt/publicKey  # encryptEnabled=false 时明文提交即可
    POST /cas/login?service=...   username / password / execution(+其余隐藏域)
    成功 -> 302 Location 含 ?ticket=ST-xxx；失败 -> 200 返回登录页

登录成功后把 ticket 回带到 service，即可在 Session 里建立对应的 Cookie
（门户的 SESSION/TGC，研究生系统的 cn_com_southsoft_gmis_stu）。
"""

from __future__ import annotations

import base64
import re
from typing import Any
from urllib.parse import parse_qs, urlparse

import requests

from .config import DEFAULT_TIMEOUT, Settings
from .errors import LoginError, NCUSDKError

# 登录页隐藏域
_HIDDEN_RE = re.compile(r'<input[^>]+type=["\']hidden["\'][^>]*>', re.I)
_ATTR_RE = re.compile(r'(name|value)=["\']([^"\']*)["\']', re.I)
# 登录页的各个 <form> 块（新版主题一页多表单，见 _pick_login_form_fields）
_FORM_RE = re.compile(r'<form\b[^>]*>.*?</form>', re.I | re.S)
_INPUT_NAME_RE = re.compile(r'<input\b[^>]*name=["\']([^"\']+)["\']', re.I)
# CAS 常见错误提示容器（含 mf 主题的 Vue el-alert 渲染结果）
_ERROR_RE = re.compile(
    r'(?:<el-alert[^>]*title=["\']([^"\']+)["\'][^>]*/?>)'
    r'|(?:<div[^>]*id=["\']?msg["\']?[^>]*>(.*?)</div>)'
    r'|(?:<span[^>]*class=["\'][^"\']*error[^"\']*["\'][^>]*>(.*?)</span>)',
    re.I | re.S,
)


class CASClient:
    """CAS 登录器。一个 CASClient 持有 requests.Session，可复用给多个系统。"""

    def __init__(self, settings: Settings | None = None, session: requests.Session | None = None) -> None:
        self.settings = settings or Settings()
        self.session = session or requests.Session()
        self.session.headers.update({"User-Agent": self.settings.user_agent})

    # ---------------------------------------------------------------- 底层请求
    def _request(self, method: str, url: str, **kw) -> requests.Response:
        kw.setdefault("timeout", self.settings.timeout)
        kw.setdefault("allow_redirects", True)
        kw.setdefault("verify", self.settings.verify_ssl)
        if self.settings.proxies:
            kw.setdefault("proxies", self.settings.proxies)
        return self.session.request(method, url, **kw)

    # ---------------------------------------------------------------- 公钥
    def get_public_key(self) -> dict[str, Any]:
        """GET /cas/jwt/publicKey。encryptEnabled=false 时无需加密密码。"""
        resp = self._request("GET", f"{self.settings.cas_base}/jwt/publicKey")
        resp.raise_for_status()
        try:
            return resp.json()
        except ValueError:
            return {"encryptEnabled": False, "publicKey": resp.text.strip()}

    @staticmethod
    def encrypt_password(password: str, public_key: str) -> str:
        """encryptEnabled=true 时用 RSA/ECB/PKCS1Padding 加密（需 cryptography）。

        返回 base64 字符串，可直接作为 password 字段提交。
        """
        try:
            from cryptography.hazmat.primitives import serialization
            from cryptography.hazmat.primitives.asymmetric import padding
        except ImportError as exc:  # pragma: no cover - 仅在开启加密时触发
            raise NCUSDKError("CAS 已开启密码加密，请安装 cryptography：pip install cryptography") from exc

        key = serialization.load_pem_public_key(_wrap_pem(public_key))
        encrypted = key.encrypt(password.encode("utf-8"), padding.PKCS1v15())
        return base64.b64encode(encrypted).decode("ascii")

    # ---------------------------------------------------------------- 登录
    def prepare(self, service: str) -> dict[str, str]:
        """访问受保护资源，跳到登录页并抽取隐藏域（execution/_eventId 等）。"""
        resp = self._request("GET", service)
        if "cas/login" not in resp.url:
            # 已有有效会话（Cookie 未过期），无需再次登录
            return {"__already_authenticated__": "1"}
        return _pick_login_form_fields(resp.text)

    def login(self, username: str, password: str, service: str) -> str:
        """执行 CAS 登录，返回 ticket（如 ST-xxx）。"""
        if not username or not password:
            from .errors import ConfigError

            raise ConfigError("缺少 CAS 用户名或密码（建议通过环境变量 NCU_USERNAME/NCU_PASSWORD 提供）")

        hidden = self.prepare(service)
        if hidden.get("__already_authenticated__"):
            return ""  # 会话仍然有效

        if "execution" not in hidden:
            raise LoginError("登录页未找到 execution 隐藏域，CAS 页面结构可能已变化")

        payload = dict(hidden)
        payload["username"] = username
        payload["password"] = self._maybe_encrypt(password)
        payload.setdefault("_eventId", "submit")
        payload.setdefault("submit", "登录")

        resp = self._request(
            "POST",
            f"{self.settings.cas_base}/login",
            params={"service": service},
            data=payload,
            allow_redirects=False,  # 需要从 Location 中取 ticket
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )

        location = resp.headers.get("Location", "")
        if resp.status_code in (301, 302, 303, 307) and "ticket=" in location:
            return parse_qs(urlparse(location).query).get("ticket", [""])[0]

        # 非重定向：登录失败（密码错误 / 验证码 / execution 失效）
        msg = _extract_error(resp.text) or f"CAS 登录失败（HTTP {resp.status_code}）"
        raise LoginError(msg, html=resp.text)

    def login_and_open(self, username: str, password: str, service: str) -> requests.Session:
        """登录并把 ticket 回带到 service，返回已建立会话的 Session。"""
        ticket = self.login(username, password, service)
        if ticket:
            sep = "&" if "?" in service else "?"
            self._request("GET", f"{service}{sep}ticket={ticket}")
        else:
            self._request("GET", service)
        return self.session

    # ---------------------------------------------------------------- 内部
    def _maybe_encrypt(self, password: str) -> str:
        """encryptEnabled=true 时加密密码，否则原样提交（文档实测为 false）。"""
        try:
            info = self.get_public_key()
        except requests.RequestException:
            return password  # 拿不到公钥时按明文处理
        if not (info.get("encryptEnabled") and info.get("publicKey")):
            return password
        return self.encrypt_password(password, str(info["publicKey"]))


# ------------------------------------------------------------------------ 工具函数
def _extract_hidden_fields(html: str) -> dict[str, str]:
    fields: dict[str, str] = {}
    for tag in _HIDDEN_RE.findall(html):
        attrs = dict(_ATTR_RE.findall(tag))
        if "name" in attrs:
            fields[attrs["name"]] = attrs.get("value", "")
    return fields


def _pick_login_form_fields(html: str) -> dict[str, str]:
    """选出“账号密码登录”表单的隐藏域，返回其 execution/_eventId 等字段。

    新版 CAS 主题（如南昌大学 mf 主题）同一页面有多个表单，各自持有独立的
    execution 与 _eventId：上网快速登录（submitDrcomIPLogin）、账号密码（submit）、
    免密令牌（submitPasswordlessToken）等。若把整页隐藏域拍平合并，_eventId 会取到
    最后出现的表单值，CAS 便按错误的 webflow 处理——密码再对也返回 401。
    因此必须按 <form> 逐块抽取，选同时含 username 与 password 输入、且
    _eventId=submit 的表单；老版单表单页面自然回退到拍平结果。
    """
    candidates: list[dict[str, str]] = []
    flat: dict[str, str] = {}
    for form in _FORM_RE.findall(html):
        fields = _extract_hidden_fields(form)
        flat.update(fields)
        if "execution" not in fields:
            continue
        names = set(fields) | set(_INPUT_NAME_RE.findall(form))
        if "username" in names and "password" in names:
            candidates.append(fields)
    if not candidates:
        return flat
    for fields in candidates:
        if fields.get("_eventId") == "submit":
            return fields
    # 没有显式 _eventId=submit 的（老版页面），补上标准提交事件
    candidates[0].setdefault("_eventId", "submit")
    return candidates[0]


def _extract_error(html: str) -> str | None:
    for groups in _ERROR_RE.findall(html):
        for g in groups:
            if g:
                text = re.sub(r"<[^>]+>", "", g).strip()
                if text:
                    return text
    return None


def _wrap_pem(public_key: str) -> bytes:
    body = public_key.strip()
    if "BEGIN" in body:
        return body.encode("utf-8")
    body = re.sub(r"\s+", "", body)
    return ("-----BEGIN PUBLIC KEY-----\n"
            + "\n".join(body[i:i + 64] for i in range(0, len(body), 64))
            + "\n-----END PUBLIC KEY-----\n").encode("utf-8")


__all__ = ["CASClient", "DEFAULT_TIMEOUT"]
