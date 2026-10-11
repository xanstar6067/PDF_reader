# Сторонние компоненты

PaperDesk использует **PDFium** — библиотеку Chromium для обработки PDF: [исходный код](https://pdfium.googlesource.com/pdfium/), [лицензия BSD](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE), [лицензии сторонних компонентов](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/third_party/).

Бинарные сборки поставляются NuGet-пакетом **bblanchon.PDFium.Win32 157.0.8086**: [репозиторий и лицензии](https://github.com/bblanchon/pdfium-binaries), Apache-2.0 для инфраструктуры сборки. Полные уведомления для PDFium и его компонентов взяты из официальной поставки `chromium/8086` и включены в `third-party/pdfium/`; при распространении сохранять весь этот каталог. Автономный дистрибутив также содержит лицензии .NET в `third-party/dotnet/`.

Встраиваемый Segoe UI берётся из установленной Windows во время пользовательской операции добавления/замены текста. Сам файл шрифта не поставляется вместе с приложением.
