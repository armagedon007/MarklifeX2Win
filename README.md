# MarklifeWin

Windows приложение для печати через Bluetooth на принтеры Marklife X2

## Требования

- Windows 10/11
- .NET 8.0 SDK
- Visual Studio 2022 или VS Code

## Сборка

```bash
dotnet restore
dotnet build
dotnet run
```

## Установка принтера в систему (запустить от имени администратора)

```bash
scripts\setup-printer.bat
```

## Удаление принтера
```bash
scripts\remove-printer.bat
```

## Структура

- `MarklifeWin.csproj` - проект
- `App.xaml` - точка входа
- `MainWindow.xaml` - основное окно
- `Bluetooth/` - Bluetooth менеджер
- `Print/` - движок печати
- `NamedPipe/` - NamedPipe сервер для CUPS

## Лицензия
MIT