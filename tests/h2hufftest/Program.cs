using System.Reflection;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Verifies the new trie-based HuffmanDecoder.Decode against:
//   1. A known-correct RFC 7541 test vector.
//   2. A from-scratch "oracle" re-implementation of the OLD O(n*257) linear-scan
//      algorithm (the one being replaced), differentially fuzzed against the
//      new decoder on thousands of randomly-generated, validly-encoded inputs.
//   3. Explicit padding edge cases: correct all-1s padding, corrupted padding,
//      over-length padding, and explicit EOS encoding.

var failures = 0;

void Check(bool condition, string description)
{
    if (condition) Console.WriteLine($"  ✓ {description}");
    else { Console.WriteLine($"  ✗ FAIL: {description}"); failures++; }
}

// Pull the private static HuffmanTable via reflection, so the oracle encoder
// and the oracle (old-algorithm) decoder both work off the EXACT same table
// data the production trie is built from — differential testing then isolates
// "did I re-implement the algorithm correctly", not "did I mistype the table".
var tableField = typeof(HuffmanDecoder).GetField("HuffmanTable", BindingFlags.NonPublic | BindingFlags.Static)!;
var table      = ((uint Code, int Bits)[]) tableField.GetValue(null)!;

byte[] OracleEncode(string s)
{
    var bits = new List<int>();
    foreach (var ch in s)
    {
        var (code, len) = table[(byte) ch];
        for (var i = len - 1; i >= 0; i--)
            bits.Add((int) ((code >> i) & 1));
    }
    // Pad with 1s to a byte boundary (valid EOS-prefix padding).
    while (bits.Count % 8 != 0)
        bits.Add(1);

    var bytes = new byte[bits.Count / 8];
    for (var i = 0; i < bits.Count; i++)
        if (bits[i] == 1)
            bytes[i / 8] |= (byte) (1 << (7 - (i % 8)));
    return bytes;
}

// Re-implementation of the OLD algorithm being replaced (O(n*257) linear scan),
// used purely as a differential oracle — not the code under test.
string OracleDecodeOld(ReadOnlySpan<byte> data)
{
    var result  = new StringBuilder(data.Length * 2);
    uint buffer = 0;
    int  bits   = 0;

    foreach (var b in data)
    {
        buffer = (buffer << 8) | b;
        bits  += 8;

        while (bits >= 5)
        {
            var matched = false;
            for (var sym = 0; sym < 256; sym++)
            {
                var (code, codeLen) = table[sym];
                if (bits >= codeLen)
                {
                    var candidate = buffer >> (bits - codeLen);
                    if (candidate == code)
                    {
                        result.Append((char) sym);
                        bits   -= codeLen;
                        buffer &= (uint) ((1L << bits) - 1);
                        matched = true;
                        break;
                    }
                }
            }
            if (!matched) break;
        }
    }

    if (bits > 7)
        throw new HTTP2ConnectionException(HTTP2ErrorCode.COMPRESSION_ERROR, "too many padding bits");

    return result.ToString();
}

// --- 1. RFC 7541 known vector ------------------------------------------------
Console.WriteLine("[test] RFC 7541 known vector");
{
    var encoded = new byte[] { 0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff };
    var decoded = HuffmanDecoder.Decode(encoded);
    Check(decoded == "www.example.com", $"\"www.example.com\" decodes correctly (got \"{decoded}\")");
}

// --- 2. Round-trip fuzz: encode with a known-correct encoder, decode with the
//        new trie decoder, assert we get the original string back -----------
Console.WriteLine("[test] Round-trip fuzz: OracleEncode -> HuffmanDecoder.Decode");
{
    var rnd = new Random(12345);
    var mismatches = 0;
    const int rounds = 5000;

    for (var i = 0; i < rounds; i++)
    {
        var len = rnd.Next(0, 60);
        var sb  = new StringBuilder(len);
        for (var j = 0; j < len; j++)
            sb.Append((char) rnd.Next(0, 256));   // full byte range, incl. rare long-code symbols

        var original = sb.ToString();
        var encoded  = OracleEncode(original);

        string newResult;
        try { newResult = HuffmanDecoder.Decode(encoded); } catch (Exception ex) { newResult = $"<threw {ex.GetType().Name}: {ex.Message}>"; }

        if (newResult != original)
        {
            mismatches++;
            if (mismatches <= 5)
                Console.WriteLine($"    mismatch: original=\"{original}\" decoded=\"{newResult}\"");
        }
    }

    Check(mismatches == 0, $"{rounds} random strings (full byte range, up to 60 chars): encode -> decode round-trips exactly ({mismatches} mismatches)");
}

