import Foundation
import CoreBluetooth
import Combine

/// Менеджер Bluetooth для работы с принтерами
class BluetoothManager: NSObject, ObservableObject {
    @Published var discoveredDevices: [PrinterDevice] = []
    @Published var connectedDevice: PrinterDevice?
    @Published var isScanning = false
    @Published var bluetoothState: CBManagerState = .unknown
    @Published var currentLoadingParameter: String? = nil // Какой параметр сейчас загружается
    @Published var autoReconnectEnabled: Bool {
        didSet {
            UserDefaults.standard.set(autoReconnectEnabled, forKey: "autoReconnectEnabled")
        }
    }
    
    // Публичный доступ к lastConnectedDeviceUUID
    var lastConnectedDeviceUUID: UUID? {
        get {
            if let uuidString = UserDefaults.standard.string(forKey: "lastConnectedDeviceUUID") {
                return UUID(uuidString: uuidString)
            }
            return nil
        }
        set {
            if let uuid = newValue {
                UserDefaults.standard.set(uuid.uuidString, forKey: "lastConnectedDeviceUUID")
            } else {
                UserDefaults.standard.removeObject(forKey: "lastConnectedDeviceUUID")
            }
        }
    }
    
    private(set) var connectedPeripheral: CBPeripheral?
    private(set) var writeCharacteristic: CBCharacteristic?
    
    var isConnected: Bool {
        return connectedPeripheral != nil && writeCharacteristic != nil
    }
    
    private var centralManager: CBCentralManager?
    private var cancellables = Set<AnyCancellable>()
    
    // Словарь для хранения соответствия UUID -> CBPeripheral
    private var peripheralCache: [UUID: CBPeripheral] = [:]
    private var discoveredCountAtScanStart: Int = 0
    
    // Автоподключение
    private var shouldAutoConnect = false
    
    // Периодическое сканирование
    private var periodicScanTimer: Timer?
    private let periodicScanInterval: TimeInterval = 10.0 // Каждые 10 секунд
    
    // Flow control для X2 принтера
    private var availableCredits: Int = 0
    private let creditLock = NSLock()
    private var mtuSize: Int = 220 // Безопасный размер пакета по умолчанию
    
    // Очередь команд для принтера
    private let commandQueue = DispatchQueue(label: "com.marklife.printer.commands", qos: .userInitiated)
    private var isProcessingCommand = false
    private let commandLock = NSLock()
    
    // UUIDs для принтеров
    // Команды отправляются на FF02, ответы приходят на 49535343-1E4D...
    private let serviceUUID = CBUUID(string: "FF00")
    private let writeCharacteristicUUID = CBUUID(string: "FF02")
    private let notifyCharacteristicUUID = CBUUID(string: "FF01")
    
    override init() {
        autoReconnectEnabled = UserDefaults.standard.bool(forKey: "autoReconnectEnabled")
        super.init()
        
        // Сразу начинаем сканирование
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            self?.startScanning()
        }
        
