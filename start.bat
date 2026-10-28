@echo off
rem ============================================================
rem  Timetable - Windows launcher (start.sh is equivalent)
rem  Usage: start.bat [app|demo|sdk|build|help]
rem ============================================================
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "EXE_REL=desktop\NcuCourseTable.Desktop"
if defined NCU_SDK_DIR ( set "SDK_DIR=%NCU_SDK_DIR%" ) else ( set "SDK_DIR=%ROOT%\sdk" )

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
echo [error] unknown command: %CMD%
echo Usage: start.bat [app^|demo^|sdk^|build^|help]
pause >nul
exit /b 1

rem ============================================================ app
:app
call :pick_exe
if errorlevel 1 (
  echo [error] build output not found. Run: start.bat build
  goto :err_end
)
set "HAS_DATA=0"
if not "%REST%"=="" for %%x in (%REST%) do (
  if /i "%%~x"=="--data" set "HAS_DATA=1"
  if /i "%%~x"=="-d"     set "HAS_DATA=1"
)
echo [timetable] exe: %EXE%
if "!HAS_DATA!"=="1" (
  start "" /D "%ROOT%" "%EXE%" !REST!
) else (
  echo [timetable] data dir: %ROOT%\data  ^(override with --data^)
  start "" /D "%ROOT%" "%EXE%" -d "%ROOT%\data" !REST!
)
echo [timetable] Tips:
echo   * Default mode: tray + desktop widget. Use "start.bat app --window" for the main window.
echo   * Course sync: main window - Plugins - NCU Course Source.
echo   * Old courses.json is migrated to schedule.json automatically.
exit /b 0

rem ============================================================ demo (SDK only)
:demo
call :resolve_python
if errorlevel 1 goto :py_missing
call :ensure_deps
if not exist "%ROOT%\data" mkdir "%ROOT%\data"
echo [timetable] running ncu_sdk offline demo...
set "PYTHONPATH=%SDK_DIR%"
call :py_run -m ncu_sdk.cli --db "%ROOT%\data\demo.db" demo %REST%
if errorlevel 1 goto :err_end
echo.
echo [done] demo database written. This is an SDK sample, not the app data file.
pause >nul
exit /b 0

rem ============================================================ sdk passthrough
:sdk
if "%REST%"=="" (
  echo [error] missing args. Example: start.bat sdk today
  goto :err_end
)
call :resolve_python
if errorlevel 1 goto :py_missing
call :ensure_deps
echo [timetable] ncu_sdk.cli %REST%  ^(SDK: %SDK_DIR%^)
set "PYTHONPATH=%SDK_DIR%"
call :py_run -m ncu_sdk.cli %REST%
exit /b %errorlevel%

rem ============================================================ build
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
    echo [error] dotnet not found. Install .NET SDK 8+.
    goto :err_end
  )
) else (
  set "DOTNET=dotnet"
)
echo [timetable] dotnet build desktop\Timetable.sln -c %CFG%
"%DOTNET%" build "%ROOT%\desktop\Timetable.sln" -c %CFG% %EXTRA%
exit /b %errorlevel%

rem ============================================================ help
:help
echo Timetable - launcher
echo.
echo Usage:
echo   start.bat [app] [args...]   Start desktop widget mode by default
echo   start.bat app --window      Start the full main window
echo   start.bat app --host bottom Desktop host: bottom (default) / wallpaper (experimental)
echo   start.bat demo              Run ncu_sdk offline demo (SDK sample only)
echo   start.bat sdk ^<args...^>    Run ncu_sdk.cli, e.g. start.bat sdk today
echo   start.bat build [--debug]   Build host + plugins (default Release)
echo   start.bat help              Show this help
echo.
echo Notes:
echo   * Data dir defaults to .\data; override with --data or TIMETABLE_DATA.
echo   * Course sync is provided by the bundled plugin "NcuCourseSource".
echo   * Plugin needs Python + sdk\ncu_sdk; env: NCU_PYTHON / NCU_SDK_DIR / NCU_VENDOR_DIR.
echo   * Campus network or VPN is required for NCU login.
exit /b 0

rem ============================================================ helpers

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
echo [hint] Python is missing requests. Installing from requirements.txt...
set /p ANS=Continue with pip install? [y/N]
if /i not "%ANS%"=="y" if /i not "%ANS%"=="yes" (
  echo [error] cancelled. Run: python -m pip install -r "%ROOT%\requirements.txt"
  exit /b 1
)
call :py_run -m pip install -r "%ROOT%\requirements.txt"
if errorlevel 1 (
  echo [error] dependency installation failed
  exit /b 1
)
exit /b 0

:py_missing
echo [error] Python not found. Set NCU_PYTHON to a python.exe path.
goto :err_end

:err_end
pause >nul
exit /b 1
