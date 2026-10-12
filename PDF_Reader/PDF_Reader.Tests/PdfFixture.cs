using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PDF_Reader.Tests;

internal static class PdfFixture
{
    public static byte[] Create(bool encrypted = false, int permissions = -4)
    {
        byte[] id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] owner = [], user = [], key = [];
        if (encrypted)
        {
            owner = Rc4(MD5.HashData(Pad("owner-secret")).Take(5).ToArray(), Pad("reader-secret"));
            key = MD5.HashData(Pad("reader-secret").Concat(owner).Concat(BitConverter.GetBytes(permissions)).Concat(id).ToArray()).Take(5).ToArray();
            user = Rc4(key, Pad(""));
        }

        string Stream(int number, string content)
        {
            var bytes = Encoding.ASCII.GetBytes(content);
            if (encrypted)
            {
                var objectKey = MD5.HashData(key.Concat(new byte[] { (byte)number, 0, 0, 0, 0 }).ToArray()).Take(10).ToArray();
                bytes = Rc4(objectKey, bytes);
            }
            return $"<< /Length {bytes.Length} >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream";
        }

        // Two pages, a hierarchy of outlines, text, vector content, metadata and a link.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R /Annots [12 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(6, "0.16 0.2 0.3 rg BT /F1 24 Tf 50 760 Td (PaperDesk sample) Tj ET\nBT /F1 12 Tf 50 720 Td (Hello PDF. Hello reader. Searchable text.) Tj ET\n0.85 0.2 0.2 rg 50 675 495 3 re f\nBT /F1 12 Tf 50 640 Td (Edit text, annotate, save and print.) Tj ET"),
            Stream(7, "BT /F1 18 Tf 50 520 Td (Second chapter) Tj ET\nBT /F1 12 Tf 50 480 Td (Hello landscape.) Tj ET"),
            "<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 3 >>",
            "<< /Title (First chapter) /Parent 8 0 R /Next 10 0 R /Dest [3 0 R /Fit] /First 11 0 R /Last 11 0 R /Count 1 >>",
            "<< /Title (Second chapter) /Parent 8 0 R /Prev 9 0 R /Dest [4 0 R /Fit] >>",
            "<< /Title (Details) /Parent 9 0 R /Dest [3 0 R /XYZ 0 640 0] >>",
            "<< /Type /Annot /Subtype /Link /Rect [50 630 250 655] /Border [0 0 0] /Dest [4 0 R /Fit] >>"
        };
        if (encrypted)
            objects.Add($"<< /Filter /Standard /V 1 /R 2 /Length 40 /P {permissions} /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> >>");
        return Build(objects, encrypted ? $" /Encrypt 13 0 R /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]" : "");
    }

    private static string PlainStream(string text) => $"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}\nendstream";

    public static byte[] CreateForm() => Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 12 Tf 0 g) /NeedAppearances true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R 7 0 R] >>",
        PlainStream("BT /F1 20 Tf 50 730 Td (AcroForm sample) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Initial) /Rect [50 600 300 628] /P 3 0 R /F 4 /DA (/F1 12 Tf 0 g) /MK << /BC [0.3 0.3 0.3] /BG [0.95 0.95 1] >> /BS << /W 1 /S /S >> >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Accept) /V /Off /AS /Off /Rect [50 550 70 570] /P 3 0 R /F 4 /AP << /N << /Off 8 0 R /Yes 9 0 R >> >> >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 22 >>\nstream\n0 0 20 20 re 1 w 0 G S\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 45 >>\nstream\n0 0 20 20 re 1 w 0 G S 3 10 m 8 4 l 17 17 l S\nendstream"
    ]);

    /// <summary>A document with a signature field: PaperDesk must keep its bytes and append changes.</summary>
    public static byte[] CreateSigned() => Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R /Annots [5 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /F 132 /P 3 0 R /V 7 0 R >>",
        PlainStream("BT /F1 18 Tf 50 760 Td (Signed contract) Tj ET"),
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <00> >>"
    ]);

    /// <summary>Invisible text over a blank page, as produced by OCR of a scan.</summary>
    public static byte[] CreateOcrLayer() => Build([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        PlainStream("BT 3 Tr /F1 24 Tf 50 700 Td (Hidden OCR text) Tj ET")
    ]);

    /// <summary>A review rectangle with its popup and a web link.</summary>
    public static byte[] CreateMarkup() => Build([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 8 0 R >> >> /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
        PlainStream("BT /F1 18 Tf 50 760 Td (Review copy) Tj ET"),
        "<< /Type /Annot /Subtype /Square /Rect [100 600 200 700] /C [1 0 0] /Contents (Review box) /T (Reviewer) /Popup 6 0 R >>",
        "<< /Type /Annot /Subtype /Popup /Rect [220 600 400 700] /Parent 5 0 R >>",
        "<< /Type /Annot /Subtype /Link /Rect [50 50 150 80] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    ]);

    /// <summary>A very tall page (a receipt or a scroll).</summary>
    public static byte[] CreateTall() => Build([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 3000] /Contents 4 0 R >>",
        PlainStream("0 0 1 rg 0 0 100 100 re f")
    ]);

    /// <summary>A coloured 500 x 700 mm canvas, tiled 3 x 3 on A4 with default glue margins.</summary>
    public static byte[] CreatePoster() => Build([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1417.3228 1984.252] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        PlainStream("0.9 0.95 1 rg 0 0 1418 1985 re f 0.1 0.35 0.65 RG 5 w 40 40 1337 1904 re S " +
            "0.8 0.15 0.2 rg 50 1650 700 250 re f 0.1 0.6 0.3 rg 850 800 500 550 re f " +
            "0.1 0.35 0.65 RG 12 w 50 50 m 1350 1900 l S 1350 50 m 50 1900 l S " +
            "1 1 1 rg BT /F1 70 Tf 90 1770 Td (PaperDesk POSTER) Tj ET " +
            "0.1 0.2 0.3 rg BT /F1 40 Tf 100 1550 Td (500 x 700 mm / A4 tiles) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    ]);

    private static byte[] Build(List<string> objects, string extraTrailer = "")
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R" + extraTrailer + $" >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static readonly byte[] Padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    private static byte[] Pad(string password) => Encoding.ASCII.GetBytes(password).Concat(Padding).Take(32).ToArray();

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var result = new byte[data.Length];
        var a = 0;
        j = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 255;
            j = (j + state[a]) & 255;
            (state[a], state[j]) = (state[j], state[a]);
            result[i] = (byte)(data[i] ^ state[(state[a] + state[j]) & 255]);
        }
        return result;
    }
}
