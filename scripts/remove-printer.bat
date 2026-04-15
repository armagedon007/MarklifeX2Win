@echo off
powershell -ExecutionPolicy Bypass -Command "Remove-Printer -Name 'X2 Print Label' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name 'MarklifePort' -ErrorAction SilentlyContinue; Write-Host 'Done'"
pause
