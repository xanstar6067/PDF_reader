namespace PDF_Reader.Models;

public sealed record OcrOptions
{
    public bool Enabled { get; set; } = true;
    public string[] Languages { get; set; } = ["rus", "eng"];
    public int Dpi { get; set; } = 300;
    public int PageSegmentation { get; set; } = 3;
    public bool UseDictionaries { get; set; } = true;
    public bool PreserveSpaces { get; set; }

    public void Validate()
    {
        if (Languages is null || Languages.Length is < 1 or > 8 || Languages.Any(l => !IsLanguageCode(l)) || Languages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Languages.Length)
            throw new ArgumentException("Выберите от одного до восьми разных языков OCR.");
        if (Dpi is not (200 or 300 or 400 or 600)) throw new ArgumentException("Разрешение OCR: 200, 300, 400 или 600 DPI.");
        if (PageSegmentation is not (3 or 6 or 7 or 11)) throw new ArgumentException("Неподдерживаемый режим разметки OCR.");
    }

    public static bool IsLanguageCode(string? code) => code is { Length: > 0 and <= 64 } && char.IsAsciiLetter(code[0]) &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && code is not ("osd" or "equ");
}
