@echo off
rem Scroll benchmark: drag a PDF onto this file.
if "%~1"=="" goto usage
echo Running the scroll benchmark, do not touch the window...
start /wait "" "%~dp0PdfReader.exe" --bench "%~1"
type "%LocalAppData%\PdfReader\bench.log"
pause
goto :eof
:usage
echo Drag a PDF file onto this .bat
pause
