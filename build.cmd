@echo off
rem Builds BrightnessFlyout.exe with the C# compiler that ships with Windows.
rem Close a running BrightnessFlyout.exe first (the file is locked otherwise).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /out:BrightnessFlyout.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll BrightnessFlyout.cs
