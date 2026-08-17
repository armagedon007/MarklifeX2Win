# ============================================
# Install IPP Printer "X2 Print Label" with port 9200
# Run as Administrator
# ============================================

$printerName = "X2 Print Label"
$printerIP = "127.0.0.1"
$portNumber = 9200
$portName = "Marklife_$printerIP"
#"http://$printerIP`:$portNumber/$printerName"

Write-Host "=== Removing old printer ===" -ForegroundColor Cyan
Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
Remove-PrinterPort -Name $portName -ErrorAction SilentlyContinue


Write-Host "=== Creating TCP/IP port $portNumber ===" -ForegroundColor Cyan

$port = Get-WmiObject -Class Win32_TCPIPPrinterPort -Namespace "root\cimv2" | Where-Object { $_.Name -eq $portName }
if ($port) {
    $port.Delete()
}
$newPort = ([WMIClass] "root\cimv2:Win32_TCPIPPrinterPort").CreateInstance()
$newPort.Name = $portName
$newPort.HostAddress = $printerIP
$newPort.PortNumber = $portNumber
$newPort.Protocol = 1
$newPort.Put()

Write-Host "Port created: $portName (port $portNumber)" -ForegroundColor Green

Write-Host "=== Creating printer ===" -ForegroundColor Cyan
Add-Printer -Name $printerName -PortName $portName -DriverName "Microsoft IPP Class Driver"
#Add-Printer -Name $printerName -PortName $portName -DriverName "Microsoft Print To PDF"


 $newPort = ([WMIClass] ''root\cimv2:Win32_TCPIPPrinterPort'').CreateInstance(); 
 $newPort.Name = ''Marklife_127.0.0.1''; 
 $newPort.HostAddress = ''127.0.0.1''; 
 $newPort.PortNumber = 9200; 
 $newPort.Protocol = 1; 
 $newPort.Put(); 
 Add-Printer -Name ''X2 Print Label'' -PortName ''Marklife_127.0.0.1'' -DriverName ''Microsoft IPP Class Driver''; 
 Restart-Service Spooler -Force -ErrorAction SilentlyContinue';



Write-Host "=== Copying GPD ===" -ForegroundColor Cyan
$gpdPath = "C:\Windows\System32\spool\drivers\"

# Получаем путь к папке драйвера из реестра
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers\$printerName"
$systemRoot = [System.Environment]::GetEnvironmentVariable("SystemRoot")
$v4Dir = Join-Path "$systemRoot\System32\spool\V4Dirs" (Get-ItemProperty -Path $regPath -Name "PrintQueueV4DriverDirectory" -ErrorAction SilentlyContinue).PrintQueueV4DriverDirectory
if (-not $v4Dir) {
    Write-Host "Printer not found or dont v4 driver" -ForegroundColor Red
    exit 1
}

Write-Host "Driver folder: $v4Dir" -ForegroundColor Cyan

# Копируем GPD
Copy-Item -Path "../Driver/IPP/Marklife_X2.gpd" -Destination "$v4Dir\Marklife_X2.gpd" -Force

# Меняем имя конфиг файла в реестре
Set-ItemProperty -Path "$regPath\PrinterDriverData" -Name "V4_Merged_ConfigFile_Name" -Value "Marklife_X2.gpd"


Write-Host "=== Creating forms in registry ===" -ForegroundColor Cyan

$sizes = @(
    @{Name="20mm x 20mm"; W=20000; H=20000},
    @{Name="40mm x 30mm"; W=30000; H=40000},
    @{Name="43mm x 25mm"; W=25000; H=43000},
    @{Name="50mm x 30mm"; W=30000; H=50000},
    @{Name="60mm x 40mm"; W=40000; H=60000},
    @{Name="80mm x 60mm"; W=60000; H=80000},
    @{Name="100mm x 80mm"; W=80000; H=100000}
)

$formsKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Forms"

foreach ($s in $sizes) {
    # First key
    $key1 = Join-Path $formsKey $s.Name
    New-Item -Path $key1 -Force | Out-Null
    Set-ItemProperty -Path $key1 -Name "FormKeyword" -Value ([System.Guid]::NewGuid().ToByteArray())
    
    # Second key
    $bytes = @()
    $bytes += [BitConverter]::GetBytes($s.W)
    $bytes += [BitConverter]::GetBytes($s.H)
    $bytes += @(0,0,0,0,0,0,0,0)
    $bytes += [BitConverter]::GetBytes($s.W)
    $bytes += [BitConverter]::GetBytes($s.H)
    $bytes += @(0x9d,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
    
    Set-ItemProperty -Path $formsKey -Name $s.Name -Value ([byte[]]$bytes)
    
    Write-Host "Added: $($s.Name)" -ForegroundColor Green
}

Restart-Service Spooler -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "DONE!" -ForegroundColor Green