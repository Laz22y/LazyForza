@echo off
setlocal
set "LAZYFORZA_RELEASE_DATA=%LOCALAPPDATA%\LazyForza-Release"
start "" "%~dp0LazyForza.App.exe" --data-dir "%LAZYFORZA_RELEASE_DATA%"
endlocal
