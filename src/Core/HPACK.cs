namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Text;


/// <summary>
/// HPACK header compression as defined in RFC 7541.
/// Implements the static table, dynamic table, integer encoding/decoding,
/// and the indexed/literal header field representations.
/// 
/// Note: Huffman coding is supported for decoding (required for interop)
/// but we use raw string encoding for outgoing headers (simpler, always valid).
/// </summary>
public sealed class HPACKDecoder
{

    #region Static Table (RFC 7541, Appendix A)

    // Internal so the HPACKEncoder can build its reverse index from the exact
    // same 61-entry table (RFC 7541, Appendix A) rather than duplicating it.
    internal static readonly (string Name, string? Value)[] StaticTable =
    [
        ("",                          null),              //  0 — unused (1-indexed)
        (":authority",                null),              //  1
        (":method",                   "GET"),             //  2
        (":method",                   "POST"),            //  3
        (":path",                     "/"),               //  4
        (":path",                     "/index.html"),     //  5
        (":scheme",                   "http"),            //  6
        (":scheme",                   "https"),           //  7
        (":status",                   "200"),             //  8
        (":status",                   "204"),             //  9
        (":status",                   "206"),             // 10
        (":status",                   "304"),             // 11
        (":status",                   "400"),             // 12
        (":status",                   "404"),             // 13
        (":status",                   "500"),             // 14
        ("accept-charset",            null),              // 15
        ("accept-encoding",           "gzip, deflate"),   // 16
        ("accept-language",           null),              // 17
        ("accept-ranges",             null),              // 18
        ("accept",                    null),              // 19
        ("access-control-allow-origin", null),            // 20
        ("age",                       null),              // 21
        ("allow",                     null),              // 22
        ("authorization",             null),              // 23
        ("cache-control",             null),              // 24
        ("content-disposition",       null),              // 25
        ("content-encoding",          null),              // 26
        ("content-language",          null),              // 27
        ("content-length",            null),              // 28
        ("content-location",          null),              // 29
        ("content-range",             null),              // 30
        ("content-type",              null),              // 31
        ("cookie",                    null),              // 32
        ("date",                      null),              // 33
        ("etag",                      null),              // 34
        ("expect",                    null),              // 35
        ("expires",                   null),              // 36
        ("from",                      null),              // 37
        ("host",                      null),              // 38
        ("if-match",                  null),              // 39
        ("if-modified-since",         null),              // 40
        ("if-none-match",             null),              // 41
        ("if-range",                  null),              // 42
        ("if-unmodified-since",       null),              // 43
        ("last-modified",             null),              // 44
        ("link",                      null),              // 45
        ("location",                  null),              // 46
        ("max-forwards",             null),               // 47
        ("proxy-authenticate",        null),              // 48
        ("proxy-authorization",       null),              // 49
        ("range",                     null),              // 50
        ("referer",                   null),              // 51
        ("refresh",                   null),              // 52
        ("retry-after",               null),              // 53
        ("server",                    null),              // 54
        ("set-cookie",                null),              // 55
        ("strict-transport-security", null),              // 56
        ("transfer-encoding",         null),              // 57
        ("user-agent",                null),              // 58
        ("vary",                      null),              // 59
        ("via",                       null),              // 60
        ("www-authenticate",          null)               // 61
    ];

    #endregion


    #region Dynamic Table

    /// <summary>
    /// The dynamic table is a FIFO list of (name, value) pairs.
    /// New entries are prepended; eviction happens from the end.
    /// Per RFC 7541 Section 4.1, each entry occupies name.Length + value.Length + 32 bytes.
    /// </summary>
    private readonly List<(string Name, string Value)> dynamicTable = [];
    private int                                        dynamicTableSize;
    private int                                        maxDynamicTableSize = 4096;

    /// <summary>
    /// The upper bound a dynamic table size update may set — the
    /// SETTINGS_HEADER_TABLE_SIZE value we advertise to the peer (RFC 7541,
    /// Section 6.3 / RFC 9113, Section 6.5.2). A size update exceeding this is a
    /// decoding error. Defaults to the HPACK default of 4096; a connection that
    /// advertises a different value should set this to match.
    /// </summary>
    public int HeaderTableSizeLimit { get; set; } = 4096;

    private void AddToDynamicTable(string Name, string Value)
    {

        var entrySize = Name.Length + Value.Length + 32;

        // Evict from the end until we have room
        while (dynamicTableSize + entrySize > maxDynamicTableSize && dynamicTable.Count > 0)
        {
            var last = dynamicTable[^1];
            dynamicTableSize -= (last.Name.Length + last.Value.Length + 32);
            dynamicTable.RemoveAt(dynamicTable.Count - 1);
        }

        // If the entry itself is larger than the max table size, just clear everything
        if (entrySize <= maxDynamicTableSize)
        {
            dynamicTable.Insert(0, (Name, Value));
            dynamicTableSize += entrySize;
        }

    }

