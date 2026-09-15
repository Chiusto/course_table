@echo off
rem ============================================================
rem 南昌大学课程表 —— 一键启动脚本 (Windows CMD 版)
rem 与根目录 start.sh 等价；用法见 start.bat help
rem 注意: 本文件以 GBK(ANSI) 编码保存，勿改为 UTF-8
rem ============================================================
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "EXE_REL=desktop\NcuCourseTable.Desktop"
if defined NCU_SDK_DIR ( set "SDK_DIR=%NCU_SDK_DIR%" ) else ( set "SDK_DIR=%ROOT%\sdk" )

rem ---------- 解析首个子命令，重建剩余参数 REST ----------
set "CMD=%~1"
if "%CMD%"=="" set "CMD=app"
set "REST="
set "SKIP_FIRST=1"
:arg_loop
shift
if "%~1"=="" goto :dispatch
if defined SKIP_FIRST ( set "SKIP_FIRST=" ) else ( set "REST=!REST! "%~1"" )
goto :arg_loop

:dispatch
if /i "%CMD%"=="app"   goto :app
if /i "%CMD%"=="demo"  goto :demo
if /i "%CMD%"=="sdk"   goto :sdk
if /i "%CMD%"=="build" goto :build
if /i "%CMD%"=="help"  goto :help
if /i "%CMD%"=="-h"    goto :help
echo [错误] 未知命令: %CMD%
echo 用法: start.bat [app^|demo^|sdk^|build^|help]    详细帮助: start.bat help
pause >nul
exit /b 1

rem ============================================================ app: 桌面组件
:app
call :pick_exe
if errorlevel 1 (
  echo [错误] 未找到编译产物，请先执行: start.bat build
  goto :err_end
)
set "HAS_DATA=0"
if not "%REST%"=="" for %%x in (%REST%) do (
  if /i "%%~x"=="--data" set "HAS_DATA=1"
  if /i "%%~x"=="-d"     set "HAS_DATA=1"
)
echo [启动] 桌面组件: %EXE%
if "!HAS_DATA!"=="1" (
  start "" /D "%ROOT%" "%EXE%" !REST!
) else (
  echo [启动] 数据目录: %ROOT%\data（可用 --data 目录 覆盖）
  start "" /D "%ROOT%" "%EXE%" -d "%ROOT%\data" !REST!
)
echo [启动] 若课表为空: 右键托盘 ->「登录并刷新课表」拉取真课表；或先执行: start.bat app --demo
exit /b 0

rem ============================================================ demo: SDK 离线演示
:demo
call :resolve_python
if errorlevel 1 goto :py_missing
call :ensure_deps
if errorlevel 1 goto :err_end
if not exist "%ROOT%\data" mkdir "%ROOT%\data"
echo [启动] 运行离线演示（样例取自 docs\API_SPEC.md 实测课表）...
set "PYTHONPATH=%SDK_DIR%"
call :py_run -m ncu_sdk.cli --db "%ROOT%\data\demo.db" demo %REST%
if errorlevel 1 goto :err_end
echo.
echo [完成] 演示结束。可继续执行: start.bat app --demo 查看桌面卡片。
pause >nul
exit /b 0

rem ============================================================ sdk: CLI 透传
:sdk
if "%REST%"=="" (
  echo [错误] 缺少子命令。示例: start.bat sdk today    或    start.bat sdk sync --term 202620271
  goto :err_end
)
call :resolve_python
if errorlevel 1 goto :py_missing
call :ensure_deps
if errorlevel 1 goto :err_end
echo [启动] ncu_sdk.cli %REST%  （SDK: %SDK_DIR%）
set "PYTHONPATH=%SDK_DIR%"
call :py_run -m ncu_sdk.cli %REST%
exit /b %errorlevel%

