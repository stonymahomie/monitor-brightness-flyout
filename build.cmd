@echo off
rem Baut Helligkeit.exe mit dem in Windows enthaltenen C#-Compiler.
rem Ein laufendes Helligkeit.exe vorher beenden (die Datei ist sonst gesperrt).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /out:Helligkeit.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll Helligkeit.cs