    /// <summary>
    /// Lookup by combined index: 1..61 = static table, 62+ = dynamic table.
    /// </summary>
    private (string Name, string? Value) LookupIndex(int Index)
    {

        if (Index < 1)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               $"HPACK index 0 is invalid");

        if (Index < StaticTable.Length)
            return StaticTable[Index];

        var dynamicIndex = Index - StaticTable.Length;

        if (dynamicIndex >= dynamicTable.Count)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               $"HPACK dynamic table index {Index} out of range (dynamic table has {dynamicTable.Count} entries)");

        var entry = dynamicTable[dynamicIndex];

        return (entry.Name, entry.Value);

    }

    #endregion


    #region Integer Decoding (RFC 7541, Section 5.1)

    /// <summary>
    /// Decode an HPACK integer with the given prefix size (in bits).
    /// Returns the decoded value and advances the offset.
    /// </summary>
    private static int DecodeInteger(ReadOnlySpan<byte> Data, ref int Offset, int PrefixBits)
    {

        // A truncated header block (the prefix byte is missing entirely) is a
        // compression error, not an unhandled IndexOutOfRangeException.
        if (Offset >= Data.Length)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               "HPACK truncated integer");

        var maxPrefix  = (1 << PrefixBits) - 1;
        var value      = Data[Offset] & maxPrefix;
        Offset++;

        if (value < maxPrefix)
            return value;

        // Multi-byte encoding
        int m = 0;

        while (Offset < Data.Length)
        {

            var b = Data[Offset++];
            value += (b & 0x7F) << m;
            m     += 7;

            if ((b & 0x80) == 0)
                break;

            if (m > 28)
                throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                                   "HPACK integer overflow");

        }

        return value;

    }

    #endregion


    #region String Decoding (RFC 7541, Section 5.2)

    /// <summary>
    /// Decode an HPACK string literal. The high bit of the first byte indicates
    /// whether Huffman coding is used (1) or raw octets (0).
    /// </summary>
    private static string DecodeString(ReadOnlySpan<byte> Data, ref int Offset)
    {

        // The length prefix byte carries the Huffman flag; if the block ends
        // exactly here (a header field whose value literal is truncated away),
        // that's a compression error rather than an IndexOutOfRangeException.
        if (Offset >= Data.Length)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               "HPACK truncated string literal");

        var huffman = (Data[Offset] & 0x80) != 0;
        var length  = DecodeInteger(Data, ref Offset, 7);

        if (Offset + length > Data.Length)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               "HPACK string length exceeds block size");

        var raw = Data.Slice(Offset, length);
        Offset += length;

        if (huffman)
            return HuffmanDecoder.Decode(raw);

        return Encoding.ASCII.GetString(raw);

    }

    #endregion


    #region Header Block Decoding (RFC 7541, Section 6)

    /// <summary>
    /// Decode a complete HPACK header block into a list of (name, value) header pairs.
    /// </summary>
    public List<(string Name, string Value)> DecodeHeaderBlock(ReadOnlySpan<byte> Block)
    {

        var headers = new List<(string Name, string Value)>();
        var offset  = 0;

        // RFC 7541, Section 4.2: a dynamic table size update MUST occur at the
        // beginning of a header block, before any header field representation.
        // Once we've emitted a field, a later size update is a decoding error.
        var sawHeaderField = false;

        while (offset < Block.Length)
        {

            var b = Block[offset];

            if ((b & 0x80) != 0)
            {
                // 6.1 Indexed Header Field Representation
                var index = DecodeInteger(Block, ref offset, 7);
                var (name, value) = LookupIndex(index);
                headers.Add((name, value ?? ""));
                sawHeaderField = true;
            }
            else if ((b & 0xC0) == 0x40)
            {
                // 6.2.1 Literal Header Field with Incremental Indexing
                var index = DecodeInteger(Block, ref offset, 6);
                string name;

                if (index > 0)
                {
                    (name, _) = LookupIndex(index);
                }
                else
                {
                    name = DecodeString(Block, ref offset);
                }

                var value = DecodeString(Block, ref offset);
                AddToDynamicTable(name, value);
                headers.Add((name, value));
                sawHeaderField = true;
            }
            else if ((b & 0xF0) == 0x00)
            {
                // 6.2.2 Literal Header Field without Indexing
                var index = DecodeInteger(Block, ref offset, 4);
                string name;

                if (index > 0)
                {
                    (name, _) = LookupIndex(index);
                }
                else
                {
                    name = DecodeString(Block, ref offset);
                }

                var value = DecodeString(Block, ref offset);
                headers.Add((name, value));
                sawHeaderField = true;
            }
            else if ((b & 0xF0) == 0x10)
            {
                // 6.2.3 Literal Header Field Never Indexed
                var index = DecodeInteger(Block, ref offset, 4);
                string name;

                if (index > 0)
                {
                    (name, _) = LookupIndex(index);
                }
                else
                {
                    name = DecodeString(Block, ref offset);
                }

                var value = DecodeString(Block, ref offset);
                headers.Add((name, value));
                sawHeaderField = true;
            }
            else if ((b & 0xE0) == 0x20)
            {
                // 6.3 Dynamic Table Size Update

                // Section 4.2: it MUST appear before any header field.
                if (sawHeaderField)
                    throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                        "Dynamic table size update must occur at the start of a header block");

                var newSize = DecodeInteger(Block, ref offset, 5);

                // Section 6.3: the new size MUST NOT exceed the limit we
                // advertised (SETTINGS_HEADER_TABLE_SIZE).
                if (newSize > HeaderTableSizeLimit)
                    throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                        $"Dynamic table size update {newSize} exceeds advertised limit {HeaderTableSizeLimit}");

                maxDynamicTableSize = newSize;

                // Evict if necessary
                while (dynamicTableSize > maxDynamicTableSize && dynamicTable.Count > 0)
                {
                    var last = dynamicTable[^1];
                    dynamicTableSize -= (last.Name.Length + last.Value.Length + 32);
                    dynamicTable.RemoveAt(dynamicTable.Count - 1);
                }
            }
            else
            {
                throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                                   $"Unknown HPACK representation byte: 0x{b:X2}");
            }

        }

        return headers;

    }

    #endregion

}


