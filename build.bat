@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler.
  exit /b 1
)

if not exist "dist" mkdir "dist"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /win32manifest:src\MyQuoteCard\app.manifest ^
  /win32icon:src\MyQuoteCard\card-orange.ico ^
  /out:dist\MyQuoteCard.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  src\MyQuoteCard\Program.cs ^
  src\MyQuoteCard\Storage.cs ^
  src\MyQuoteCard\Forms.cs ^
  src\MyQuoteCard\AssemblyInfo.cs

if errorlevel 1 exit /b %errorlevel%
echo Built: dist\MyQuoteCard.exe

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /win32manifest:src\MyQuoteCard\app.manifest ^
  /win32icon:src\Launcher\launcher.ico ^
  /out:dist\QuoteCardLauncher.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  src\Launcher\Launcher.cs

if errorlevel 1 exit /b %errorlevel%
echo Built: dist\QuoteCardLauncher.exe