        // Запускаем периодическое сканирование
        startPeriodicScanning()
    }
    
    /// Запуск периодического сканирования в фоне
    func startPeriodicScanning() {
        stopPeriodicScanning()
        
        periodicScanTimer = Timer.scheduledTimer(withTimeInterval: periodicScanInterval, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            
            // Сканируем только если принтер не подключен и Bluetooth включен
            if self.connectedDevice == nil && self.bluetoothState == .poweredOn {
                NSLog("🔄 Периодическое сканирование...")
                self.startScanning()
            }
        }
        NSLog("⏰ Периодическое сканирование запущено (каждые %.0f сек)", periodicScanInterval)
    }
    
    /// Остановка периодического сканирования
    func stopPeriodicScanning() {
        periodicScanTimer?.invalidate()
        periodicScanTimer = nil
    }
    
    /// Запуск автоподключения к сохраненному устройству
    func startAutoConnect() {
        // Если есть сохраненное устройство - запускаем автоподключение
        if lastConnectedDeviceUUID != nil {
            NSLog("🔄 Обнаружено сохраненное устройство, запуск автоподключения...")
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
                self?.startScanning()
            }
        }
    }
    
    private func initializeCentralManagerIfNeeded() {
        if centralManager == nil {
            NSLog("🔵 Инициализация CBCentralManager (запрос разрешения)")
            centralManager = CBCentralManager(delegate: self, queue: nil)
        }
    }
    
    func startScanning() {
        // Инициализируем CBCentralManager при первом вызове (запрос разрешения)
        initializeCentralManagerIfNeeded()
        
        guard let centralManager = centralManager else { return }
        
        // Ждем пока CBCentralManager обновит состояние
        if bluetoothState == .unknown {
            NSLog("⏳ Ожидание инициализации Bluetooth...")
            // Попробуем через 0.5 секунды
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                self?.startScanning()
            }
            return
        }
        
        guard bluetoothState == .poweredOn else {
            NSLog("❌ Bluetooth не включен, состояние: %ld", bluetoothState.rawValue)
            return
        }
        
        NSLog("🔵 Начало сканирования Bluetooth устройств...")
        
        // НЕ очищаем список устройств, только обновляем статусы
        for i in 0..<discoveredDevices.count {
            if discoveredDevices[i].status != .connected {
                discoveredDevices[i].status = .disconnected
            }
        }
        
        isScanning = true
        
        discoveredCountAtScanStart = discoveredDevices.count
        
        // Автоподключение (сканирование всегда запускается)
        if let lastUUID = lastConnectedDeviceUUID {
            NSLog("🔄 Поиск сохраненного устройства: %@", lastUUID.uuidString)
            // Флаг shouldAutoConnect определяет, подключаться ли автоматически
            shouldAutoConnect = autoReconnectEnabled
        }
        
        NSLog("🔎 Фильтр сканирования по сервису: %@", serviceUUID.uuidString)
        centralManager.scanForPeripherals(withServices: [serviceUUID], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        
        NSLog("✅ Сканирование запущено")
        
        // Fallback: если за 5 секунд ничего не нашли с фильтром — пробуем без фильтра
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self = self, self.isScanning else { return }
            let noNewDevices = self.discoveredDevices.count == self.discoveredCountAtScanStart
            if noNewDevices {
                NSLog("⚠️ За 5 сек устройств с сервисом %@ не найдено — пробуем сканировать без фильтра", self.serviceUUID.uuidString)
                self.centralManager?.stopScan()
                self.centralManager?.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
            }
        }
        
        // Остановить сканирование через 15 секунд
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in
            self?.stopScanning()
        }
    }
    
    func stopScanning() {
        guard let centralManager = centralManager else { return }
        isScanning = false
        centralManager.stopScan()
        print("Stopped scanning. Found \(discoveredDevices.count) devices")
    }
    
    func connect(to device: PrinterDevice) {
        guard let centralManager = centralManager else { return }
        print("Attempting to connect to device: \(device.name)")
        
        // Найти peripheral в кэше
        guard let peripheral = peripheralCache[device.id] else {
            print("Peripheral not found in cache for device: \(device.name)")
            return
        }
        
        // Обновить статус на "подключается"
        if let index = discoveredDevices.firstIndex(where: { $0.id == device.id }) {
            discoveredDevices[index].status = .connecting
        }
        
        // Остановить сканирование если оно активно
        if isScanning {
            stopScanning()
        }
        
        // Сохраняем UUID для автоподключения
        lastConnectedDeviceUUID = device.id
        NSLog("💾 Сохранен UUID для автоподключения: %@", device.id.uuidString)
        
        // Подключиться к устройству
        centralManager.connect(peripheral, options: nil)
    }
    
    func disconnect() {
        guard let peripheral = connectedPeripheral, let centralManager = centralManager else { return }
        print("Disconnecting from \(peripheral.name ?? "device")")
        
        // НЕ очищаем lastConnectedDeviceUUID - оставляем для автоподключения
        
        centralManager.cancelPeripheralConnection(peripheral)
        
        // Уведомление будет отправлено в didDisconnectPeripheral
    }
    
    func sendData(_ data: Data) {
        // Добавляем команду в очередь
        commandQueue.async { [weak self] in
            guard let self = self else { return }
            
            // Ждем пока предыдущая команда завершится
            self.commandLock.lock()
            while self.isProcessingCommand {
                self.commandLock.unlock()
                Thread.sleep(forTimeInterval: 0.1)
                self.commandLock.lock()
            }
            self.isProcessingCommand = true
            self.commandLock.unlock()
            
            // Выполняем отправку
            self.sendDataInternal(data)
            
            // Освобождаем очередь
            self.commandLock.lock()
            self.isProcessingCommand = false
            self.commandLock.unlock()
        }
    }
    
    private func sendDataInternal(_ data: Data) {
        NSLog("📡 sendDataInternal() вызван")
        NSLog("   Размер данных: %ld байт", data.count)
        
        guard let peripheral = connectedPeripheral else {
            NSLog("❌ connectedPeripheral == nil")
            return
        }
        NSLog("   Peripheral: %@", peripheral.name ?? "unknown")
        NSLog("   Peripheral state: %ld", peripheral.state.rawValue)
        
        guard let characteristic = writeCharacteristic else {
            NSLog("❌ writeCharacteristic == nil")
            return
        }
        NSLog("   Characteristic UUID: %@", characteristic.uuid.uuidString)
        NSLog("   Characteristic properties: %lu", characteristic.properties.rawValue)
        
        // Выводим первые 50 байт в hex
        let previewBytes = min(50, data.count)
        let hexString = data.prefix(previewBytes).map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("   Первые %d байт (hex): %@", previewBytes, hexString)
        
        // Выводим как ASCII (если возможно)
        if let asciiString = String(data: data.prefix(100), encoding: .ascii) {
            NSLog("   Первые 100 байт (ASCII): %@", asciiString.replacingOccurrences(of: "\r", with: "\\r").replacingOccurrences(of: "\n", with: "\\n"))
        }
        
        // Инициализируем flow control если еще не было
        creditLock.lock()
        if availableCredits == 0 {
            availableCredits = 4 // Начальные кредиты
        }
        if mtuSize == 0 {
            mtuSize = 220 // Безопасный размер пакета
        }
        let currentMTU = mtuSize
        let initialCredits = availableCredits
        creditLock.unlock()
        
        NSLog("📦 Flow control: MTU=%d, кредитов=%d", currentMTU, initialCredits)
        
        // BLE требует разбивки на пакеты по MTU размеру
        // Используем безопасный размер 20 байт для совместимости
        let chunkSize = min(20, currentMTU - 3) // 3 байта на заголовок BLE
        let totalChunks = (data.count + chunkSize - 1) / chunkSize
        
        NSLog("📦 Разбиваем на %d пакетов по %d байт", totalChunks, chunkSize)
        
        var offset = 0
        var chunkNumber = 0
        var creditsUsed = 0
        
        while offset < data.count {
            // Ждем пока появятся кредиты (max 10 секунд)
            var hasCredit = false
            for _ in 0..<100 {
                creditLock.lock()
                if availableCredits > 0 {
                    availableCredits -= 1
                    hasCredit = true
                    let credits = availableCredits
                    creditLock.unlock()
                    creditsUsed += 1
                    if creditsUsed % 50 == 0 {
                        NSLog("   💳 Кредитов использовано: %d, осталось: %d", creditsUsed, credits)
                    }
                    break
                }
                creditLock.unlock()
                Thread.sleep(forTimeInterval: 0.1)
            }
            
            if !hasCredit {
                NSLog("❌ Таймаут ожидания кредитов, отправлено %d/%d пакетов", chunkNumber, totalChunks)
                return
            }
            
            let end = min(offset + chunkSize, data.count)
            let chunk = data.subdata(in: offset..<end)
            
            chunkNumber += 1
            
            // Отправляем пакет с ожиданием подтверждения для flow control
            peripheral.writeValue(chunk, for: characteristic, type: .withoutResponse)
            
            if chunkNumber % 100 == 0 || chunkNumber == totalChunks {
                NSLog("   📤 Отправлено пакетов: %d/%d", chunkNumber, totalChunks)
            }
            
            offset = end
            
            // Небольшая задержка между пакетами для надежности
            Thread.sleep(forTimeInterval: 0.01)
        }
        
        NSLog("✅ Все %d пакетов отправлены, использовано кредитов: %d", totalChunks, creditsUsed)
        
        // Ждем немного и проверяем буфер на наличие ответов
        Thread.sleep(forTimeInterval: 0.5)
        if !receivedData.isEmpty {
            let hexResponse = receivedData.map { String(format: "%02X", $0) }.joined(separator: " ")
            NSLog("📥 Получен ответ от принтера (FF01): [%@]", hexResponse)
        } else {
            NSLog("⚠️ Ответа от принтера нет (FF01)")
        }
    }
    
    // MARK: - Запросы информации о принтере
    
    private var readCallback: ((Data?) -> Void)?
    private var receivedData = Data()
    private var awaitingWriteAck = false
    private var pendingCommandTimeout: TimeInterval?
    
    /// Очистка буфера чтения (как flush() в Android)
    func flushReadBuffer() {
        receivedData.removeAll()
        NSLog("🧹 Буфер чтения очищен")
    }

    
    /// Печать тестовой страницы (самотест)
    func printSelfTest(completion: @escaping (Bool) -> Void) {
        NSLog("📡 Печать тестовой страницы")
        guard let peripheral = connectedPeripheral,
              let characteristic = writeCharacteristic else {
            NSLog("❌ Принтер не подключен")
            completion(false)
            return
        }
        
        // Команда печати самотеста
        let command = Data([0x12, 0x54])
        
        let hexString = command.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("📤 Отправка команды самотеста: [%@]", hexString)
        
        peripheral.writeValue(command, for: characteristic, type: .withoutResponse)
        
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            NSLog("✅ Команда самотеста отправлена")
            completion(true)
        }
    }
    
    /// Тест минимальной печати (простейший bitmap)
    func printMinimalTest(completion: @escaping (Bool) -> Void) {
        NSLog("📡 Тест минимальной печати")
        guard let peripheral = connectedPeripheral,
              let characteristic = writeCharacteristic else {
            NSLog("❌ Принтер не подключен")
            completion(false)
            return
        }
        
        // ТЕСТ: попробуем ESC/POS команды (стандартный протокол термопринтеров)
        var commands = Data()
        
        // ESC @ - инициализация
        commands.append(contentsOf: [0x1B, 0x40])
        
        // Текст
        if let textData = "TEST PRINT\n\n\n".data(using: .ascii) {
            commands.append(textData)
        }
        
        NSLog("   ESC/POS команды: %d байт", commands.count)
        let hexPreview = commands.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("   Команды: %@", hexPreview)
        
        // Отправляем
        sendData(commands)
        
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) {
            NSLog("✅ ESC/POS тест отправлен")
            completion(true)
        }
    }
    
    /// Печать тестового текста
    func printTestText(completion: @escaping (Bool) -> Void) {
        NSLog("📡 Печать тестового текста")
        guard let peripheral = connectedPeripheral,
              let characteristic = writeCharacteristic else {
            NSLog("❌ Принтер не подключен")
            completion(false)
            return
        }
        
        // ESC @ - инициализация принтера
        let initCommand = Data([0x1B, 0x40])
        
        // Текст для печати
        let text = "Hello from Mac!\n\n\n\n"
        guard let textData = text.data(using: .utf8) else {
            completion(false)
            return
        }
        
        // Объединяем команды
        var fullCommand = Data()
        fullCommand.append(initCommand)
        fullCommand.append(textData)
        
        let hexString = fullCommand.prefix(20).map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("📤 Отправка команды печати текста: [%@]...", hexString)
        NSLog("   Всего байт: %d", fullCommand.count)
        
        // Отправляем с учетом flow control
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self = self else { return }
            
            var offset = 0
            var packetNumber = 0
            
            while offset < fullCommand.count {
                // Ждем пока появятся кредиты
                var hasCredit = false
                for _ in 0..<50 { // Максимум 5 секунд ожидания
                    self.creditLock.lock()
                    if self.availableCredits > 0 {
                        self.availableCredits -= 1
                        hasCredit = true
                        let credits = self.availableCredits
                        self.creditLock.unlock()
                        NSLog("✅ Получен кредит, осталось: %d", credits)
                        break
                    }
                    self.creditLock.unlock()
                    Thread.sleep(forTimeInterval: 0.1)
                }
                
                if !hasCredit {
                    NSLog("❌ Таймаут ожидания кредитов")
                    DispatchQueue.main.async {
                        completion(false)
                    }
                    return
                }
                
                // Определяем размер пакета
                let chunkSize = min(self.mtuSize, fullCommand.count - offset)
                let end = offset + chunkSize
                let chunk = fullCommand.subdata(in: offset..<end)
                
                packetNumber += 1
                NSLog("📦 Пакет #%d: %d байт", packetNumber, chunk.count)
                
                // Отправляем пакет
                peripheral.writeValue(chunk, for: characteristic, type: .withoutResponse)
                
                offset = end
                
                // Небольшая задержка
                Thread.sleep(forTimeInterval: 0.02)
            }
            
            NSLog("✅ Все %d пакетов отправлены", packetNumber)
            DispatchQueue.main.async {
                completion(true)
            }
        }
    }
    
    /// Запрос статуса принтера
    func requestPrinterStatus(completion: @escaping (String) -> Void) {
        NSLog("📡 Запрос статуса принтера")
        flushReadBuffer()
        // BLE: Команда запроса статуса
        let command = Data([0x10, 0x04, 0x05])
        
        sendCommandAndRead(command, timeout: 3.0) { data in
            guard let data = data, !data.isEmpty else {
                completion("Print Read Error")
                return
            }
            
            let status = data[0]
            
            if status == 0 || (status == 79 && data.count > 1 && data[1] == 75) {
                completion("OK")
            } else if (status & 0x10) != 0 {
                completion("CoverOpened")
            } else if (status & 0x01) != 0 {
                completion("NoPaper")
            } else if (status & 0x08) != 0 {
                completion("Printing")
            } else if (status & 0x04) != 0 {
                completion("BatteryLow")
            } else {
                completion("OK")
            }
        }
    }
    
    // Список параметров для запроса (порядок как в Android)
    private enum PrinterAttribute: String, CaseIterable {
        case battery = "BATTERY_VOL"
        case firmware = "FIRMWARE_VERSION"
        case serialNumber = "SN_CODE"
        case paperLevel = "MILEAGE_BALANCE"
        case shutdownTime = "SHUTDOWN_TIME"
        case macAddress = "MAC_ADDRESS"
    }
    
    private var currentAttributeIndex = 0
    private var attributeRequestCompletion: ((Bool) -> Void)?
    private var isRequestingParameters = false
    
    /// Запрос всех параметров принтера
    func requestAllPrinterInfo(completion: @escaping (Bool) -> Void) {
        NSLog("🔄 Загрузка информации о принтере...")
        
        guard connectedPeripheral != nil else {
            NSLog("❌ Принтер не подключен")
            completion(false)
            return
        }
        
        currentAttributeIndex = 0
        attributeRequestCompletion = completion
        isRequestingParameters = true
        requestNextAttribute()
    }
    
    /// Отмена запроса параметров
    func cancelParameterRequest() {
        if isRequestingParameters {
            NSLog("🛑 Отмена запроса параметров")
            isRequestingParameters = false
            currentAttributeIndex = 0
            attributeRequestCompletion?(false)
            attributeRequestCompletion = nil
            readCallback = nil
            
            DispatchQueue.main.async { [weak self] in
                self?.currentLoadingParameter = nil
            }
        }
    }
    
    /// Запрос следующего параметра из списка
    private func requestNextAttribute() {
        // Проверяем, не была ли отменена операция
        guard isRequestingParameters else {
            NSLog("⚠️ Запрос параметров отменен")
            return
        }
        
        let attributes = PrinterAttribute.allCases
        
        guard currentAttributeIndex < attributes.count else {
            NSLog("✅ Информация о принтере загружена")
            isRequestingParameters = false
            // Увеличенная задержка перед сбросом индикатора, чтобы последний параметр успел отобразиться
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                self?.currentLoadingParameter = nil
            }
            attributeRequestCompletion?(true)
            attributeRequestCompletion = nil
            return
        }
        
        let attribute = attributes[currentAttributeIndex]
        NSLog("📡 Запрос параметра: %@", attribute.rawValue)
        
        // Устанавливаем индикатор загрузки
        DispatchQueue.main.async { [weak self] in
            switch attribute {
            case .battery:
                self?.currentLoadingParameter = "battery"
            case .firmware:
                self?.currentLoadingParameter = "firmware"
            case .serialNumber:
                self?.currentLoadingParameter = "serial"
            case .paperLevel:
                self?.currentLoadingParameter = "paper"
            case .shutdownTime:
                self?.currentLoadingParameter = "shutdown"
            case .macAddress:
                self?.currentLoadingParameter = "mac"
            }
        }
        
        switch attribute {
        case .battery:
            requestBatteryLevel { [weak self] value in
                self?.updateBatteryInUI(value)
                self?.moveToNextAttribute()
            }
        case .firmware:
            requestFirmwareVersion { [weak self] value in
                self?.updateFirmwareInUI(value)
                self?.moveToNextAttribute()
            }
        case .serialNumber:
            requestSerialNumber { [weak self] value in
                self?.updateSerialNumberInUI(value)
                self?.moveToNextAttribute()
            }
        case .paperLevel:
            requestPaperLevel { [weak self] value in
                self?.updatePaperLevelInUI(value)
                self?.moveToNextAttribute()
            }
        case .shutdownTime:
            requestAutoShutdownTime { [weak self] value in
                self?.updateShutdownTimeInUI(value)
                self?.moveToNextAttribute()
            }
        case .macAddress:
            requestMacAddress { [weak self] value in
                self?.updateMacAddressInUI(value)
                self?.moveToNextAttribute()
            }
        }
    }
    
    /// Переход к следующему параметру с задержкой
    private func moveToNextAttribute() {
        // Проверяем, не была ли отменена операция
        guard isRequestingParameters else {
            NSLog("⚠️ Запрос параметров отменен, не переходим к следующему")
            return
        }
        
        currentAttributeIndex += 1
        // Задержка 1 сек между командами
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
            self?.requestNextAttribute()
        }
    }
    
    /// Проверка буфера перед отправкой команды - обрабатываем запоздалые ответы
    private func checkBufferForDelayedResponses() {
        guard !receivedData.isEmpty else { return }
        
        let hex = receivedData.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("🔍 Проверка буфера перед командой: %@", hex)
        
        // Проверяем ответ на запрос бумаги (1A 1F 06)
        if receivedData.count >= 8 && receivedData[0] == 0x1A && receivedData[1] == 0x1F && receivedData[2] == 0x06 {
            let paperLevel = Int(receivedData[3]) * 2
            NSLog("✅ Обработан запоздалый ответ на запрос бумаги: %d%%", paperLevel)
            updatePaperLevelInUI(paperLevel)
            receivedData.removeAll()
            return
        }
        
        // Проверяем ответ на запрос автоотключения (10 FF 13 -> 1 байт)
        if receivedData.count >= 1 && receivedData.count <= 2 {
            let minutes = Int(receivedData[0])
            if minutes >= 15 && minutes <= 60 {
                NSLog("✅ Обработан запоздалый ответ на запрос автоотключения: %d мин", minutes)
                updateShutdownTimeInUI(minutes)
                receivedData.removeAll()
                return
            }
        }
        
        // Проверяем ответ на запрос батареи (10 FF 50 F1 -> 2 байта: 00 XX)
        if receivedData.count == 2 && receivedData[0] == 0x00 {
            let battery = Int(receivedData[1])
            NSLog("✅ Обработан запоздалый ответ на запрос батареи: %d%%", battery)
            updateBatteryInUI(battery)
            receivedData.removeAll()
            return
        }
        
        // Проверяем ответ на запрос прошивки (ASCII строка начинается с V)
        if let str = String(data: receivedData, encoding: .ascii), str.hasPrefix("V") {
            NSLog("✅ Обработан запоздалый ответ на запрос прошивки: %@", str)
            updateFirmwareInUI(str)
            receivedData.removeAll()
            return
        }
        
        // Проверяем ответ на запрос серийника (ASCII строка начинается с X2)
        if let str = String(data: receivedData, encoding: .ascii), str.hasPrefix("X2") {
            let cleanData = receivedData.prefix(while: { $0 != 0 })
            let serial = String(data: cleanData, encoding: .ascii) ?? "Unknown"
            NSLog("✅ Обработан запоздалый ответ на запрос серийника: %@", serial)
            updateSerialNumberInUI(serial)
            receivedData.removeAll()
            return
        }
        
        // Проверяем ответ на запрос MAC (6 байт)
        if receivedData.count == 6 {
            let mac = receivedData.map { String(format: "%02X", $0) }.joined(separator: ":")
            NSLog("✅ Обработан запоздалый ответ на запрос MAC: %@", mac)
            updateMacAddressInUI(mac)
            receivedData.removeAll()
            return
        }
        
        // Если не распознали - оставляем в буфере, может еще данные придут
        NSLog("⚠️ Неизвестные данные в буфере, оставляем")
    }
    
    // MARK: - UI Update Methods
    
    private func updateBatteryInUI(_ value: Int?) {
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.batteryLevel = value
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].batteryLevel = value
            }
            NSLog("✅ Батарея обновлена в UI: %d%%", value ?? -1)
        }
    }
    
    private func updateFirmwareInUI(_ value: String?) {
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.firmwareVersion = value
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].firmwareVersion = value
            }
            NSLog("✅ Прошивка обновлена в UI: %@", value ?? "nil")
        }
    }
    
    private func updateSerialNumberInUI(_ value: String?) {
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.serialNumber = value
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].serialNumber = value
            }
            NSLog("✅ Серийник обновлен в UI: %@", value ?? "nil")
        }
    }
    
    private func updatePaperLevelInUI(_ value: Int?) {
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.paperLevel = value
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].paperLevel = value
            }
            NSLog("✅ Остаток бумаги обновлен в UI: %d%%", value ?? -1)
        }
    }
    
    private func updateShutdownTimeInUI(_ value: Int?) {
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.autoShutdownMinutes = value
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].autoShutdownMinutes = value
            }
            NSLog("✅ Время автоотключения обновлено в UI: %d мин", value ?? -1)
        }
    }
    
    private func updateMacAddressInUI(_ value: String?) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            
            // PrinterDevice - это struct (value type), поэтому нужно заменять элемент целиком
            if let device = self.connectedDevice,
               let index = self.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                var updatedDevice = self.discoveredDevices[index]
                updatedDevice.macAddress = value
                
                // Принудительно обновляем - сначала nil, потом новое значение
                self.connectedDevice = nil
                self.discoveredDevices[index] = updatedDevice
                self.connectedDevice = updatedDevice
            }
            NSLog("✅ MAC адрес обновлен в UI: %@", value ?? "nil")
        }
    }
    
    /// Запрос уровня батареи
    private func requestBatteryLevel(completion: @escaping (Int?) -> Void) {
        NSLog("📡 Запрос уровня батареи")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x10, 0xFF, 0x50, 0xF1])
        
        sendCommandAndRead(command, timeout: 2.0) { data in
            guard let data = data, data.count >= 2 else {
                NSLog("❌ Ошибка чтения батареи")
                completion(nil)
                return
            }
            
            let battery = Int(data[1])
            NSLog("🔋 Батарея: %d%%", battery)
            completion(battery)
        }
    }
    
    /// Запрос версии прошивки
    private func requestFirmwareVersion(completion: @escaping (String?) -> Void) {
        NSLog("📡 Запрос версии прошивки")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x10, 0xFF, 0x20, 0xF1])
        
        sendCommandAndRead(command, timeout: 2.0) { data in
            guard let data = data, !data.isEmpty else {
                NSLog("❌ Ошибка чтения прошивки")
                completion(nil)
                return
            }
            
            let version = String(data: data, encoding: .ascii) ?? "Unknown"
            NSLog("📌 Прошивка: %@", version)
            completion(version)
        }
    }
    
    /// Запрос серийного номера
    private func requestSerialNumber(completion: @escaping (String?) -> Void) {
        NSLog("📡 Запрос серийного номера")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x10, 0xFF, 0x20, 0xF2])
        
        sendCommandAndRead(command, timeout: 2.0) { data in
            guard let data = data, !data.isEmpty else {
                NSLog("❌ Ошибка чтения серийника")
                completion(nil)
                return
            }
            
            // Убираем нулевой байт в конце
            let cleanData = data.prefix(while: { $0 != 0 })
            let serial = String(data: cleanData, encoding: .ascii) ?? "Unknown"
            NSLog("🔢 Серийник: %@", serial)
            completion(serial)
        }
    }
    
    /// Запрос остатка бумаги
    private func requestPaperLevel(completion: @escaping (Int?) -> Void) {
        NSLog("📡 Запрос остатка бумаги")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x1A, 0x1F, 0x06])
        
        // Увеличенный таймаут - ответ приходит медленно
        sendCommandAndRead(command, timeout: 20.0) { data in
            guard let data = data, data.count >= 4 else {
                NSLog("❌ Ошибка чтения остатка бумаги, получено %d байт", data?.count ?? 0)
                if let data = data, !data.isEmpty {
                    let hex = data.map { String(format: "%02X", $0) }.joined(separator: " ")
                    NSLog("   Данные: %@", hex)
                }
                completion(nil)
                return
            }
            
            // Проверяем что это правильный ответ (начинается с 1A 1F 06)
            if data.count >= 8 && data[0] == 0x1A && data[1] == 0x1F && data[2] == 0x06 {
                // Байт 3 * 2 = процент
                let paperLevel = Int(data[3]) * 2
                NSLog("📄 Остаток бумаги: %d%%", paperLevel)
                completion(paperLevel)
            } else {
                NSLog("❌ Неверный формат ответа на запрос бумаги")
                let hex = data.map { String(format: "%02X", $0) }.joined(separator: " ")
                NSLog("   Данные: %@", hex)
                completion(nil)
            }
        }
    }
    
    /// Запрос MAC адреса
    private func requestMacAddress(completion: @escaping (String?) -> Void) {
        NSLog("📡 Запрос MAC адреса")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x10, 0xFF, 0x20, 0xF3])

        // Увеличенный таймаут для MAC адреса (данные могут приходить позже)
        sendCommandAndRead(command, timeout: 10.0) { data in
            guard let data = data, data.count >= 6 else {
                NSLog("❌ Ошибка чтения MAC адреса")
                completion(nil)
                return
            }
            
            let mac = data.prefix(6).map { String(format: "%02X", $0) }.joined(separator: ":")
            NSLog("🌐 MAC адрес: %@", mac)
            completion(mac)
        }
    }
    
    /// Запрос времени автоотключения
    private func requestAutoShutdownTime(completion: @escaping (Int?) -> Void) {
        NSLog("📡 Запрос времени автоотключения")
        checkBufferForDelayedResponses()
        receivedData.removeAll()
        let command = Data([0x10, 0xFF, 0x13])
        
        // Увеличенный таймаут - ответ может приходить медленно
        sendCommandAndRead(command, timeout: 10.0) { data in
            guard let data = data, !data.isEmpty else {
                NSLog("❌ Ошибка чтения времени автоотключения")
                completion(nil)
                return
            }
            
            let minutes = Int(data[0])
            NSLog("⏱️ Время автоотключения: %d минут", minutes)
            completion(minutes)
        }
    }
    
    /// Установка времени автоотключения
    func setAutoShutdownTime(minutes: Int, completion: @escaping (Bool) -> Void) {
        NSLog("📡 Установка времени автоотключения: %d минут", minutes)
        
        guard let peripheral = connectedPeripheral,
              let characteristic = writeCharacteristic else {
            NSLog("❌ Принтер не подключен")
            completion(false)
            return
        }
        
        // Команда: 10 FF 12 <high_byte> <low_byte>
        // Время в минутах разбивается на 2 байта (big-endian)
        let highByte = UInt8(minutes / 256)
        let lowByte = UInt8(minutes % 256)
        let command = Data([0x10, 0xFF, 0x12, highByte, lowByte])
        
        let hexString = command.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("📤 Отправка команды установки автоотключения: [%@]", hexString)
        NSLog("   Decimal: [%@]", command.map { String($0) }.joined(separator: ", "))
        
        // Отправляем команду без ожидания ответа (команда установки не возвращает ответ)
        peripheral.writeValue(command, for: characteristic, type: .withoutResponse)
        
        // Обновляем в модели сразу
        DispatchQueue.main.async { [weak self] in
            self?.connectedDevice?.autoShutdownMinutes = minutes
            if let device = self?.connectedDevice,
               let index = self?.discoveredDevices.firstIndex(where: { $0.id == device.id }) {
                self?.discoveredDevices[index].autoShutdownMinutes = minutes
            }
            NSLog("✅ Команда установки автоотключения отправлена")
            completion(true)
        }
    }

    
    /// Отправка команды и чтение ответа
    private func sendCommandAndRead(_ command: Data, timeout: TimeInterval, completion: @escaping (Data?) -> Void) {
        guard let peripheral = connectedPeripheral,
              let characteristic = writeCharacteristic else {
            NSLog("❌ Принтер не подключен")
            completion(nil)
            return
        }
        
        // Логируем что отправляем
        let hexString = command.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("📤 Отправка команды: [%@]", hexString)
        let decimalBytes = command.map { String($0) }
        NSLog("   Decimal: [%@]", decimalBytes.joined(separator: ", "))
        
        // Очищаем буфер перед отправкой
        receivedData.removeAll()
        awaitingWriteAck = false  // Не ждем ACK для writeWithoutResponse
        pendingCommandTimeout = timeout
        
        readCallback = completion
        
        // iOS приложение использует writeWithoutResponse
        peripheral.writeValue(command, for: characteristic, type: .withoutResponse)
        
        NSLog("⏳ Ожидание ответа (таймаут: %.1f сек)", timeout)
        
        // Запускаем таймаут сразу, т.к. не будет didWriteValueFor callback
        DispatchQueue.main.asyncAfter(deadline: .now() + timeout) { [weak self] in
            guard let self = self else { return }
            if self.readCallback != nil {
                NSLog("⏱️ Таймаут ожидания ответа")
                NSLog("   Получено байт в буфере: %ld", self.receivedData.count)
                if !self.receivedData.isEmpty {
                    let hex = self.receivedData.map { String(format: "%02X", $0) }.joined(separator: " ")
                    NSLog("   Данные в буфере: %@", hex)
                }
                self.readCallback?(self.receivedData.isEmpty ? nil : self.receivedData)
                self.readCallback = nil
            }
        }
    }
    
    // Проверка имени принтера (из Android кода)
    private func isValidPrinterName(_ name: String) -> Bool {
        let validPrefixes = [
            "P11", "P12", "P15", "P7", "P50", "P80", "P1S", "P5OS",
            "L50", "L80", "LP90", "LP15",
            "D1", "D50", "D100", "D200", "D210",
            "X2", "X4", "S2", "S8", "T3", "ET",
            "M1", "IP_D80", "DP_D80", "DP_8028",
            "Silvertec_WE_P12", "LuckP_D1", "Jammuk_S2", "OUT_LPC", "PS50",
            "210"
        ]
        
        for prefix in validPrefixes {
            if name.hasPrefix(prefix) {
                return true
            }
        }
        
        return false
    }
}