/// <summary>
/// HPACK encoder (RFC 7541). Now a full-featured mirror of
/// <see cref="HPACKDecoder"/>:
/// - the complete 61-entry static table (shared with the decoder) for exact
///   and name-only indexed references;
/// - a per-connection dynamic table, kept byte-for-byte in step with the peer
///   decoder's, so a repeated header field is sent as a one-byte index;
/// - Huffman coding of string literals whenever it is shorter than raw.
///
/// Because the dynamic table is stateful, one encoder instance belongs to one
/// connection, and <see cref="EncodeHeaderBlock"/> calls MUST happen in the
/// same order the resulting blocks are written to the wire (the decoder
/// replays that order) — callers serialize encode+write accordingly.
/// </summary>
public sealed class HPACKEncoder
{

    // Reverse lookups over the shared static table (RFC 7541, Appendix A).
    private static readonly Dictionary<(string Name, string Value), int> StaticExact;
    private static readonly Dictionary<string, int>                      StaticName;

    static HPACKEncoder()
    {

        var table = HPACKDecoder.StaticTable;
        StaticExact = new(table.Length);
        StaticName  = new(table.Length);

        for (var i = 1; i < table.Length; i++)
        {
            var (name, value) = table[i];
            if (value is not null)
                StaticExact.TryAdd((name, value), i);
            StaticName.TryAdd(name, i);   // keep the lowest index for a name
        }

    }


    #region Dynamic table (encoder side — mirrors the peer decoder)

    private readonly List<(string Name, string Value)> dynamicTable = [];
    private int  dynamicTableSize;
    private int  maxDynamicTableSize = 4096;
    private int? pendingSizeUpdate;                 // signal at the next block start

    /// <summary>
    /// Field names we deliberately never insert into the dynamic table:
    /// values that change nearly every message (indexing them just churns the
    /// table and evicts useful entries). They are sent as "literal without
    /// indexing".
    /// </summary>
    private static readonly HashSet<string> NoIndexNames = new(StringComparer.Ordinal)
    {
        ":path", "age", "content-length", "content-range", "date", "etag",
        "expires", "last-modified", "if-modified-since", "if-none-match",
        "if-range", "if-unmodified-since", "location", "retry-after"
    };

