using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PDF_Reader.Services;

/// <summary>
/// Creates compact TrueType subsets for embedding into PDF. Glyphs are renumbered, hinting and
/// OpenType layout tables are dropped: PDF text is positioned explicitly, so they are not needed.
/// </summary>
internal sealed class TrueTypeSubsetter
{
    private const ushort ArgsAreWords = 0x0001, HasScale = 0x0008, MoreComponents = 0x0020;
    private const ushort HasXyScale = 0x0040, HasTwoByTwo = 0x0080, HasInstructions = 0x0100;

    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _cmap = [];
    private readonly int[] _loca;
    private readonly int _glyphCount;
    private readonly int _metricCount;
    private readonly string _family;
    private readonly string _style;
    private readonly string _postScriptName;

    public bool CanSubset { get; }

    public TrueTypeSubsetter(byte[] data)
    {
        _data = data;
        var version = U32(0);
        if (version is not (0x00010000 or 0x74727565)) throw new NotSupportedException("Поддерживаются только шрифты TrueType.");
        int count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(data, record, 4);
            var offset = checked((int)U32(record + 8));
            var length = checked((int)U32(record + 12));
            if (offset < 0 || length < 0 || offset + length > data.Length) throw new InvalidDataException("Повреждена таблица шрифта " + tag + ".");
            _tables[tag] = (offset, length);
        }
        foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "cmap", "loca", "glyf", "OS/2", "post" })
            if (!_tables.ContainsKey(required)) throw new InvalidDataException("В шрифте нет таблицы " + required + ".");

        _glyphCount = U16(Table("maxp") + 4);
        _metricCount = Math.Clamp((int)U16(Table("hhea") + 34), 1, _glyphCount);
        var longLoca = I16(Table("head") + 50) == 1;
        _loca = new int[_glyphCount + 1];
        for (var i = 0; i <= _glyphCount; i++)
            _loca[i] = longLoca ? checked((int)U32(Table("loca") + i * 4)) : U16(Table("loca") + i * 2) * 2;
        ReadCmap();

        var fsType = U16(Table("OS/2") + 8);
        if ((fsType & 0x000F) == 0x0002) throw new NotSupportedException("Лицензия шрифта запрещает встраивание.");
        CanSubset = (fsType & 0x0100) == 0;

        _family = ReadName(1) ?? "Font";
        _style = ReadName(2) ?? "Regular";
        _postScriptName = new string((ReadName(6) ?? _family).Where(c => c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')).ToArray());
    }

    public bool Supports(int codepoint) => _cmap.ContainsKey(codepoint);

    /// <summary>Returns a font that contains only the given characters, or the original font if the license forbids subsetting.</summary>
    public byte[] Subset(IEnumerable<int> codepoints)
    {
        if (!CanSubset) return _data;
        var characters = codepoints.Distinct().Order().ToArray();
        var missing = characters.Where(c => !_cmap.ContainsKey(c)).ToArray();
        if (missing.Length > 0) throw new ArgumentException("Шрифт не содержит символы: " + string.Concat(missing.Take(12).Select(char.ConvertFromUtf32)));

        // Glyph 0 (.notdef) must stay first; composite glyphs pull in their components.
        var glyphs = new SortedSet<int>();
        var pending = new Stack<int>(characters.Select(c => _cmap[c]).Prepend(0));
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph >= _glyphCount || !glyphs.Add(glyph)) continue;
            foreach (var component in Components(glyph)) pending.Push(component);
        }
        var order = glyphs.ToArray();
        var remap = new Dictionary<int, int>();
        for (var i = 0; i < order.Length; i++) remap[order[i]] = i;

        var glyf = new MemoryStream();
        var loca = new List<int>();
        foreach (var glyph in order)
        {
            loca.Add((int)glyf.Length);
            glyf.Write(StripGlyph(glyph, remap));
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        loca.Add((int)glyf.Length);

        var tag = SubsetTag(characters);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = Copy("OS/2"),
            ["cmap"] = BuildCmap(characters.Select(c => (c, remap[_cmap[c]])).ToArray()),
            ["glyf"] = glyf.ToArray(),
            ["head"] = BuildHead(),
            ["hhea"] = BuildHhea(order.Length),
            ["hmtx"] = BuildHmtx(order),
            ["loca"] = BuildLoca(loca),
            ["maxp"] = BuildMaxp(order.Length),
            ["name"] = BuildName(tag + "+" + _postScriptName),
            ["post"] = BuildPost()
        };
        return Assemble(tables);
    }

    private int Table(string tag) => _tables[tag].Offset;
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));
    private short I16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
    private byte[] Copy(string tag) => _data.AsSpan(_tables[tag].Offset, _tables[tag].Length).ToArray();

    private void ReadCmap()
    {
        var cmap = Table("cmap");
        int count = U16(cmap + 2);
        int best = -1, bestScore = 0;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + i * 8;
            int platform = U16(record), encoding = U16(record + 2);
            var offset = cmap + checked((int)U32(record + 4));
            int format = U16(offset);
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4,
                (0, 4 or 6, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => 0
            };
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        if (best < 0) throw new NotSupportedException("В шрифте нет Unicode-таблицы символов.");

        if (U16(best) == 12)
        {
            var groups = U32(best + 12);
            for (var i = 0L; i < groups; i++)
            {
                var group = best + 16 + (int)i * 12;
                uint start = U32(group), end = U32(group + 4), glyph = U32(group + 8);
                for (var c = start; c <= end && c <= 0x10FFFF; c++) Add((int)c, (int)(glyph + c - start));
            }
            return;
        }
        var segments = U16(best + 6) / 2;
        var ends = best + 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var ranges = deltas + segments * 2;
        for (var i = 0; i < segments; i++)
        {
            int start = U16(starts + i * 2), end = U16(ends + i * 2), delta = I16(deltas + i * 2), range = U16(ranges + i * 2);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (range == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    var address = ranges + i * 2 + range + (c - start) * 2;
                    if (address + 2 > _data.Length) continue;
                    glyph = U16(address);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                Add(c, glyph);
            }
        }
    }

    private void Add(int codepoint, int glyph)
    {
        if (glyph > 0 && glyph < _glyphCount && codepoint is not (>= 0xD800 and <= 0xDFFF)) _cmap.TryAdd(codepoint, glyph);
    }

    private string? ReadName(int id)
    {
        if (!_tables.TryGetValue("name", out var table)) return null;
        int count = U16(table.Offset + 2), storage = table.Offset + U16(table.Offset + 4);
        for (var i = 0; i < count; i++)
        {
            var record = table.Offset + 6 + i * 12;
            if (U16(record) != 3 || U16(record + 2) != 1 || U16(record + 6) != id) continue;
            int length = U16(record + 8), offset = storage + U16(record + 10);
            if (offset + length <= _data.Length) return Encoding.BigEndianUnicode.GetString(_data, offset, length);
        }
        return null;
    }

    private ReadOnlySpan<byte> Glyph(int glyph)
    {
        var start = _tables["glyf"].Offset + _loca[glyph];
        var length = _loca[glyph + 1] - _loca[glyph];
        return length <= 0 ? [] : _data.AsSpan(start, length);
    }

    private IEnumerable<int> Components(int glyph)
    {
        var data = Glyph(glyph).ToArray();
        if (data.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(data) >= 0) yield break;
        var offset = 10;
        ushort flags;
        do
        {
            flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            yield return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            offset += ComponentLength(flags);
        } while ((flags & MoreComponents) != 0 && offset + 4 <= data.Length);
    }

    private static int ComponentLength(ushort flags) =>
        4 + ((flags & ArgsAreWords) != 0 ? 4 : 2) +
        ((flags & HasScale) != 0 ? 2 : (flags & HasXyScale) != 0 ? 4 : (flags & HasTwoByTwo) != 0 ? 8 : 0);

    private byte[] StripGlyph(int glyph, Dictionary<int, int> remap)
    {
        var data = Glyph(glyph).ToArray();
        if (data.Length < 10) return [];
        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        if (contours >= 0)
        {
            // Simple glyph: header, end points, instructions, then flags and coordinates.
            var instructionsAt = 10 + contours * 2;
            var instructionLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(instructionsAt));
            var rest = instructionsAt + 2 + instructionLength;
            var result = new byte[data.Length - instructionLength];
            data.AsSpan(0, instructionsAt).CopyTo(result);
            data.AsSpan(rest).CopyTo(result.AsSpan(instructionsAt + 2));
            return result;
        }
        var offset = 10;
        int last;
        ushort flags;
        do
        {
            last = offset;
            flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var component = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), (ushort)remap[component]);
            offset += ComponentLength(flags);
        } while ((flags & MoreComponents) != 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(last), (ushort)(flags & ~HasInstructions));
        return data[..offset];
    }

    private byte[] BuildHead()
    {
        var head = Copy("head");
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return head;
    }

    private byte[] BuildHhea(int count)
    {
        var hhea = Copy("hhea");
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)count);
        return hhea;
    }

    private byte[] BuildMaxp(int count)
    {
        var maxp = Copy("maxp");
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)count);
        return maxp;
    }

    private byte[] BuildHmtx(int[] order)
    {
        var hmtx = Table("hmtx");
        var result = new byte[order.Length * 4];
        for (var i = 0; i < order.Length; i++)
        {
            var glyph = order[i];
            var advance = U16(hmtx + Math.Min(glyph, _metricCount - 1) * 4);
            var bearing = glyph < _metricCount ? U16(hmtx + glyph * 4 + 2) : U16(hmtx + _metricCount * 4 + (glyph - _metricCount) * 2);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(i * 4), advance);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(i * 4 + 2), bearing);
        }
        return result;
    }

    private static byte[] BuildLoca(List<int> offsets)
    {
        var result = new byte[offsets.Count * 4];
        for (var i = 0; i < offsets.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), (uint)offsets[i]);
        return result;
    }

    private byte[] BuildPost()
    {
        var post = _data.AsSpan(Table("post"), 32).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        return post;
    }

    private static byte[] BuildCmap((int Code, int Glyph)[] map)
    {
        // Format 4 covers the BMP; format 12 is added only when supplementary characters are present.
        var runs = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in map.Where(m => m.Code < 0xFFFF))
        {
            if (runs.Count > 0 && runs[^1].End + 1 == code && runs[^1].Glyph + code - runs[^1].Start == glyph)
                runs[^1] = (runs[^1].Start, code, runs[^1].Glyph);
            else runs.Add((code, code, glyph));
        }
        runs.Add((0xFFFF, 0xFFFF, 0));
        if (runs.Count > 8000) throw new ArgumentException("Слишком много различных символов для одного текстового фрагмента.");
        var segments = runs.Count;
        var power = 1;
        while (power * 2 <= segments) power *= 2;
        var format4 = new byte[16 + segments * 8];
        var span = format4.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 4);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)format4.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(segments * 2));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)(power * 2));
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)Math.Log2(power));
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)(segments * 2 - power * 2));
        for (var i = 0; i < segments; i++)
        {
            var (start, end, glyph) = runs[i];
            BinaryPrimitives.WriteUInt16BigEndian(span[(14 + i * 2)..], (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(span[(16 + segments * 2 + i * 2)..], (ushort)start);
            var delta = end == 0xFFFF ? 1 : glyph - start;
            BinaryPrimitives.WriteUInt16BigEndian(span[(16 + segments * 4 + i * 2)..], (ushort)(delta & 0xFFFF));
        }

        var supplementary = map.Where(m => m.Code > 0xFFFF).ToArray();
        byte[]? format12 = null;
        if (supplementary.Length > 0)
        {
            format12 = new byte[16 + supplementary.Length * 12];
            BinaryPrimitives.WriteUInt16BigEndian(format12, 12);
            BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(4), (uint)format12.Length);
            BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(12), (uint)supplementary.Length);
            for (var i = 0; i < supplementary.Length; i++)
            {
                var group = format12.AsSpan(16 + i * 12);
                BinaryPrimitives.WriteUInt32BigEndian(group, (uint)supplementary[i].Code);
                BinaryPrimitives.WriteUInt32BigEndian(group[4..], (uint)supplementary[i].Code);
                BinaryPrimitives.WriteUInt32BigEndian(group[8..], (uint)supplementary[i].Glyph);
            }
        }

        var tableCount = format12 is null ? 1 : 2;
        var header = 4 + tableCount * 8;
        var result = new byte[header + format4.Length + (format12?.Length ?? 0)];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)tableCount);
        WriteRecord(0, 1, header);
        format4.CopyTo(result, header);
        if (format12 is not null)
        {
            WriteRecord(1, 10, header + format4.Length);
            format12.CopyTo(result, header + format4.Length);
        }
        return result;

        void WriteRecord(int index, ushort encoding, int offset)
        {
            var record = result.AsSpan(4 + index * 8);
            BinaryPrimitives.WriteUInt16BigEndian(record, 3);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], encoding);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], (uint)offset);
        }
    }

    private byte[] BuildName(string postScriptName)
    {
        var names = new (ushort Id, string Value)[]
        {
            (1, _family), (2, _style), (3, postScriptName), (4, _family + " " + _style), (6, postScriptName)
        };
        var strings = names.Select(n => Encoding.BigEndianUnicode.GetBytes(n.Value)).ToArray();
        var storage = 6 + names.Length * 12;
        var result = new byte[storage + strings.Sum(s => s.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)names.Length);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)storage);
        var offset = 0;
        for (var i = 0; i < names.Length; i++)
        {
            var record = result.AsSpan(6 + i * 12);
            BinaryPrimitives.WriteUInt16BigEndian(record, 3);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], 0x0409);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], names[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)offset);
            strings[i].CopyTo(result, storage + offset);
            offset += strings[i].Length;
        }
        return result;
    }

    private static string SubsetTag(int[] characters)
    {
        var hash = 17L;
        foreach (var c in characters) hash = unchecked(hash * 31 + c);
        var tag = new char[6];
        for (var i = 0; i < 6; i++) { tag[i] = (char)('A' + (int)((ulong)hash % 26)); hash = (long)((ulong)hash / 26); }
        return new string(tag);
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var power = 1;
        while (power * 2 <= count) power *= 2;
        var header = 12 + count * 16;
        var size = header + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)Math.Log2(power));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)(count * 16 - power * 16));
        var offset = header;
        var index = 0;
        var headOffset = 0;
        foreach (var (tag, table) in tables)
        {
            var record = result.AsSpan(12 + index++ * 16);
            Encoding.ASCII.GetBytes(tag, record);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(table));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)table.Length);
            if (tag == "head") headOffset = offset;
            table.CopyTo(result, offset);
            offset += (table.Length + 3) & ~3;
        }
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(result)));
        return result;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }
}
