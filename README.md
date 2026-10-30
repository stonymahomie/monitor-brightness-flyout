# Helligkeit

Kleines Windows-Tool, mit dem man die Helligkeit **externer Monitore einzeln** vom PC aus regelt. Windows kann das nur bei Laptop-Displays. Das Tool schickt die Befehle per **DDC/CI** über das Bildschirmkabel direkt an den Monitor.

- Symbol neben der Uhr, Popup im Stil der Windows-11-Schnelleinstellungen (abgerundet, halbdurchsichtig, Einflug-Animation, Regler in der Akzentfarbe)
- Ein Regler pro Monitor plus „Alle Monitore“
- Kommandozeile für Skripte, Tastenkürzel und geplante Aufgaben
- Keine Installation, keine Abhängigkeiten: eine einzelne `.exe`, gebaut mit dem C#-Compiler, der in Windows enthalten ist

## Bedienung

Doppelklick auf `Helligkeit.exe` startet das Programm mit einem Sonnen-Symbol im Infobereich (bei Windows 11 zuerst unter dem Pfeil `^`, von dort in die Taskleiste ziehen).

- **Linksklick:** Popup mit den Reglern. Mausrad über einer Zeile = ±2 %, `Esc` oder Klick daneben schließt es.
- **Rechtsklick:** „Alle auf 100/75/50/25 %“, „Monitore neu erkennen“, „Mit Windows starten“, „Beenden“.
- Ein zweiter Start öffnet nur das Popup der laufenden Instanz.

### Kommandozeile

```
Helligkeit.exe list        Monitore und aktuelle Werte anzeigen
Helligkeit.exe 2 50        Monitor 2 auf 50 %
Helligkeit.exe all 30      alle Monitore auf 30 %
Helligkeit.exe 3 +10       Monitor 3 um 10 % heller (-10 = dunkler)
```

Die Nummern zählen die Monitore von links nach rechts.

## Bauen

Benötigt nur Windows mit .NET Framework 4.x (bei Windows 10/11 vorhanden):

```
build.cmd
```

Das ruft den eingebauten Compiler auf (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). Er kann nur C# 5, also keine `$"…"`-Strings und kein `?.`.

## Voraussetzungen und Grenzen

- **DDC/CI muss im Menü des Monitors eingeschaltet sein.** Monitore ohne DDC/CI erscheinen als „kein DDC/CI“ und ihr Regler ist gesperrt.
- Über manche Docking-Stationen, Adapter oder KVM-Switches kommen die Befehle nicht beim Monitor an.
- DDC/CI ist langsam (etwa 50–100 ms pro Befehl). Deshalb wird erst gesendet, wenn ein Regler kurz ruht.
- Laptop-Displays werden nicht unterstützt (die laufen nicht über DDC/CI).

## Technik

- Helligkeit: `dxva2.dll` (`GetMonitorBrightness` / `SetMonitorBrightness`)
- Monitornamen: EDID-Daten über WMI (`WmiMonitorID`)
- Popup: selbst gezeichnet mit GDI+ als Layered Window (`UpdateLayeredWindow`), Unschärfe über `SetWindowCompositionAttribute`
- Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, Wert `Helligkeit`
- `Helligkeit.exe --render bild.png` speichert das Popup als Bild (zum Prüfen ohne Bildschirm)