    /// <summary>
    /// Sensitive field names sent as "literal never indexed" (RFC 7541,
    /// Section 7.1.3) — kept out of the dynamic table so their values are never
    /// exposed to a compression-based side channel.
    /// </summary>
    private static readonly HashSet<string> NeverIndexNames = new(StringComparer.Ordinal)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie"
    };

    /// <summary>
    /// Adjust the encoder's dynamic table size limit to the value the peer
    /// advertised in SETTINGS_HEADER_TABLE_SIZE. A reduction evicts entries and
    /// queues a dynamic table size update to be emitted at the start of the next
    /// header block (RFC 7541, Section 6.3), so our table never exceeds what the
    /// peer's decoder will keep.
    /// </summary>
    public void SetMaxDynamicTableSize(int NewMax)
    {
        NewMax = Math.Max(0, NewMax);
        if (NewMax == maxDynamicTableSize)
            return;

        maxDynamicTableSize = NewMax;

        while (dynamicTableSize > maxDynamicTableSize && dynamicTable.Count > 0)
            EvictOldest();

        pendingSizeUpdate = NewMax;
    }

    private void EvictOldest()
    {
        var last = dynamicTable[^1];
        dynamicTableSize -= (last.Name.Length + last.Value.Length + 32);
        dynamicTable.RemoveAt(dynamicTable.Count - 1);
    }

    // Mirrors HPACKDecoder.AddToDynamicTable exactly (same 32-byte per-entry
    // overhead, same eviction) so both tables stay in lockstep.
    private void AddToDynamicTable(string Name, string Value)
    {

        var entrySize = Name.Length + Value.Length + 32;

        while (dynamicTableSize + entrySize > maxDynamicTableSize && dynamicTable.Count > 0)
            EvictOldest();

        if (entrySize <= maxDynamicTableSize)
        {
            dynamicTable.Insert(0, (Name, Value));
            dynamicTableSize += entrySize;
        }

    }

    // Position (0 = newest) of an exact (name,value) entry, or -1.
    private int FindDynamicExact(string Name, string Value)
    {
        for (var p = 0; p < dynamicTable.Count; p++)
            if (dynamicTable[p].Name == Name && dynamicTable[p].Value == Value)
                return p;
        return -1;
    }

    // Position (0 = newest) of the newest entry with this name, or -1.
    private int FindDynamicName(string Name)
    {
        for (var p = 0; p < dynamicTable.Count; p++)
            if (dynamicTable[p].Name == Name)
                return p;
        return -1;
    }

    #endregion


    /// <summary>
    /// Encode a list of headers into an HPACK header block.
    /// </summary>
    public byte[] EncodeHeaderBlock(IEnumerable<(string Name, string Value)> Headers)
    {

        using var ms = new MemoryStream(256);

        // A pending dynamic table size update MUST come first (RFC 7541,
        // Section 4.2): 001 prefix, 5-bit integer.
        if (pendingSizeUpdate is { } newSize)
        {
            EncodeInteger(ms, newSize, 5, 0x20);
            pendingSizeUpdate = null;
        }

        foreach (var (name, value) in Headers)
        {

            // 1. Exact match in the static table -> Indexed Header Field (6.1).
            if (StaticExact.TryGetValue((name, value), out var staticIdx))
            {
                EncodeInteger(ms, staticIdx, 7, 0x80);
                continue;
            }

            // 2. Exact match in the dynamic table -> Indexed Header Field (6.1).
            var dynExact = FindDynamicExact(name, value);
            if (dynExact >= 0)
            {
                EncodeInteger(ms, HPACKDecoder.StaticTable.Length + dynExact, 7, 0x80);
                continue;
            }

            // Otherwise a literal — resolve a name index (static preferred, as it
            // never shifts; else the newest dynamic entry with the same name).
            int nameIndex;
            if (StaticName.TryGetValue(name, out var sn))
                nameIndex = sn;
            else
            {
                var dn = FindDynamicName(name);
                nameIndex = dn >= 0 ? HPACKDecoder.StaticTable.Length + dn : 0;
            }

            if (NeverIndexNames.Contains(name))
            {
                // Literal Never Indexed (6.2.3): 0001 prefix, 4-bit name index.
                EncodeInteger(ms, nameIndex, 4, 0x10);
                if (nameIndex == 0) EncodeString(ms, name);
                EncodeString(ms, value);
            }
            else if (NoIndexNames.Contains(name))
            {
                // Literal without Indexing (6.2.2): 0000 prefix, 4-bit name index.
                EncodeInteger(ms, nameIndex, 4, 0x00);
                if (nameIndex == 0) EncodeString(ms, name);
                EncodeString(ms, value);
            }
            else
            {
                // Literal with Incremental Indexing (6.2.1): 01 prefix, 6-bit.
                EncodeInteger(ms, nameIndex, 6, 0x40);
                if (nameIndex == 0) EncodeString(ms, name);
                EncodeString(ms, value);
                AddToDynamicTable(name, value);
            }

        }

        return ms.ToArray();

    }


    #region Integer Encoding (RFC 7541, Section 5.1)

    private static void EncodeInteger(Stream Output, int Value, int PrefixBits, byte Prefix)
    {

        var maxPrefix = (1 << PrefixBits) - 1;

        if (Value < maxPrefix)
        {
            Output.WriteByte((byte) (Prefix | Value));
            return;
        }

        Output.WriteByte((byte) (Prefix | maxPrefix));
        Value -= maxPrefix;

        while (Value >= 128)
        {
            Output.WriteByte((byte) (0x80 | (Value & 0x7F)));
            Value >>= 7;
        }

        Output.WriteByte((byte) Value);

    }

    #endregion


    #region String Encoding (RFC 7541, Section 5.2)

    /// <summary>
    /// Encode a string literal, choosing Huffman coding when it is strictly
    /// shorter than the raw octets (RFC 7541, Section 5.2 — the length octet's
    /// high bit flags which form was used).
    /// </summary>
    private static void EncodeString(Stream Output, string Value)
    {

        var raw     = Encoding.ASCII.GetBytes(Value);
        var huffLen = HuffmanEncoder.EncodedByteLength(raw);

        if (huffLen < raw.Length)
        {
            var huffman = HuffmanEncoder.Encode(raw);
            EncodeInteger(Output, huffman.Length, 7, 0x80);   // H bit set
            Output.Write(huffman, 0, huffman.Length);
        }
        else
        {
            EncodeInteger(Output, raw.Length, 7, 0x00);       // raw octets
            Output.Write(raw, 0, raw.Length);
        }

    }

    #endregion

}


