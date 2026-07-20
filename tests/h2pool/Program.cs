/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// HTTP2ClientPool tests: a single-origin pool that keeps N warm connections,
// spreads requests across the least-loaded one, fails over a not-processed
// request to another connection, and self-heals when a connection dies (GOAWAY)
// by reconnecting in the background — all transparent to the caller.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

static X509Certificate2 MakeCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(san.Build());
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);
}

var cert = MakeCert();
RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;
var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

// Poll a condition up to a timeout — used to wait for the pool to warm up / heal.
static async Task<bool> Eventually(Func<bool> cond, int timeoutMs = 3000)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (cond()) return true;
        await Task.Delay(25);
    }
    return cond();
}

// ---- raw frame I/O + a MULTI-connection mock origin ------------------------

static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
{
    var off = 0;
    while (off < buf.Length)
    {
        var n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
        if (n == 0) return false;
        off += n;
    }
    return true;
}

static async Task<HTTP2Frame?> ReadFrame(Stream s, CancellationToken ct)
{
    var header = new byte[9];
    if (!await ReadExact(s, header, ct)) return null;
    var f = HTTP2Frame.ParseHeader(header);
    if (f.Length > 0)
    {
        f.Payload = new byte[f.Length];
        if (!await ReadExact(s, f.Payload, ct)) return null;
    }
    return f;
}

static async Task WriteFrame(Stream s, HTTP2Frame f)
{
    await s.WriteAsync(f.Serialize());
    await s.FlushAsync();
}

static async Task Respond200(SslStream ssl, HPACKEncoder enc, uint streamId)
{
    var block = enc.EncodeHeaderBlock([(":status", "200"), ("content-length", "2")]);
    await WriteFrame(ssl, HTTP2Frame.CreateHeaders(streamId, block, EndStream: false, EndHeaders: true));
    await WriteFrame(ssl, HTTP2Frame.CreateData(streamId, Encoding.ASCII.GetBytes("ok"), EndStream: true));
}

// Accepts MANY connections; each gets a 0-based index handed to onFrame, so the
// mock can behave differently per connection (e.g. GOAWAY the first, serve the rest).
TcpListener StartMultiMock(int mcs, Func<int, SslStream, HTTP2Frame, HPACKEncoder, Task> onFrame)
{
    var listener  = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var connIndex = -1;

    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { break; }

            var idx = Interlocked.Increment(ref connIndex);
            _ = Task.Run(async () =>
            {
                try
                {
                    using (tcp)
                    {
                        var ssl = new SslStream(tcp.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                            ServerCertificate = cert, ApplicationProtocols = [new SslApplicationProtocol("h2")] });

                        var magic = new byte[preface.Length];
                        if (!await ReadExact(ssl, magic, CancellationToken.None)) return;

                        await WriteFrame(ssl, mcs > 0
                            ? HTTP2Frame.CreateSettings((HTTP2SettingsParameter.MAX_CONCURRENT_STREAMS, (uint) mcs))
                            : HTTP2Frame.CreateSettings());

                        var enc = new HPACKEncoder();
                        while (true)
                        {
                            var f = await ReadFrame(ssl, CancellationToken.None);
                            if (f is null) break;
                            if (f.Type == HTTP2FrameType.SETTINGS && !f.IsAck)
                                await WriteFrame(ssl, HTTP2Frame.CreateSettingsAck());
                            await onFrame(idx, ssl, f, enc);
                        }
                    }
                }
                catch { /* connection ended */ }
            });
        }
    });

    return listener;
}

static int Port(TcpListener l) => ((IPEndPoint) l.LocalEndpoint).Port;

// =========================================================================
// 1. Warm pool + happy path (against our real HTTP2Server)
// =========================================================================
Console.WriteLine("=== warm pool + happy path ===");
{
    const int port = 9470;
    Task<(List<(string, string)>, byte[]?)> Handle(uint s, List<(string Name, string Value)> h, byte[]? b, CancellationToken ct)
        => Task.FromResult<(List<(string, string)>, byte[]?)>(
               ([(":status", "200"), ("content-length", "2")], Encoding.ASCII.GetBytes("ok")));

    var server = new OurServer(IPAddress.Loopback, port, cert, Handle);
    var serverTask = server.RunAsync();
    await Task.Delay(300);

    await using var pool = await HTTP2ClientPool.ConnectAsync("127.0.0.1", port, acceptAny, MaxConnections: 4);

    Check("pool warms up to 4 connections", await Eventually(() => pool.ConnectionCount == 4), $"count={pool.ConnectionCount}");

    // 40 concurrent requests, all served.
    var tasks = Enumerable.Range(0, 40).Select(_ =>
        pool.SendRequestAsync("GET", "https", $"127.0.0.1:{port}", "/")).ToArray();
    var results = await Task.WhenAll(tasks);
    Check("40 concurrent requests all 200", results.All(r => r.Status == 200));
    Check("still 4 connections after the burst", pool.ConnectionCount == 4, $"count={pool.ConnectionCount}");

    await server.StopAsync();
    try { await serverTask; } catch { }
}

