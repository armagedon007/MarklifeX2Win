# MarklifeWin

Windows приложение для печати через Bluetooth на принтеры Marklife.

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

## Структура

- `MarklifeWin.csproj` - проект
- `App.xaml` - точка входа
- `MainWindow.xaml` - основное окно
- `Bluetooth/` - Bluetooth менеджер
- `Print/` - движок печати
- `NamedPipe/` - NamedPipe сервер для CUPS