#region Huffman Decoder (RFC 7541, Appendix B)

/// <summary>
/// A minimal but complete Huffman decoder for HPACK.
/// Uses a lookup table derived from the HPACK Huffman code table.
/// We only need decoding — our encoder sends raw strings.
/// </summary>
public static class HuffmanDecoder
{

    // Each entry: (code, bitLength) indexed by symbol (0-256, where 256 = EOS).
    // Internal so HuffmanEncoder can encode from the same canonical table.
    internal static readonly (uint Code, int Bits)[] HuffmanTable =
    [
        (0x1ff8,     13), // 0
        (0x7fffd8,   23), // 1
        (0xfffffe2,  28), // 2
        (0xfffffe3,  28), // 3
        (0xfffffe4,  28), // 4
        (0xfffffe5,  28), // 5
        (0xfffffe6,  28), // 6
        (0xfffffe7,  28), // 7
        (0xfffffe8,  28), // 8
        (0xffffea,   24), // 9
        (0x3ffffffc, 30), // 10
        (0xfffffe9,  28), // 11
        (0xfffffea,  28), // 12
        (0x3ffffffd, 30), // 13
        (0xfffffeb,  28), // 14
        (0xfffffec,  28), // 15
        (0xfffffed,  28), // 16
        (0xfffffee,  28), // 17
        (0xfffffef,  28), // 18
        (0xffffff0,  28), // 19
        (0xffffff1,  28), // 20
        (0xffffff2,  28), // 21
        (0x3ffffffe, 30), // 22
        (0xffffff3,  28), // 23
        (0xffffff4,  28), // 24
        (0xffffff5,  28), // 25
        (0xffffff6,  28), // 26
        (0xffffff7,  28), // 27
        (0xffffff8,  28), // 28
        (0xffffff9,  28), // 29
        (0xffffffa,  28), // 30
        (0xffffffb,  28), // 31
        (0x14,        6), // 32 ' '
        (0x3f8,      10), // 33 '!'
        (0x3f9,      10), // 34 '"'
        (0xffa,      12), // 35 '#'
        (0x1ff9,     13), // 36 '$'
        (0x15,        6), // 37 '%'
        (0xf8,        8), // 38 '&'
        (0x7fa,      11), // 39 '\''
        (0x3fa,      10), // 40 '('
        (0x3fb,      10), // 41 ')'
        (0xf9,        8), // 42 '*'
        (0x7fb,      11), // 43 '+'
        (0xfa,        8), // 44 ','
        (0x16,        6), // 45 '-'
        (0x17,        6), // 46 '.'
        (0x18,        6), // 47 '/'
        (0x0,         5), // 48 '0'
        (0x1,         5), // 49 '1'
        (0x2,         5), // 50 '2'
        (0x19,        6), // 51 '3'
        (0x1a,        6), // 52 '4'
        (0x1b,        6), // 53 '5'
        (0x1c,        6), // 54 '6'
        (0x1d,        6), // 55 '7'
        (0x1e,        6), // 56 '8'
        (0x1f,        6), // 57 '9'
        (0x5c,        7), // 58 ':'
        (0xfb,        8), // 59 ';'
        (0x7ffc,     15), // 60 '<'
        (0x20,        6), // 61 '='
        (0xffb,      12), // 62 '>'
        (0x3fc,      10), // 63 '?'
        (0x1ffa,     13), // 64 '@'
        (0x21,        6), // 65 'A'
        (0x5d,        7), // 66 'B'
        (0x5e,        7), // 67 'C'
        (0x5f,        7), // 68 'D'
        (0x60,        7), // 69 'E'
        (0x61,        7), // 70 'F'
        (0x62,        7), // 71 'G'
        (0x63,        7), // 72 'H'
        (0x64,        7), // 73 'I'
        (0x65,        7), // 74 'J'
        (0x66,        7), // 75 'K'
        (0x67,        7), // 76 'L'
        (0x68,        7), // 77 'M'
        (0x69,        7), // 78 'N'
        (0x6a,        7), // 79 'O'
        (0x6b,        7), // 80 'P'
        (0x6c,        7), // 81 'Q'
        (0x6d,        7), // 82 'R'
        (0x6e,        7), // 83 'S'
        (0x6f,        7), // 84 'T'
        (0x70,        7), // 85 'U'
        (0x71,        7), // 86 'V'
        (0x72,        7), // 87 'W'
        (0xfc,        8), // 88 'X'
        (0x73,        7), // 89 'Y'
        (0xfd,        8), // 90 'Z'
        (0x1ffb,     13), // 91 '['
        (0x7fff0,    19), // 92 '\'
        (0x1ffc,     13), // 93 ']'
        (0x3ffc,     14), // 94 '^'
        (0x22,        6), // 95 '_'
        (0x7ffd,     15), // 96 '`'
        (0x3,         5), // 97 'a'
        (0x23,        6), // 98 'b'
        (0x4,         5), // 99 'c'
        (0x24,        6), // 100 'd'
        (0x5,         5), // 101 'e'
        (0x25,        6), // 102 'f'
        (0x26,        6), // 103 'g'
        (0x27,        6), // 104 'h'
        (0x6,         5), // 105 'i'
        (0x74,        7), // 106 'j'
        (0x75,        7), // 107 'k'
        (0x28,        6), // 108 'l'
        (0x29,        6), // 109 'm'
        (0x2a,        6), // 110 'n'
        (0x7,         5), // 111 'o'
        (0x2b,        6), // 112 'p'
        (0x76,        7), // 113 'q'
        (0x2c,        6), // 114 'r'
        (0x8,         5), // 115 's'
        (0x9,         5), // 116 't'
        (0x2d,        6), // 117 'u'
        (0x77,        7), // 118 'v'
        (0x78,        7), // 119 'w'
        (0x79,        7), // 120 'x'
        (0x7a,        7), // 121 'y'
        (0x7b,        7), // 122 'z'
        (0x7ffe,     15), // 123 '{'
        (0x7fc,      11), // 124 '|'
        (0x3ffd,     14), // 125 '}'
        (0x1ffd,     13), // 126 '~'
        (0xffffffc,  28), // 127
        (0xfffe6,    20), // 128
        (0x3fffd2,   22), // 129
        (0xfffe7,    20), // 130
        (0xfffe8,    20), // 131
        (0x3fffd3,   22), // 132
        (0x3fffd4,   22), // 133
        (0x3fffd5,   22), // 134
        (0x7fffd9,   23), // 135
        (0x3fffd6,   22), // 136
        (0x7fffda,   23), // 137
        (0x7fffdb,   23), // 138
        (0x7fffdc,   23), // 139
        (0x7fffdd,   23), // 140
        (0x7fffde,   23), // 141
        (0xffffeb,   24), // 142
        (0x7fffdf,   23), // 143
        (0xffffec,   24), // 144
        (0xffffed,   24), // 145
        (0x3fffd7,   22), // 146
        (0x7fffe0,   23), // 147
        (0xffffee,   24), // 148
        (0x7fffe1,   23), // 149
        (0x7fffe2,   23), // 150
        (0x7fffe3,   23), // 151
        (0x7fffe4,   23), // 152
        (0x1fffdc,   21), // 153
        (0x3fffd8,   22), // 154
        (0x7fffe5,   23), // 155
        (0x3fffd9,   22), // 156
        (0x7fffe6,   23), // 157
        (0x7fffe7,   23), // 158
        (0xffffef,   24), // 159
        (0x3fffda,   22), // 160
        (0x1fffdd,   21), // 161
        (0xfffe9,    20), // 162
        (0x3fffdb,   22), // 163
        (0x3fffdc,   22), // 164
        (0x7fffe8,   23), // 165
        (0x7fffe9,   23), // 166
        (0x1fffde,   21), // 167
        (0x7fffea,   23), // 168
        (0x3fffdd,   22), // 169
        (0x3fffde,   22), // 170
        (0xfffff0,   24), // 171
        (0x1fffdf,   21), // 172
        (0x3fffdf,   22), // 173
        (0x7fffeb,   23), // 174
        (0x7fffec,   23), // 175
        (0x1fffe0,   21), // 176
        (0x1fffe1,   21), // 177
        (0x3fffe0,   22), // 178
        (0x1fffe2,   21), // 179
        (0x7fffed,   23), // 180
        (0x3fffe1,   22), // 181
        (0x7fffee,   23), // 182
        (0x7fffef,   23), // 183
        (0xfffea,    20), // 184
        (0x3fffe2,   22), // 185
        (0x3fffe3,   22), // 186
        (0x3fffe4,   22), // 187
        (0x7ffff0,   23), // 188
        (0x3fffe5,   22), // 189
        (0x3fffe6,   22), // 190
        (0x7ffff1,   23), // 191
        (0x3ffffe0,  26), // 192
        (0x3ffffe1,  26), // 193
        (0xfffeb,    20), // 194
        (0x7fff1,    19), // 195
        (0x3fffe7,   22), // 196
        (0x7ffff2,   23), // 197
        (0x3fffe8,   22), // 198
        (0x1ffffec,  25), // 199
        (0x3ffffe2,  26), // 200
        (0x3ffffe3,  26), // 201
        (0x3ffffe4,  26), // 202
        (0x7ffffde,  27), // 203
        (0x7ffffdf,  27), // 204
        (0x3ffffe5,  26), // 205
        (0xfffff1,   24), // 206
        (0x1ffffed,  25), // 207
        (0x7fff2,    19), // 208
        (0x1fffe3,   21), // 209
        (0x3ffffe6,  26), // 210
        (0x7ffffe0,  27), // 211
        (0x7ffffe1,  27), // 212
        (0x3ffffe7,  26), // 213
        (0x7ffffe2,  27), // 214
        (0xfffff2,   24), // 215
        (0x1fffe4,   21), // 216
        (0x1fffe5,   21), // 217
        (0x3ffffe8,  26), // 218
        (0x3ffffe9,  26), // 219
        (0xffffffd,  28), // 220
        (0x7ffffe3,  27), // 221
        (0x7ffffe4,  27), // 222
        (0x7ffffe5,  27), // 223
        (0xfffec,    20), // 224
        (0xfffff3,   24), // 225
        (0xfffed,    20), // 226
        (0x1fffe6,   21), // 227
        (0x3fffe9,   22), // 228
        (0x1fffe7,   21), // 229
        (0x1fffe8,   21), // 230
        (0x7ffff3,   23), // 231
        (0x3fffea,   22), // 232
        (0x3fffeb,   22), // 233
        (0x1ffffee,  25), // 234
        (0x1ffffef,  25), // 235
        (0xfffff4,   24), // 236
        (0xfffff5,   24), // 237
        (0x3ffffea,  26), // 238
        (0x7ffff4,   23), // 239
        (0x3ffffeb,  26), // 240
        (0x7ffffe6,  27), // 241
        (0x3ffffec,  26), // 242
        (0x3ffffed,  26), // 243
        (0x7ffffe7,  27), // 244
        (0x7ffffe8,  27), // 245
        (0x7ffffe9,  27), // 246
        (0x7ffffea,  27), // 247
        (0x7ffffeb,  27), // 248
        (0xffffffe,  28), // 249
        (0x7ffffec,  27), // 250
        (0x7ffffed,  27), // 251
        (0x7ffffee,  27), // 252
        (0x7ffffef,  27), // 253
        (0x7fffff0,  27), // 254
        (0x3ffffee,  26), // 255
        (0x3fffffff, 30), // 256 = EOS
    ];


