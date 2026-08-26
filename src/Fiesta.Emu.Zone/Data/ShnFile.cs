using System.Text;

namespace Fiesta.Emu.Zone.Data;

/// <summary>One column of an `.shn` table: its name, its wire type code, and its width in bytes.</summary>
public sealed record ShnColumn(string Name, uint TypeCode, int Length);

/// <summary>A decoded `.shn` table — the binary sibling of <see cref="ShineTable"/>.
///
/// <para>The mob tables (`MobInfo`, `MobInfoServer`, `MobWeapon`) are SHN, not text, so reading them needs
/// the container format as well as the field layout. The layout comes from the PDB (`tools/pdb_types.py`);
/// this is the container.</para>
///
/// <para><b>Format.</b> A 32-byte crypt header, then a <c>uint32</c> total file length at offset 32, then
/// the remainder encrypted with a rolling XOR. Decrypted, the body is a four-word header
/// (<c>header, recordCount, defaultRecordLength, columnCount</c>), a column table of
/// <c>{ char name[48]; uint32 type; int32 length; }</c>, and then the records.</para></summary>
public sealed class ShnFile
{
    private static readonly Encoding Korean;

    static ShnFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Korean = Encoding.GetEncoding(949);
    }

    public required string Name { get; init; }
    public required IReadOnlyList<ShnColumn> Columns { get; init; }

    /// <summary>Rows as column-name to value. Values are boxed primitives or strings, matching the column's
    /// type code — callers use the typed accessors rather than casting.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object>> Rows { get; init; }

    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>An integer column. Every numeric SHN type widens to <see cref="long"/> without loss except
    /// <c>float</c>, which is truncated — no SHN column this project reads is a float.</summary>
    public static int Int(IReadOnlyDictionary<string, object> row, string column)
        => row.TryGetValue(column, out var v)
            ? v switch
            {
                byte b => b, sbyte sb => sb, short s => s, ushort us => us,
                int i => i, uint ui => (int)ui, long l => (int)l, ulong ul => (int)ul,
                float f => (int)f, _ => 0,
            }
            : throw new KeyNotFoundException($"no column '{column}'");

    public static string Str(IReadOnlyDictionary<string, object> row, string column)
        => row.TryGetValue(column, out var v) ? v as string ?? v.ToString() ?? "" : "";

    /// <summary>The rolling XOR the client and server both use.
    ///
    /// <para>It runs BACKWARDS from the end of the buffer, and the key for each byte is derived from the
    /// previous key and the byte's index. Running it forwards produces plausible-looking garbage rather
    /// than an obvious failure, which is the trap.</para></summary>
    public static void Crypt(byte[] data, int offset, int length)
    {
        var key = (byte)length;
        for (var i = offset + length - 1; i >= offset; i--)
        {
            data[i] ^= key;

            var j = (byte)(i - offset);
            var next = (byte)(j & 0x0F);
            next = (byte)(next + 0x55);
            next = (byte)(next ^ (byte)(j * 11));
            next = (byte)(next ^ key);
            next = (byte)(next ^ 0xAA);
            key = next;
        }
    }

    public static ShnFile Load(string path)
    {
        byte[] body;
        using (var file = File.OpenRead(path))
        using (var head = new BinaryReader(file))
        {
            head.ReadBytes(32);                                   // crypt header, not needed to read
            var declared = head.ReadInt32();
            if (declared != file.Length)
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)}: declared length {declared} != actual {file.Length}; not an SHN file");
            body = head.ReadBytes(declared - 36);
            Crypt(body, 0, body.Length);
        }

        using var stream = new MemoryStream(body);
        using var r = new BinaryReader(stream, Korean, leaveOpen: true);

        r.ReadUInt32();                                           // header word, unused
        var recordCount = r.ReadUInt32();
        var defaultRecordLength = r.ReadUInt32();
        var columnCount = r.ReadUInt32();

        var columns = new List<ShnColumn>((int)columnCount);
        var unnamed = 0;
        var fixedWidth = 2;                                       // the 2-byte per-record length prefix
        for (var i = 0; i < columnCount; i++)
        {
            var raw = r.ReadBytes(48);
            var end = Array.IndexOf(raw, (byte)0);
            var name = Korean.GetString(raw, 0, end < 0 ? raw.Length : end).Trim();
            var type = r.ReadUInt32();
            var len = r.ReadInt32();
            if (name.Length < 2) name = $"Undefined{unnamed++}";
            columns.Add(new ShnColumn(name, type, len));
            fixedWidth += len;
        }

        var rows = new List<IReadOnlyDictionary<string, object>>((int)recordCount);
        for (var n = 0; n < recordCount && stream.Position < stream.Length; n++)
        {
            var start = stream.Position;
            var rowLength = r.ReadUInt16();                       // per-record, differs when a column varies
            var row = new Dictionary<string, object>(columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns)
                row[c.Name] = ReadValue(r, c);
            rows.Add(row);

            // Trust the record's own length over the sum of the columns: a variable-length column makes them
            // disagree, and following the declared length keeps the stream aligned either way.
            stream.Position = start + (rowLength > 0 ? rowLength : fixedWidth);
        }

        return new ShnFile
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Columns = columns,
            Rows = rows,
        };
    }

    private static object ReadValue(BinaryReader r, ShnColumn c) => c.TypeCode switch
    {
        1 or 12 or 16 => r.ReadByte(),
        20 => r.ReadSByte(),
        2 => r.ReadUInt16(),
        13 or 21 => r.ReadInt16(),
        3 or 11 or 18 or 27 => r.ReadUInt32(),
        22 => r.ReadInt32(),
        29 => r.ReadUInt64(),
        5 => r.ReadSingle(),
        9 or 10 or 24 => FixedString(r, c.Length),
        26 => NullTerminated(r),
        _ => Skip(r, c.Length),
    };

    private static string FixedString(BinaryReader r, int length)
    {
        var raw = r.ReadBytes(length);
        var end = Array.IndexOf(raw, (byte)0);
        return Korean.GetString(raw, 0, end < 0 ? raw.Length : end).Trim();
    }

    private static string NullTerminated(BinaryReader r)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = r.ReadByte()) != 0) bytes.Add(b);
        return Korean.GetString(bytes.ToArray());
    }

    private static object Skip(BinaryReader r, int length)
    {
        r.ReadBytes(length);
        return 0;
    }
}
