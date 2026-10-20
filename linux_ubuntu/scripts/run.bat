@echo off
setlocal
for %%I in ("%~dp0..") do set "LINKLAB_DIR=%%~fI"
set "LINKLAB_PYTHON=%LINKLAB_DIR%\.venv\Scripts\python.exe"
set "LINKLAB_PYTHONW=%LINKLAB_DIR%\.venv\Scripts\pythonw.exe"

if /I "%~1"=="--help" goto help
if not exist "%LINKLAB_PYTHON%" goto missing_environment
if not exist "%LINKLAB_PYTHONW%" goto missing_environment
if not exist "%LINKLAB_DIR%\main.py" goto missing_application
if /I "%~1"=="--debug" goto debug

start "" /D "%LINKLAB_DIR%" "%LINKLAB_PYTHONW%" "%LINKLAB_DIR%\main.py" %*
exit /b %errorlevel%

:debug
pushd "%LINKLAB_DIR%"
"%LINKLAB_PYTHON%" -u "%LINKLAB_DIR%\main.py"
set "LINKLAB_RESULT=%errorlevel%"
popd
if not "%LINKLAB_RESULT%"=="0" pause
exit /b %LINKLAB_RESULT%

:missing_environment
echo Python environment not found in:
echo   "%LINKLAB_DIR%\.venv"
echo Open PowerShell and run setup.ps1 with Python 3.10-3.13 first.
echo Example: .\setup.ps1 -Python C:\path\to\python.exe
pause
exit /b 1

:missing_application
echo Missing application: "%LINKLAB_DIR%\main.py"
echo Keep the complete linux_ubuntu folder together.
pause
exit /b 1

:help
echo Device Debug Tools 1.0 - Windows launcher for the Python Qt edition
echo.
echo Double-click run.bat, or run it from any CMD directory.
echo   run.bat                Open Device Debug Tools without a console window.
echo   run.bat --background   Start in the tray when available.
echo   run.bat --debug        Keep Python errors visible in this console.
exit /b 0