rem ============================================================ build: 构建桌面组件
:build
set "CFG=Release"
set "EXTRA="
if not "%REST%"=="" for %%x in (%REST%) do (
  if /i "%%~x"=="--debug" ( set "CFG=Debug" ) else ( set "EXTRA=!EXTRA! %%x" )
)
where dotnet >nul 2>&1
if errorlevel 1 (
  if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
    set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
  ) else (
    echo [错误] 未找到 dotnet（.NET SDK 8+），请安装或加入 PATH
    goto :err_end
  )
) else (
  set "DOTNET=dotnet"
)
echo [启动] dotnet build -c %CFG%
"%DOTNET%" build "%ROOT%\%EXE_REL%\NcuCourseTable.Desktop.csproj" -c %CFG% %EXTRA%
exit /b %errorlevel%

rem ============================================================ help
:help
echo 南昌大学课程表 —— 一键启动脚本（Windows CMD 版）
echo.
echo 用法:
echo   start.bat [app] [参数...]    启动桌面组件（默认动作，参数透传）
echo   start.bat app --demo         内置样例数据启动桌面组件（无需账号/网络）
echo   start.bat app --window       打开完整管理窗口（旧行为）
echo   start.bat app --host bottom  指定宿主层（bottom=图标之上[默认]；wallpaper=壁纸层[WPF 下不可见]）
echo   start.bat demo               SDK 离线演示：内置样例课表（无需登录）
echo   start.bat sdk ^<命令...^>     透传 SDK 命令行，如: start.bat sdk today / sync --term 202620271
echo   start.bat build [--debug]    构建桌面组件（默认 Release）
echo   start.bat help               显示本帮助
echo.
echo 说明:
echo   * 桌面组件常驻托盘；退出请用托盘菜单。重复启动会自动提示"已在运行"。
echo   * 桌面数据目录默认指向仓库 data\；可用 --data 目录 或 NCU_COURSE_DATA 覆盖。
echo   * Python/SDK 路径约定: NCU_PYTHON / NCU_SDK_DIR 环境变量优先，其次 PATH 探测。
echo   * SDK 网络登录需校园网/VPN，凭据走 NCU_USERNAME / NCU_PASSWORD 环境变量。
exit /b 0

rem ============================================================ 子过程

:pick_exe
if exist "%ROOT%\%EXE_REL%\bin\Release\net8.0-windows\NcuCourseTable.Desktop.exe" (
  set "EXE=%ROOT%\%EXE_REL%\bin\Release\net8.0-windows\NcuCourseTable.Desktop.exe"
  exit /b 0
)
if exist "%ROOT%\%EXE_REL%\bin\Debug\net8.0-windows\NcuCourseTable.Desktop.exe" (
  set "EXE=%ROOT%\%EXE_REL%\bin\Debug\net8.0-windows\NcuCourseTable.Desktop.exe"
  exit /b 0
)
set "EXE="
exit /b 1

:resolve_python
if defined NCU_PYTHON (
  set "PYEXE=%NCU_PYTHON%"
  set "PYL=0"
  exit /b 0
)
where python >nul 2>&1
if not errorlevel 1 ( set "PYEXE=python" & set "PYL=0" & exit /b 0 )
where py >nul 2>&1
if not errorlevel 1 ( set "PYEXE=py" & set "PYL=1" & exit /b 0 )
set "PYEXE="
exit /b 1

:py_run
if "%PYL%"=="1" (
  py -3 %*
) else (
  if defined NCU_PYTHON ( "%PYEXE%" %* ) else ( %PYEXE% %* )
)
exit /b 0

:ensure_deps
call :py_run -c "import requests" >nul 2>&1
if not errorlevel 1 exit /b 0
echo [提示] Python 缺少依赖 requests（见仓库 requirements.txt）。
set /p ANS=是否执行 pip install -r requirements.txt 安装？[y/N] 
if /i not "%ANS%"=="y" if /i not "%ANS%"=="yes" (
  echo [错误] 已取消。请手动执行: python -m pip install -r "%ROOT%\requirements.txt"
  exit /b 1
)
call :py_run -m pip install -r "%ROOT%\requirements.txt"
if errorlevel 1 (
  echo [错误] 依赖安装失败
  exit /b 1
)
exit /b 0

:py_missing
echo [错误] 未找到 Python，请设置环境变量 NCU_PYTHON 指定解释器
goto :err_end

:err_end
pause >nul
exit /b 1