    #region Decode Trie

    /// <summary>
    /// A node in the bit-level decoding trie: two children (for the next bit
    /// being 0 or 1) while internal, or a resolved <see cref="Symbol"/> (0-256,
    /// 256 = EOS) once it's a leaf. Built once from <see cref="HuffmanTable"/>.
    /// </summary>
    private sealed class TrieNode
    {
        public TrieNode?  Zero;
        public TrieNode?  One;
        public int        Symbol = -1;
    }

    private static readonly TrieNode Root = BuildTrie();

    /// <summary>
    /// Insert every (code, bitLength) pair from <see cref="HuffmanTable"/> into
    /// a bit trie. Also doubles as a self-check of that 257-entry literal table:
    /// a prefix collision here (one symbol's code being a prefix of another's)
    /// means the table itself is transcribed wrong, and this throws immediately
    /// at class-init time rather than silently mis-decoding at runtime.
    /// </summary>
    private static TrieNode BuildTrie()
    {

        var root = new TrieNode();

        for (var symbol = 0; symbol < HuffmanTable.Length; symbol++)
        {

            var (code, bitLength) = HuffmanTable[symbol];
            var node = root;

            for (var bitIndex = bitLength - 1; bitIndex >= 0; bitIndex--)
            {

                if (node.Symbol >= 0)
                    throw new InvalidOperationException(
                        $"HPACK Huffman table is not prefix-free: symbol {symbol}'s code has an earlier, shorter code as a prefix");

                var bit = (int) ((code >> bitIndex) & 1);
                node = bit == 0 ? (node.Zero ??= new TrieNode())
                                 : (node.One  ??= new TrieNode());

            }

            if (node.Symbol >= 0 || node.Zero is not null || node.One is not null)
                throw new InvalidOperationException(
                    $"HPACK Huffman table is not prefix-free: symbol {symbol}'s code collides with another symbol's code");

            node.Symbol = symbol;

        }

        return root;

    }

