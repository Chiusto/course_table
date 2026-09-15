"""ncu_sdk.errors —— 异常体系。

异常按“故障来源”分层，便于上层区分处理：
- 认证类（CAS / token / Cookie）：需要重新登录
- 业务类（接口返回 code != 0）：通常是参数或权限问题
- 网络/解析类：可重试或需要人工介入（如混淆 URL 升级）
"""

from __future__ import annotations


class NCUSDKError(Exception):
    """SDK 所有异常的基类。"""


# ---------------------------------------------------------------- 认证相关
class AuthError(NCUSDKError):
    """认证失败基类（需要重新登录）。"""


class LoginError(AuthError):
    """CAS 登录失败：账号/密码错误、验证码、execution 失效等。"""

    def __init__(self, message: str, html: str | None = None) -> None:
        super().__init__(message)
        self.html = html


class TokenNotFoundError(AuthError):
    """未能从门户响应中换取到 x-id-token。

    docs/API_SPEC.md 3.1 说明 token 由前端写入 localStorage。自动化登录时
    只能从 Set-Cookie / HTML 内联脚本里猜测，失败时要求调用方手动传入。
    """


class GmsstuSessionError(AuthError):
    """研究生系统 Cookie（cn_com_southsoft_gmis_stu）缺失或失效。"""


# ---------------------------------------------------------------- 接口相关
class ApiError(NCUSDKError):
    """portal-api / gmsstu 返回了非预期的业务结果。"""

    def __init__(self, message: str, code: int | None = None, payload: object = None) -> None:
        super().__init__(message)
        self.code = code
        self.payload = payload


class PermissionError_(ApiError):
    """对应 portal-api 的 {"code": -1, "message": "没有访问权限01"}。"""

    def __init__(self, message: str = "没有访问权限（缺少或过期的 x-id-token）", **kw) -> None:
        super().__init__(message, **kw)


class EndpointOutdatedError(ApiError):
    """gmsstu 混淆 URL 升级（返回 HTML 而非 JSON）时抛出。

    docs/API_SPEC.md 6：升级后重新抓包 POST 01E8D44... 即可，只需替换
    Settings.gmsstu_obfuscated_path。
    """


# ---------------------------------------------------------------- 数据相关
class ParseError(NCUSDKError):
    """单元格 / 周次 / 响应结构解析失败。"""


class ConfigError(NCUSDKError):
    """缺少必要配置（如未提供凭据或学期起始日期）。"""