// =========================================================================
// 2. Load spreading: requests fan out across the pool's connections
// =========================================================================
Console.WriteLine("\n=== load spreading across connections ===");
{
    var usedConns = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

    // MCS=2 per connection; hold each stream briefly so several are in flight at once.
    var listener = StartMultiMock(2, async (idx, ssl, f, enc) =>
    {
        if (f.Type != HTTP2FrameType.HEADERS) return;
        usedConns[idx] = 1;
        await Task.Delay(300);
        await Respond200(ssl, enc, f.StreamId);
    });

    await using var pool = await HTTP2ClientPool.ConnectAsync("127.0.0.1", Port(listener), acceptAny, MaxConnections: 4);
    await Eventually(() => pool.ConnectionCount == 4);

    var tasks = Enumerable.Range(0, 8).Select(_ =>
        pool.SendRequestAsync("GET", "https", "127.0.0.1", "/")).ToArray();
    var results = await Task.WhenAll(tasks);

    Check("8 concurrent requests all 200", results.All(r => r.Status == 200));
    Check("requests spread across >= 2 connections", usedConns.Count >= 2, $"used {usedConns.Count} connections");

    listener.Stop();
}

// =========================================================================
// 3. Self-healing: a connection that dies (GOAWAY) is replaced; caller unaffected
// =========================================================================
Console.WriteLine("\n=== self-healing (dead connection replaced) ===");
{
    // Every connection serves exactly one request, then GOAWAYs itself
    // (lastStreamId = that stream, NO_ERROR) — draining and dying. The pool must
    // keep serving from the survivor while reconnecting the dead one.
    var listener = StartMultiMock(0, async (idx, ssl, f, enc) =>
    {
        if (f.Type != HTTP2FrameType.HEADERS) return;
        await Respond200(ssl, enc, f.StreamId);
        await WriteFrame(ssl, HTTP2Frame.CreateGoAway(f.StreamId, HTTP2ErrorCode.NO_ERROR, "bye"));
    });

    await using var pool = await HTTP2ClientPool.ConnectAsync("127.0.0.1", Port(listener), acceptAny, MaxConnections: 2);
    await Eventually(() => pool.ConnectionCount == 2);

    // 6 sequential requests — each kills the connection it used; the caller still
    // gets 200 every time because the pool routes to a live one and heals in bg.
    var allOk = true;
    for (var i = 0; i < 6; i++)
    {
        var r = await pool.SendRequestAsync("GET", "https", "127.0.0.1", "/");
        if (r.Status != 200) allOk = false;
    }
    Check("6 sequential requests all 200 despite connections dying", allOk);
    Check("dead connections were reconnected in the background", await Eventually(() => pool.Reconnects >= 3), $"reconnects={pool.Reconnects}");
    Check("pool recovers to full strength (2 connections)", await Eventually(() => pool.ConnectionCount == 2), $"count={pool.ConnectionCount}");

    listener.Stop();
}

// =========================================================================
// 4. Failover: a not-processed request is retried on another connection
// =========================================================================
Console.WriteLine("\n=== failover on a not-processed request ===");
{
    // The FIRST connection GOAWAYs with lastStreamId=0 (the request was NOT
    // processed); every later connection serves 200. With MaxConnections=1 the
    // pool must reconnect and retry the request there.
    var listener = StartMultiMock(0, async (idx, ssl, f, enc) =>
    {
        if (f.Type != HTTP2FrameType.HEADERS) return;
        if (idx == 0)
            await WriteFrame(ssl, HTTP2Frame.CreateGoAway(0, HTTP2ErrorCode.NO_ERROR, "not processed"));
        else
            await Respond200(ssl, enc, f.StreamId);
    });

    await using var pool = await HTTP2ClientPool.ConnectAsync("127.0.0.1", Port(listener), acceptAny, MaxConnections: 1);

    var resp = await pool.SendRequestAsync("GET", "https", "127.0.0.1", "/");
    Check("request succeeds after failing over to a fresh connection", resp.Status == 200, $"status {resp.Status}");
    Check("a failover was recorded", pool.Failovers >= 1, $"failovers={pool.Failovers}");
    Check("the dead connection was reconnected", pool.Reconnects >= 1, $"reconnects={pool.Reconnects}");

    listener.Stop();
}

// =========================================================================
// 5. Disposal: a disposed pool refuses further requests
// =========================================================================
Console.WriteLine("\n=== disposal ===");
{
    const int port = 9470;
    Task<(List<(string, string)>, byte[]?)> Handle(uint s, List<(string Name, string Value)> h, byte[]? b, CancellationToken ct)
        => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "200"), ("content-length", "2")], Encoding.ASCII.GetBytes("ok")));

    var server = new OurServer(IPAddress.Loopback, port, cert, Handle);
    var serverTask = server.RunAsync();
    await Task.Delay(300);

    var pool = await HTTP2ClientPool.ConnectAsync("127.0.0.1", port, acceptAny, MaxConnections: 2);
    await pool.DisposeAsync();

    var threw = false;
    try { await pool.SendRequestAsync("GET", "https", $"127.0.0.1:{port}", "/"); }
    catch (ObjectDisposedException) { threw = true; }
    Check("disposed pool throws ObjectDisposedException", threw);

    await server.StopAsync();
    try { await serverTask; } catch { }
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