    #endregion


    /// <summary>
    /// Decode Huffman-encoded bytes into a string via a bit-level trie walk —
    /// one array-lookup per bit instead of the O(n×257) linear symbol scan a
    /// naive decoder would do. Each step resolves at most one symbol: HPACK's
    /// shortest codeword is 5 bits, so a single bit can never complete two
    /// codes at once, which is what keeps this simple (no multi-symbol-per-step
    /// bookkeeping needed, unlike byte-at-a-time table designs).
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> Data)
    {

        var result = new StringBuilder(Data.Length * 2);

        var node         = Root;
        var pendingDepth = 0;   // bits consumed since the last completed symbol
        var pendingBits  = 0;   // their actual values (MSB-first) — only used to validate trailing padding

        foreach (var b in Data)
        {
            for (var bitIndex = 7; bitIndex >= 0; bitIndex--)
            {

                var bit  = (b >> bitIndex) & 1;
                var next = bit == 0 ? node.Zero : node.One;

                if (next is null)
                    throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                        "HPACK Huffman: invalid code (no matching Huffman codeword)");

                node          = next;
                pendingDepth += 1;
                pendingBits   = (pendingBits << 1) | bit;

                if (node.Symbol >= 0)
                {

                    // RFC 7541 Section 5.2: the EOS codeword must never appear
                    // as an actual encoded symbol, only ever as a trailing
                    // padding prefix (checked below, once input is exhausted).
                    if (node.Symbol == 256)
                        throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                            "HPACK Huffman: EOS symbol must not be explicitly encoded");

                    result.Append((char) node.Symbol);
                    node          = Root;
                    pendingDepth  = 0;
                    pendingBits   = 0;

                }

            }
        }

        // Whatever's left is padding (RFC 7541 Section 5.2): at most 7 bits,
        // and it must be a prefix of the EOS codeword — whose top bits are all
        // 1 — so the leftover bits themselves must all be 1.
        if (pendingDepth > 7)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               "HPACK Huffman: too many padding bits");

        if (pendingDepth > 0 && pendingBits != (1 << pendingDepth) - 1)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR,
                                               "HPACK Huffman: padding bits are not all 1s");

        return result.ToString();

    }

}