// MARK: - CBCentralManagerDelegate
extension BluetoothManager: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        bluetoothState = central.state
        
        switch central.state {
        case .poweredOn:
            print("Bluetooth is powered on")
        case .poweredOff:
            print("Bluetooth is powered off")
        case .unauthorized:
            print("Bluetooth is unauthorized")
        case .unsupported:
            print("Bluetooth is not supported")
        default:
            break
        }
    }
    
    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String : Any], rssi RSSI: NSNumber) {
        let name = peripheral.name ?? "Unknown Device"
        
        NSLog("🔍 Найдено устройство: %@", name)
        
        // Фильтруем только принтеры Marklife по именам из Android приложения
        guard isValidPrinterName(name) else {
            NSLog("❌ Устройство %@ не является принтером Marklife", name)
            return
        }
        
        NSLog("✅ Это принтер Marklife: %@", name)
        
        // Используем identifier от peripheral как UUID
        let deviceId = peripheral.identifier
        
        let device = PrinterDevice(
            id: deviceId,
            name: name,
            address: peripheral.identifier.uuidString,
            connectionType: .bluetooth,
            status: .disconnected
        )
        
        // Сохранить peripheral в кэш
        peripheralCache[deviceId] = peripheral
        
        // Обновляем на главном потоке
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            
            if !self.discoveredDevices.contains(where: { $0.id == device.id }) {
                self.discoveredDevices.append(device)
                NSLog("✅ Добавлен принтер: %@", name)
                NSLog("   Всего устройств: %d", self.discoveredDevices.count)
            } else {
                NSLog("⚠️ Принтер %@ уже в списке", name)
            }
            
            // Автоподключение к последнему устройству
            if self.shouldAutoConnect, let lastUUID = self.lastConnectedDeviceUUID, deviceId == lastUUID {
                NSLog("🔄 Автоподключение к устройству: %@", name)
                self.shouldAutoConnect = false // Подключаемся только один раз
                
                // Подключаемся через небольшую задержку
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    self.connect(to: device)
                }
            }
        }
    }
    
    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        print("✅ Successfully connected to \(peripheral.name ?? "device")")
        connectedPeripheral = peripheral
        peripheral.delegate = self
        
        // Обновить статус устройства
        if let index = discoveredDevices.firstIndex(where: { $0.address == peripheral.identifier.uuidString }) {
            discoveredDevices[index].status = .connected
            connectedDevice = discoveredDevices[index]
            print("Updated device status to connected: \(discoveredDevices[index].name)")
            
            // Уведомляем об изменении состояния
            NotificationCenter.default.post(name: NSNotification.Name("ConnectionStateChanged"), object: nil)
        }
        
        // Начать поиск ВСЕХ сервисов
        print("Discovering all services...")
        peripheral.discoverServices(nil)
    }
    
    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        if let error = error {
            print("❌ Disconnected from \(peripheral.name ?? "device") with error: \(error.localizedDescription)")
        } else {
            print("Disconnected from \(peripheral.name ?? "device")")
        }
        
        connectedPeripheral = nil
        writeCharacteristic = nil
        connectedDevice = nil
        
        if let index = discoveredDevices.firstIndex(where: { $0.address == peripheral.identifier.uuidString }) {
            discoveredDevices[index].status = .disconnected
        }
        
        // Уведомляем об изменении состояния подключения
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: NSNotification.Name("ConnectionStateChanged"), object: nil)
        }
    }
    
    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        print("❌ Failed to connect to \(peripheral.name ?? "device"): \(error?.localizedDescription ?? "unknown error")")
    }
}

