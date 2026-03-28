@echo off
echo Installing Marklife Printer...

:: Создаём локальный порт (Named Pipe)
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\Local Port" /v "Ports" /t REG_MULTI_SZ /d "marklife-print\0\0" /f

:: Добавляем принтер через PowerShell
powershell -Command "Add-Printer -Name 'Marklife X2' -DriverName 'MS Publisher Imagesetter' -PortName 'marklife-print'"

echo.
echo Marklife Printer installed!
echo You can now print from any application.
pause