// --- 2b. Differential check against the old algorithm, restricted to inputs
//         that don't trip the old algorithm's OWN separate bug: its `uint`
//         bit-buffer accumulates via unbounded left-shifts and silently loses
//         high bits once >32 bits of unresolved long codes pile up. Restrict
//         to symbols <128 (mostly short, common codes) so old is a valid
//         oracle here — this is a secondary cross-check, not the primary one.
Console.WriteLine("[test] Differential cross-check vs. old algorithm (short-code symbols only)");
{
    var rnd = new Random(54321);
    var mismatches = 0;
    const int rounds = 2000;

    for (var i = 0; i < rounds; i++)
    {
        var len = rnd.Next(0, 60);
        var sb  = new StringBuilder(len);
        for (var j = 0; j < len; j++)
            sb.Append((char) rnd.Next(32, 127));   // printable ASCII: all <=15-bit codes

        var original = sb.ToString();
        var encoded  = OracleEncode(original);

        var oldResult = OracleDecodeOld(encoded);
        var newResult = HuffmanDecoder.Decode(encoded);

        if (oldResult != newResult || oldResult != original)
        {
            mismatches++;
            if (mismatches <= 5)
                Console.WriteLine($"    mismatch: original=\"{original}\" old=\"{oldResult}\" new=\"{newResult}\"");
        }
    }

    Check(mismatches == 0, $"{rounds} printable-ASCII strings: old and new decoders agree, both match the original ({mismatches} mismatches)");
}

// --- 3. Padding edge cases ----------------------------------------------------
Console.WriteLine("[test] Padding edge cases");
{
    // Valid: single 5-bit code 'a' (00011, code=0x3) + 3 padding bits, all 1s -> 0x3F byte... let's build precisely.
    // 'a' = code 0x3, 5 bits: 00011. Padding needs 3 more bits, all 1: 111. Full byte: 00011111 = 0x1F.
    var validPad = new byte[] { 0x1F };
    var okDecoded = HuffmanDecoder.Decode(validPad);
    Check(okDecoded == "a", $"valid all-1s padding after 'a' decodes correctly (got \"{okDecoded}\")");

    // Corrupted: same but padding bits are 000 instead of 111: 00011000 = 0x18
    var badPad = new byte[] { 0x18 };
    var badPadThrew = false;
    try { HuffmanDecoder.Decode(badPad); }
    catch (HTTP2ConnectionException ex) when (ex.ErrorCode == HTTP2ErrorCode.COMPRESSION_ERROR) { badPadThrew = true; }
    Check(badPadThrew, "padding bits that are not all 1s throws COMPRESSION_ERROR (RFC 7541 Section 5.2)");

    // Over-length padding: 9 leftover bits (more than 7) — two bytes where only
    // a 5-bit code decodes in the first byte and nothing at all in the second.
    // 'a' (00011) then 11 more 1-bits => 00011111 11111111 = 0x1F 0xFF (11 leftover 1-bits, >7).
    var overLong = new byte[] { 0x1F, 0xFF };
    var overLongThrew = false;
    try { HuffmanDecoder.Decode(overLong); }
    catch (HTTP2ConnectionException ex) when (ex.ErrorCode == HTTP2ErrorCode.COMPRESSION_ERROR) { overLongThrew = true; }
    Check(overLongThrew, "more than 7 leftover padding bits throws COMPRESSION_ERROR");

    // Explicit EOS (30 bits of 1, i.e. 0x3fffffff) encoded mid-stream must be rejected.
    var eosBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // 32 ones >= the 30-bit EOS codeword
    var eosThrew = false;
    try { HuffmanDecoder.Decode(eosBytes); }
    catch (HTTP2ConnectionException ex) when (ex.ErrorCode == HTTP2ErrorCode.COMPRESSION_ERROR) { eosThrew = true; }
    Check(eosThrew, "explicit EOS codeword throws COMPRESSION_ERROR (must never be encoded)");

    // Empty input decodes to empty string without error.
    var empty = HuffmanDecoder.Decode(ReadOnlySpan<byte>.Empty);
    Check(empty == "", "empty input decodes to an empty string");
}

// --- 4. Rough perf comparison (informative, not a pass/fail gate) ------------
Console.WriteLine("[test] Rough perf comparison");
{
    var rnd = new Random(999);
    var samples = new List<byte[]>();
    const string charset = "abcdefghijklmnopqrstuvwxyz0123456789.-/: ";
    for (var i = 0; i < 500; i++)
    {
        var sb = new StringBuilder();
        for (var j = 0; j < 60; j++)
            sb.Append(charset[rnd.Next(charset.Length)]);
        samples.Add(OracleEncode(sb.ToString()));
    }

    var sw1 = System.Diagnostics.Stopwatch.StartNew();
    for (var iter = 0; iter < 50; iter++)
        foreach (var s in samples)
            OracleDecodeOld(s);
    sw1.Stop();

    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    for (var iter = 0; iter < 50; iter++)
        foreach (var s in samples)
            HuffmanDecoder.Decode(s);
    sw2.Stop();

    Console.WriteLine($"    old linear-scan: {sw1.ElapsedMilliseconds} ms");
    Console.WriteLine($"    new trie walk:   {sw2.ElapsedMilliseconds} ms");
    Console.WriteLine($"    speedup: {(double) sw1.ElapsedMilliseconds / Math.Max(1, sw2.ElapsedMilliseconds):F1}x");
}

Console.WriteLine(failures == 0 ? "\n[test] ALL PASS" : $"\n[test] {failures} FAILURE(S)");
Environment.Exit(failures == 0 ? 0 : 1);
