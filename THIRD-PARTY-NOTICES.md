# Сторонние компоненты

**PdfPig 0.1.14** (Apache-2.0, UglyToad) читает встроенные 3D-потоки из снимка PDF; версия закреплена NuGet lock-файлом. [Исходный код версии](https://github.com/UglyToad/PdfPig/tree/88172af1c4d4f440949f59c94966c3880e3f6032), полный текст лицензии: `third-party/pdfpig/LICENSE`.

**nanoPRC** (GNU AGPLv3, Copyright 2023–2026 CascadiaVoxel LLC) включён в отдельный `PaperDeskModelWorker.exe`. [Исходный проект](https://github.com/mvrhel/nanoPRC). Снимок исходников загружен 12.10.2026, SHA256 исходного ZIP: `6F27527636419D5ACD179C3498CC35020818289AF515481490D99C1B7F71714E`. Исходники библиотеки, исходники работника PaperDesk, полный текст AGPLv3, уведомления компонентов и самостоятельная CMake-сборка поставляются в `third-party/nanoprc/`. Работник обменивается проверяемыми бинарными файлами PD3D с отдельно реализованным просмотрщиком и не загружает библиотеку PRC в процесс WPF. Изменения работника распространяются в исходниках под AGPLv3.

Патчи nanoPRC от 13.10.2026 изменяют `source/src/prc_api.c` и `source/include/prc_api.h`: размещение экземпляров/прототипов, ссылки на геометрию, локальные координаты и поворот с неравномерным масштабом. Полное описание и команды проверок находятся в `third-party/nanoprc/BUILD.md`; изменённые исходники и регрессии включены в поставку под AGPLv3.

**zlib 1.3.1**, Copyright Jean-loup Gailly и Mark Adler: [исходный код](https://github.com/madler/zlib/tree/v1.3.1). Используется отдельным работником PRC. Исходники и полная лицензия включены в `third-party/zlib/`.

U3D reader работника реализован по формату [ECMA-363](https://ecma-international.org/publications-and-standards/standards/ecma-363/) и поддерживает начальный несжатый профиль. Исходники исследовательского Python-конвертера в приложение не включены. D3D11/DXGI и компилятор HLSL предоставляются Windows; собственный рендер находится в `Native3D/Renderer.cpp`.

PaperDesk использует **PDFium** — библиотеку Chromium для обработки PDF: [исходный код](https://pdfium.googlesource.com/pdfium/), [лицензия BSD](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE), [лицензии сторонних компонентов](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/third_party/).

Бинарные сборки поставляются NuGet-пакетом **bblanchon.PDFium.Win32 157.0.8086**: [репозиторий и лицензии](https://github.com/bblanchon/pdfium-binaries), Apache-2.0 для инфраструктуры сборки. Полные уведомления для PDFium и его компонентов взяты из официальной поставки `chromium/8086` и включены в `third-party/pdfium/`; при распространении сохранять весь этот каталог. Автономный дистрибутив также содержит лицензии .NET в `third-party/dotnet/`.

Встраиваемый Segoe UI берётся из установленной Windows во время пользовательской операции добавления/замены текста. Сам файл шрифта не поставляется вместе с приложением.
