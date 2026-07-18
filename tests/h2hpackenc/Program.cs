using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Unit tests for the reworked HPACK encoder (full static table + per-connection
// dynamic table + Huffman coding). The strongest correctness check is a round
// trip through our OWN decoder — an encoder+decoder pair that stay in lockstep
// prove the dynamic-table accounting matches. Interop with real peers
// (Kestrel/HttpClient decoding our encoder output) is covered separately by
// h2clienttest / h2semantics.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

static bool SameHeaders(List<(string Name, string Value)> a, List<(string Name, string Value)> b)
{
    if (a.Count != b.Count) return false;
    for (var i = 0; i < a.Count; i++)
        if (a[i].Name != b[i].Name || a[i].Value != b[i].Value) return false;
    return true;
}

// =========================================================================
// 1. Huffman encoder <-> decoder round trip (standalone)
// =========================================================================
Console.WriteLine("=== Huffman encode/decode round trip ===");
{
    var rng = new Random(1234);
    var mismatches = 0;
    for (var iter = 0; iter < 5000; iter++)
    {
        var len = rng.Next(0, 40);
        var bytes = new byte[len];
        rng.NextBytes(bytes);
        var encoded = HuffmanEncoder.Encode(bytes);
        var decoded = HuffmanDecoder.Decode(encoded);
        // Decoder yields one char per source octet.
        var back = new byte[decoded.Length];
        for (var i = 0; i < decoded.Length; i++) back[i] = (byte) decoded[i];
        if (!back.AsSpan().SequenceEqual(bytes)) mismatches++;
    }
    Check("5000-round Huffman round trip", mismatches == 0, $"{mismatches} mismatches");

    // Known lengths: 'a' is a 5-bit code -> 10 x 'a' = 50 bits = 7 bytes.
    Check("EncodedByteLength('aaaaaaaaaa') == 7",
          HuffmanEncoder.EncodedByteLength(Encoding.ASCII.GetBytes("aaaaaaaaaa")) == 7,
          HuffmanEncoder.EncodedByteLength(Encoding.ASCII.GetBytes("aaaaaaaaaa")).ToString());
}

// =========================================================================
// 2. Static table: exact index + name index
// =========================================================================
Console.WriteLine("\n=== Static table references ===");
{
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();

    // :method GET is static index 2 -> single indexed byte 0x82.
    var block = enc.EncodeHeaderBlock([(":method", "GET")]);
    Check(":method GET encodes to one byte 0x82", block.Length == 1 && block[0] == 0x82,
          BitConverter.ToString(block));
    Check(":method GET round-trips", SameHeaders(dec.DecodeHeaderBlock(block), [(":method", "GET")]));

    // content-type is a static NAME (index 31) but the value is a literal.
    var enc2 = new HPACKEncoder();
    var dec2 = new HPACKDecoder();
    var block2 = enc2.EncodeHeaderBlock([("content-type", "text/plain")]);
    var decoded2 = dec2.DecodeHeaderBlock(block2);
    Check("content-type: text/plain round-trips", SameHeaders(decoded2, [("content-type", "text/plain")]));
    Check("content-type value shorter than raw (Huffman or index used)",
          block2.Length < ("content-type".Length + "text/plain".Length),
          $"{block2.Length} bytes");
}

// =========================================================================
// 3. Dynamic table: a repeated field collapses to a one-byte index
// =========================================================================
Console.WriteLine("\n=== Dynamic table indexing ===");
{
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();

    var hdr = new List<(string, string)> { ("x-custom-header", "some-repeated-value") };

    var first  = enc.EncodeHeaderBlock(hdr);
    var d1      = dec.DecodeHeaderBlock(first);
    var second = enc.EncodeHeaderBlock(hdr);
    var d2      = dec.DecodeHeaderBlock(second);

    Check("first block round-trips", SameHeaders(d1, hdr));
    Check("second block round-trips", SameHeaders(d2, hdr));
    Check("repeated field is a single index byte the 2nd time",
          second.Length == 1 && (second[0] & 0x80) != 0,
          $"first={first.Length}B second={second.Length}B");
    Check("second is dramatically smaller than first", second.Length < first.Length);
}

// =========================================================================
// 4. Multi-block sequence stays in sync across the connection
// =========================================================================
Console.WriteLine("\n=== Multi-block encoder/decoder sync ===");
{
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();

    var blocks = new List<(string, string)>[]
    {
        [(":status", "200"), ("content-type", "text/plain"), ("server", "demo")],
        [(":status", "200"), ("content-type", "application/json"), ("server", "demo")],
        [(":status", "404"), ("content-type", "text/plain"), ("server", "demo")],
    };

    var allOk = true;
    foreach (var b in blocks)
    {
        var block = enc.EncodeHeaderBlock(new List<(string, string)>(b));
        var back  = dec.DecodeHeaderBlock(block);
        if (!SameHeaders(back, new List<(string, string)>(b))) allOk = false;
    }
    Check("3 sequential mixed blocks all round-trip", allOk);

    // "server: demo" repeated across blocks -> indexed after the first.
    var enc2 = new HPACKEncoder();
    var dec2 = new HPACKDecoder();
    var s1 = enc2.EncodeHeaderBlock([("server", "demo")]);
    _ = dec2.DecodeHeaderBlock(s1);
    var s2 = enc2.EncodeHeaderBlock([("server", "demo")]);
    Check("repeated 'server: demo' indexed on reuse", s2.Length < s1.Length && SameHeaders(dec2.DecodeHeaderBlock(s2), [("server", "demo")]));
}

// =========================================================================
// 5. Sensitive + volatile fields are NOT indexed
// =========================================================================
Console.WriteLine("\n=== Never-index / no-index policy ===");
{
    // authorization -> never indexed; must not collapse to an index on repeat.
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();
    var auth = new List<(string, string)> { ("authorization", "Bearer secret-token-value") };
    var a1 = enc.EncodeHeaderBlock(auth);
    var da1 = dec.DecodeHeaderBlock(a1);
    var a2 = enc.EncodeHeaderBlock(auth);
    var da2 = dec.DecodeHeaderBlock(a2);
    Check("authorization round-trips", SameHeaders(da1, auth) && SameHeaders(da2, auth));
    Check("authorization NOT collapsed to a 1-byte index on repeat", a2.Length > 1,
          $"a1={a1.Length}B a2={a2.Length}B");

    // content-length -> no-index (volatile); repeat also stays literal.
    var enc2 = new HPACKEncoder();
    var dec2 = new HPACKDecoder();
    var cl = new List<(string, string)> { ("content-length", "12345") };
    var c1 = enc2.EncodeHeaderBlock(cl);
    var dc1 = dec2.DecodeHeaderBlock(c1);
    var c2 = enc2.EncodeHeaderBlock(cl);
    var dc2 = dec2.DecodeHeaderBlock(c2);
    Check("content-length round-trips and stays literal", SameHeaders(dc1, cl) && SameHeaders(dc2, cl) && c2.Length > 1);
}

// =========================================================================
// 6. Dynamic table size update signaling
// =========================================================================
Console.WriteLine("\n=== Dynamic table size update ===");
{
    // Shrinking to 0: the next block must carry a size update (0x20 prefix) and
    // the decoder must still round-trip it, with no dynamic indexing possible.
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();
    enc.SetMaxDynamicTableSize(0);
    var hdr = new List<(string, string)> { ("x-thing", "value1") };
    var b1 = enc.EncodeHeaderBlock(hdr);
    Check("block after shrink starts with a size update (0x20)", (b1[0] & 0xE0) == 0x20,
          $"0x{b1[0]:X2}");
    Check("round-trips through decoder after size update", SameHeaders(dec.DecodeHeaderBlock(b1), hdr));

    var b2 = enc.EncodeHeaderBlock(hdr);
    Check("with table size 0, repeat is NOT indexed", b2.Length > 1 && SameHeaders(dec.DecodeHeaderBlock(b2), hdr));

    // Growing back to 4096 (== our decoder's advertised limit): still valid, and
    // dynamic indexing resumes.
    enc.SetMaxDynamicTableSize(4096);
    var g1 = enc.EncodeHeaderBlock(hdr);
    var dg1 = dec.DecodeHeaderBlock(g1);
    var g2 = enc.EncodeHeaderBlock(hdr);
    var dg2 = dec.DecodeHeaderBlock(g2);
    Check("after growing back, round-trips and re-indexes", SameHeaders(dg1, hdr) && SameHeaders(dg2, hdr) && g2.Length < g1.Length);
}

// =========================================================================
// 7. A realistic request header set round-trips
// =========================================================================
Console.WriteLine("\n=== Realistic header set ===");
{
    var enc = new HPACKEncoder();
    var dec = new HPACKDecoder();
    var req = new List<(string, string)>
    {
        (":method", "GET"),
        (":scheme", "https"),
        (":authority", "example.com"),
        (":path", "/index.html"),
        ("user-agent", "HTTP2FromScratch/1.0"),
        ("accept", "text/html,application/xhtml+xml"),
        ("accept-encoding", "gzip, deflate"),
        ("cookie", "session=abc123"),
    };
    var block = enc.EncodeHeaderBlock(req);
    var back  = dec.DecodeHeaderBlock(block);
    var rawSize = req.Sum(h => h.Item1.Length + h.Item2.Length);
    Check("realistic request round-trips", SameHeaders(back, req));
    Check("compressed smaller than raw", block.Length < rawSize, $"{block.Length}B vs {rawSize}B raw");
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