// MARK: - CBPeripheralDelegate
extension BluetoothManager: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        if let error = error {
            print("Error discovering services: \(error.localizedDescription)")
            return
        }
        
        guard let services = peripheral.services else { return }
        
        NSLog("🔍 Найдено сервисов: %d", services.count)
        for service in services {
            NSLog("   Сервис UUID: %@", service.uuid.uuidString)
            // Ищем characteristics во ВСЕХ сервисах
            peripheral.discoverCharacteristics(nil, for: service)
        }
    }
    
    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        if let error = error {
            print("Error discovering characteristics: \(error.localizedDescription)")
            return
        }
        
        guard let characteristics = service.characteristics else { return }
        
        NSLog("📋 Найдено characteristics: %d для сервиса %@", characteristics.count, service.uuid.uuidString)
        for characteristic in characteristics {
            NSLog("   UUID: %@", characteristic.uuid.uuidString)
            NSLog("   Properties raw: %lu", characteristic.properties.rawValue)
            
            // Детальный разбор properties
            var props: [String] = []
            if characteristic.properties.contains(.read) { props.append("READ") }
            if characteristic.properties.contains(.write) { props.append("WRITE") }
            if characteristic.properties.contains(.writeWithoutResponse) { props.append("WRITE_NO_RESPONSE") }
            if characteristic.properties.contains(.notify) { props.append("NOTIFY") }
            if characteristic.properties.contains(.indicate) { props.append("INDICATE") }
            if characteristic.properties.contains(.broadcast) { props.append("BROADCAST") }
            NSLog("   Properties: [%@]", props.joined(separator: ", "))
            
            // Используем FF02 для записи (как в iOS)
            if characteristic.uuid == writeCharacteristicUUID {
                writeCharacteristic = characteristic
                NSLog("✅ Found write characteristic FF02")
            }
            
            // Подписываемся на ВСЕ notify/indicate characteristics
            if characteristic.properties.contains(.notify) || characteristic.properties.contains(.indicate) {
                // Для FF01 сначала запрашиваем descriptors, потом подписываемся
                if characteristic.uuid.uuidString.uppercased() == "FF01" || 
                   characteristic.uuid.uuidString.uppercased() == "0000FF01-0000-1000-8000-00805F9B34FB" {
                    NSLog("🔍 Запрашиваем descriptors для FF01 (подписка будет после)")
                    peripheral.discoverDescriptors(for: characteristic)
                } else {
                    peripheral.setNotifyValue(true, for: characteristic)
                    NSLog("✅ Подписываемся на notify/indicate: %@", characteristic.uuid.uuidString)
                }
            }
        }
        
        if writeCharacteristic == nil {
            NSLog("⚠️ Write characteristic не найден по UUID, пробуем использовать первый с write свойством")
            for characteristic in characteristics {
                if characteristic.properties.contains(.write) || characteristic.properties.contains(.writeWithoutResponse) {
                    writeCharacteristic = characteristic
                    NSLog("✅ Используем characteristic: %@", characteristic.uuid.uuidString)
                    break
                }
            }
        }
        
        // Проверяем финальное состояние
        NSLog("🔍 Финальная проверка для сервиса %@:", service.uuid.uuidString)
        NSLog("   writeCharacteristic: %@", writeCharacteristic != nil ? "✅ установлен" : "❌ nil")
        if let wc = writeCharacteristic {
            NSLog("   writeCharacteristic UUID: %@", wc.uuid.uuidString)
        }
        let notifyCount = characteristics.filter { $0.properties.contains(.notify) || $0.properties.contains(.indicate) }.count
        NSLog("   notify characteristics: %d", notifyCount)
        
        // ВАЖНО: Ждем пока все подписки на notify будут готовы
        // Только после этого можно отправлять команды
    }
    
    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        if let error = error {
            NSLog("❌ Ошибка подписки на notify для %@: %@", characteristic.uuid.uuidString, error.localizedDescription)
            return
        }
        
        NSLog("✅ Подписка на notify обновлена для %@", characteristic.uuid.uuidString)
        NSLog("   isNotifying: %@", characteristic.isNotifying ? "YES" : "NO")
    }
    
    func peripheral(_ peripheral: CBPeripheral, didDiscoverDescriptorsFor characteristic: CBCharacteristic, error: Error?) {
        if let error = error {
            NSLog("❌ Ошибка discovery descriptors для %@: %@", characteristic.uuid.uuidString, error.localizedDescription)
            return
        }
        
        NSLog("✅ Найдены descriptors для %@", characteristic.uuid.uuidString)
        if let descriptors = characteristic.descriptors {
            NSLog("   Количество descriptors: %d", descriptors.count)
            for descriptor in descriptors {
                NSLog("   Descriptor UUID: %@", descriptor.uuid.uuidString)
            }
        }
        
        // ПОСЛЕ обнаружения descriptors подписываемся на notifications
        if characteristic.uuid.uuidString.uppercased() == "FF01" || 
           characteristic.uuid.uuidString.uppercased() == "0000FF01-0000-1000-8000-00805F9B34FB" {
            NSLog("✅ Теперь подписываемся на FF01 (после discovery descriptors)")
            peripheral.setNotifyValue(true, for: characteristic)
        }
    }
    
    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor descriptor: CBDescriptor, error: Error?) {
        if let error = error {
            NSLog("❌ Ошибка чтения descriptor %@: %@", descriptor.uuid.uuidString, error.localizedDescription)
            return
        }
        
        NSLog("✅ Прочитан descriptor %@", descriptor.uuid.uuidString)
        if let value = descriptor.value {
            NSLog("   Значение: %@", String(describing: value))
            
            // Если CCCD (2902) = 0, значит notifications не включены на уровне дескриптора
            // НО мы уже вызвали setNotifyValue(true), так что это проблема принтера/macOS
            if descriptor.uuid.uuidString.uppercased() == "2902" {
                if let numValue = value as? NSNumber, numValue.intValue == 0 {
                    NSLog("   ⚠️ CCCD = 0, но setNotifyValue уже вызван")
                    NSLog("   � Это известная проблема macOS - дескриптор не обновляется автоматически")
                    NSLog("   🔧 Попробуем вызвать setNotifyValue повторно...")
                    
                    // Попытка повторного вызова setNotifyValue
                    if let char = descriptor.characteristic {
                        peripheral.setNotifyValue(false, for: char)
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                            peripheral.setNotifyValue(true, for: char)
                            NSLog("   🔄 Повторная подписка на %@", char.uuid.uuidString)
                        }
                    }
                }
            }
        }
    }
    
    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor descriptor: CBDescriptor, error: Error?) {
        if let error = error {
            NSLog("❌ Ошибка записи descriptor %@: %@", descriptor.uuid.uuidString, error.localizedDescription)
            NSLog("   ⚠️ Возможно CoreBluetooth не позволяет прямую запись в CCCD")
            NSLog("   💡 Нужен другой подход для включения notifications")
            return
        }
        
        NSLog("✅ Записан descriptor %@", descriptor.uuid.uuidString)
        
        // Перечитываем чтобы убедиться что значение изменилось
        if descriptor.uuid.uuidString.uppercased() == "2902" {
            NSLog("   🔍 Перечитываем CCCD после записи...")
            peripheral.readValue(for: descriptor)
        }
    }
    
    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        let uuidUpper = characteristic.uuid.uuidString.uppercased()
        NSLog("🔔 didUpdateValueFor вызван для UUID: %@ (upper: %@)", characteristic.uuid.uuidString, uuidUpper)
        
        if let error = error {
            NSLog("❌ Ошибка чтения characteristic %@: %@", characteristic.uuid.uuidString, error.localizedDescription)
            return
        }
        
        guard let data = characteristic.value else {
            NSLog("⚠️ Получен update для %@ но data == nil", characteristic.uuid.uuidString)
            return
        }
        
        NSLog("📥 Получены данные от принтера")
        NSLog("   Characteristic UUID: %@", characteristic.uuid.uuidString)
        NSLog("   Размер: %d байт", data.count)
        let hexString = data.map { String(format: "%02X", $0) }.joined(separator: " ")
        NSLog("   HEX: %@", hexString)
        
        // FF03 - это flow control (кредиты и MTU)
        let isFF03 = uuidUpper == "FF03" || uuidUpper == "0000FF03-0000-1000-8000-00805F9B34FB"
        NSLog("   🔍 Проверка FF03: %@ (result: %@)", uuidUpper, isFF03 ? "YES" : "NO")
        if isFF03 {
            handleFlowControl(data: data)
            return
        }
        
        // 49535343-1E4D-4BD9-BA61-23C647249616 - реальные данные от X2 (серийный номер, версия)
        let isChar14 = uuidUpper == "49535343-1E4D-4BD9-BA61-23C647249616"
        NSLog("   🔍 Проверка Char14: %@ (result: %@)", uuidUpper, isChar14 ? "YES" : "NO")
        
        // Также проверяем 49535343-ACA3-481C-91EC-D85E28A60318 (может ответы приходят туда)
        let isCharACA3 = uuidUpper == "49535343-ACA3-481C-91EC-D85E28A60318"
        NSLog("   🔍 Проверка CharACA3: %@ (result: %@)", uuidUpper, isCharACA3 ? "YES" : "NO")
        
        if isChar14 || isCharACA3 {
            NSLog("   📍 Данные на characteristic 14 (X2)")
            
            // Пробуем вывести как ASCII
            if let str = String(data: data, encoding: .ascii) {
                let printable = str.replacingOccurrences(of: "\r", with: "\\r")
                                  .replacingOccurrences(of: "\n", with: "\\n")
                                  .replacingOccurrences(of: "\0", with: "")
                if !printable.isEmpty {
                    NSLog("   ASCII: %@", printable)
                }
                
                // Проверяем формат
                if printable.hasPrefix("V") && printable.contains(".") {
                    NSLog("   🔎 Версия прошивки: %@", printable)
                } else if printable.hasPrefix("X2") {
                    NSLog("   🔎 Серийный номер: %@", printable)
                }
            }
            
            // Проверяем короткие данные (батарея, электричество)
            if data.count == 1 {
                let value = Int(data[0])
                NSLog("   🔋 Короткие данные (1 байт): %d (0x%02X)", value, data[0])
            } else if data.count == 2 {
                let value = Int(data[1])
                NSLog("   🔋 Короткие данные (2 байта): %d (0x%02X %02X)", value, data[0], data[1])
            }
            
            // MAC адрес
            if data.count == 6 {
                let mac = data.map { String(format: "%02X", $0) }.joined(separator: ":")
                NSLog("   📡 MAC адрес: %@", mac)
            }
            
            // Проверяем формат ответа с заголовком 1A 1F
            if data.count >= 3 && data[0] == 0x1A && data[1] == 0x1F {
                if data[2] == 0x05 {
                    // Ответ на запрос серийного номера: 1A 1F 05 + ASCII + checksum
                    if data.count > 3 {
                        let snData = data.subdata(in: 3..<(data.count-1))
                        if let sn = String(data: snData, encoding: .ascii) {
                            NSLog("   🔎 Серийный номер (1A1F05): %@", sn)
                        }
                    }
                } else if data[2] == 0x06 {
                    // Ответ на запрос версии: 1A 1F 06 + 4 байта + checksum
                    if data.count >= 8 {
                        let versionBytes = data.subdata(in: 3..<7)
                        let versionHex = versionBytes.map { String(format: "%02X", $0) }.joined(separator: " ")
                        NSLog("   🔎 Версия (1A1F06 hex): %@", versionHex)
                    }
                }
            }
            
            // Накапливаем данные в буфер
            receivedData.append(data)
            NSLog("   Всего в буфере: %ld байт", receivedData.count)
            
            // Проверяем, есть ли callback
            if let callback = readCallback {
                NSLog("   ✅ Callback активен, вызываем сразу")
                callback(receivedData)
                readCallback = nil
            } else {
                NSLog("   ⚠️ Callback == nil, данные накоплены в буфере")
            }
            return
        }
        
        // FF01 - реальные данные от принтера (ответы на команды)
        let isFF01 = uuidUpper == "FF01" || uuidUpper == "0000FF01-0000-1000-8000-00805F9B34FB"
        NSLog("   🔍 Проверка FF01: %@ (result: %@)", uuidUpper, isFF01 ? "YES" : "NO")
        if isFF01 {
            // Пробуем вывести как ASCII
            if let str = String(data: data, encoding: .ascii) {
                let printable = str.replacingOccurrences(of: "\r", with: "\\r")
                                  .replacingOccurrences(of: "\n", with: "\\n")
                                  .replacingOccurrences(of: "\0", with: "\\0")
                NSLog("   ASCII: %@", printable)
            }
            
            // Проверяем, начинается ли с 'V' (версия прошивки)
            if let str = String(data: data, encoding: .ascii), str.hasPrefix("V") {
                NSLog("   🔎 Версия (ASCII): %@", str.trimmingCharacters(in: .whitespacesAndNewlines))
            }
            
            // Накапливаем данные в буфер
            receivedData.append(data)
            NSLog("   Всего в буфере: %ld байт", receivedData.count)
            
            // Проверяем, есть ли callback
            if let callback = readCallback {
                NSLog("   ✅ Callback активен, вызываем сразу")
                callback(receivedData)
                readCallback = nil
            } else {
                NSLog("   ⚠️ Callback == nil, данные накоплены в буфере")
            }
            return
        }
    }
    
    /// Обработка flow control сообщений (FF03)
    private func handleFlowControl(data: Data) {
        creditLock.lock()
        defer { creditLock.unlock() }
        
        if data.count == 2 && data[0] == 0x01 {
            // Формат: [0x01, количество_кредитов]
            let credits = Int(data[1])
            
            if credits == 0x04 {
                // Инициализация - устанавливаем начальные кредиты
                availableCredits = 4
                NSLog("🔄 Flow Control: Инициализация, кредитов = %d", availableCredits)
            } else {
                // Добавляем кредиты
                availableCredits += credits
                NSLog("🔄 Flow Control: +%d кредитов, всего = %d", credits, availableCredits)
            }
        } else if data.count == 3 && data[0] == 0x02 {
            // Формат: [0x02, MTU_low, MTU_high]
            let mtu = Int(data[1]) | (Int(data[2]) << 8)
            mtuSize = mtu - 3 // Вычитаем заголовок
            NSLog("🔄 Flow Control: MTU = %d, размер пакета = %d", mtu, mtuSize)
        } else {
            NSLog("⚠️ Flow Control: Неизвестный формат данных")
        }
    }
    
    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        if let error = error {
            NSLog("❌ Ошибка записи: %@", error.localizedDescription)
            awaitingWriteAck = false
            pendingCommandTimeout = nil
        } else {
            NSLog("✅ Данные успешно записаны в characteristic")
            if awaitingWriteAck {
                let t = pendingCommandTimeout ?? 5.0
                NSLog("⏳ Старт ожидания ответа: %.1f сек", t)
                DispatchQueue.main.asyncAfter(deadline: .now() + t) { [weak self] in
                    guard let self = self else { return }
                    if self.readCallback != nil {
                        NSLog("⏱️ Таймаут ожидания ответа")
                        NSLog("   Получено байт в буфере: %ld", self.receivedData.count)
                        if !self.receivedData.isEmpty {
                            let hex = self.receivedData.map { String(format: "%02X", $0) }.joined(separator: " ")
                            NSLog("   Данные в буфере: %@", hex)
                        }
                        self.readCallback?(self.receivedData.isEmpty ? nil : self.receivedData)
                        self.readCallback = nil
                    }
                    self.awaitingWriteAck = false
                    self.pendingCommandTimeout = nil
                }
            }
        }
    }
}