#endregion


#region Huffman Encoder (RFC 7541, Section 5.2)

/// <summary>
/// Huffman encoder for HPACK string literals, the mirror of
/// <see cref="HuffmanDecoder"/>. Emits each octet's canonical codeword MSB-first
/// and pads the final partial byte with 1-bits (a prefix of the EOS codeword),
/// exactly as RFC 7541 Section 5.2 requires. Encodes from the same
/// <see cref="HuffmanDecoder.HuffmanTable"/>, so a round trip is exact by
/// construction.
/// </summary>
public static class HuffmanEncoder
{

    /// <summary>
    /// The number of bytes <paramref name="Data"/> would occupy Huffman-encoded
    /// (rounded up to a whole byte). Lets the caller pick the shorter of the
    /// Huffman and raw representations without encoding twice.
    /// </summary>
    public static int EncodedByteLength(ReadOnlySpan<byte> Data)
    {
        var bits = 0L;
        foreach (var octet in Data)
            bits += HuffmanDecoder.HuffmanTable[octet].Bits;
        return (int) ((bits + 7) / 8);
    }

    /// <summary>
    /// Huffman-encode <paramref name="Data"/> into a freshly allocated byte
    /// array, padding the last byte's unused low bits with 1s.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> Data)
    {

        var output = new byte[EncodedByteLength(Data)];

        var bitPos = 0;   // absolute bit offset into output, MSB-first

        foreach (var octet in Data)
        {

            var (code, bits) = HuffmanDecoder.HuffmanTable[octet];

            // Emit the codeword's bits from most- to least-significant.
            for (var bitIndex = bits - 1; bitIndex >= 0; bitIndex--)
            {
                if (((code >> bitIndex) & 1) != 0)
                    output[bitPos >> 3] |= (byte) (0x80 >> (bitPos & 7));
                bitPos++;
            }

        }

        // Pad the remainder of the final byte with 1-bits (EOS prefix).
        while ((bitPos & 7) != 0)
        {
            output[bitPos >> 3] |= (byte) (0x80 >> (bitPos & 7));
            bitPos++;
        }

        return output;

    }

}

#endregion
