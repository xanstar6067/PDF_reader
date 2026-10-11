# Карта проекта PDF Reader

## Назначение и состояние
Заготовка настольного приложения для чтения PDF. Сейчас содержит стандартное пустое окно WPF; открытие и отображение PDF ещё не реализованы.

## Технологии
C#, WPF, .NET 10 (`net10.0-windows`), nullable reference types и implicit usings. Внешние пакеты пока отсутствуют.

## Структура
- `PDF_Reader/PDF_Reader.slnx` — решение Visual Studio.
- `PDF_Reader/PDF_Reader/PDF_Reader.csproj` — проект Windows-приложения.
- `PDF_Reader/PDF_Reader/App.xaml` и `App.xaml.cs` — запуск приложения и ресурсы.
- `PDF_Reader/PDF_Reader/MainWindow.xaml` и `MainWindow.xaml.cs` — главное окно.
- `PDF_Reader/PDF_Reader/AssemblyInfo.cs` — настройки поиска ресурсов WPF.
- `.gitignore` — исключения для результатов сборки и локальных настроек.
- `AGENTS.md` — инструкции для работы с проектом.

## Сборка и запуск
На Windows с .NET 10 SDK:

```powershell
dotnet build PDF_Reader/PDF_Reader/PDF_Reader.csproj
dotnet run --project PDF_Reader/PDF_Reader/PDF_Reader.csproj
```

Тестовые проекты отсутствуют. Файлы `bin`, `obj`, `.vs` и `*.user` не должны попадать в репозиторий.
