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

// h2bench — the first measurement of this stack.
//
// Every other claim in this repository has a number behind it: 166 unit tests,
// 48 harness runs, h2spec 146/146, Autobahn 517/517. Performance had none, which
// meant "readable rather than fast" was an assumption rather than a finding.
// This exists to turn it into a finding — and, before any optimisation is
// attempted, to say what the starting point actually is.
//
//   dotnet run -c Release --project tests/h2bench            # everything
//   dotnet run -c Release --project tests/h2bench -- hpack   # one scenario
//   dotnet run -c Release --project tests/h2bench -- --mib 256
//
// WHAT THESE NUMBERS ARE, AND ARE NOT
//
// Client and server run in the *same process* over loopback. That is deliberate
// — it makes the harness self-contained and the numbers reproducible — but it
// means every figure covers the whole round trip: our client, our server, TLS,
// and the loopback stack, sharing one machine's cores. They are for tracking
// this stack against itself over time. They are not a comparison with nginx, and
// anyone quoting them as one is quoting them wrong.
//
// Allocation is measured with GC.GetTotalAllocatedBytes, which is process-wide,
// so "bytes per request" likewise covers both roles. That is the honest figure
// for "what does one request cost this stack", and the one worth watching.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using org.GraphDefined.Vanaheimr.Hermod.HTTP2;


#region Arguments

// Options take a value, so a bare word is only a scenario name if it is not
// the value of the option before it — otherwise "--mib 64" quietly registers
// "64" as the one scenario to run, and nothing matches it.
var valued    = new HashSet<String>(StringComparer.Ordinal) { "--mib", "--requests" };
var scenarios = new List<String>();
var bodyMiB   = 64;
var requests  = 20_000;

for (var i = 0; i < args.Length; i++)
{

    if (valued.Contains(args[i]) && i + 1 < args.Length && Int32.TryParse(args[i + 1], out var value))
    {

        if (args[i] == "--mib")      bodyMiB  = value;
        if (args[i] == "--requests") requests = value;

        i++;   // consume the value
        continue;

    }

    if (!args[i].StartsWith('-'))
        scenarios.Add(args[i]);

}

// Run the networked scenarios over cleartext h2c instead of TLS. Not a
// deployment mode worth benchmarking for its own sake — it is a *bisection*:
// if the numbers move, the cost is in SslStream; if they do not, it is in our
// framing and task plumbing.
var cleartext = args.Contains("--cleartext");

Boolean Wanted(String name)
    => scenarios.Count == 0 || scenarios.Contains(name, StringComparer.OrdinalIgnoreCase);

#endregion

Console.WriteLine();
Console.WriteLine("h2bench — hand-rolled HTTP/2 stack");
Console.WriteLine($"  {Environment.OSVersion}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
Console.WriteLine($"  server GC: {System.Runtime.GCSettings.IsServerGC}");

#if DEBUG
Console.WriteLine();
Console.WriteLine("  !! Built in DEBUG. These numbers measure the debugger, not the stack.");
Console.WriteLine("  !! Re-run with:  dotnet run -c Release --project tests/h2bench");
#endif

Console.WriteLine();
Console.WriteLine("  Client and server share this process; figures cover the whole round trip");
Console.WriteLine("  (both roles + TLS + loopback) and are for tracking change over time.");
Console.WriteLine();


#region In-memory scenarios (no sockets — the clearest view of allocation)

if (Wanted("frames"))
{

    var payload = new Byte[1024];
    Random.Shared.NextBytes(payload);

    var frame  = HTTP2Frame.CreateData(1, payload, EndStream: false);
    var header = new Byte[9];

    Report("frames: serialize + parse header", 200_000, iterations =>
    {
        for (var i = 0; i < iterations; i++)
        {
            var bytes = frame.Serialize();
            Array.Copy(bytes, header, 9);
            _ = HTTP2Frame.ParseHeader(header);
        }
    }, unit: "frames");

}

if (Wanted("hpack"))
{

    var headers = new List<(String Name, String Value)> {
        (":method",          "GET"),
        (":scheme",          "https"),
        (":authority",       "www.example.com"),
        (":path",            "/api/v3/resources/1234?expand=all&fields=id,name,created"),
        ("user-agent",       "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"),
        ("accept",           "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
        ("accept-language",  "en-GB,en;q=0.9,de;q=0.8"),
        ("accept-encoding",  "gzip, deflate, br"),
        ("cookie",           "session=8f14e45fceea167a5a36dedd4bea2543; consent=1; theme=dark"),
        ("referer",          "https://www.example.com/previous/page")
    };

    // The compressed sizes are measured with a throwaway encoder. HPACK is
    // stateful: every encode mutates the dynamic table, and the decoder's table
    // only stays in step if it sees *every* block the encoder produced. Encoding
    // one extra block here for a size report would desynchronise the pair below
    // — which the decoder correctly rejects as an out-of-range table index.
    var probe      = new HPACKEncoder();
    var firstBlock = probe.EncodeHeaderBlock(headers);
    var laterBlock = probe.EncodeHeaderBlock(headers);

    Console.WriteLine($"  (a 10-field request block: {firstBlock.Length} bytes on first use, " +
                      $"{laterBlock.Length} once the dynamic table is warm)");
    Console.WriteLine();

    // A fresh pair per iteration would measure table construction; reusing one
    // pair is what a real connection does — and keeps encoder and decoder in the
    // lockstep the protocol requires.
    var encoder = new HPACKEncoder();
    var decoder = new HPACKDecoder();

    Report("hpack: encode + decode a request block", 100_000, iterations =>
    {
        for (var i = 0; i < iterations; i++)
        {
            var block = encoder.EncodeHeaderBlock(headers);
            _ = decoder.DecodeHeaderBlock(block);
        }
    }, unit: "blocks");

}

#endregion


#region Networked scenarios

if (Wanted("requests") || Wanted("throughput") || Wanted("upload") || Wanted("probe"))
{

    var small = "hello, world!"u8.ToArray();
    var large = new Byte[bodyMiB * 1024 * 1024];
    Random.Shared.NextBytes(large);

    using var certificate = cleartext ? null : SelfSignedCertificate();

    Console.WriteLine($"  transport: {(cleartext ? "h2c (cleartext — TLS bypassed)" : "h2 over TLS")}");
    Console.WriteLine();

    var port   = FreePort();
    var server = new HTTP2Server(
                     IPAddress.Loopback,
                     port,
                     certificate,
                     (streamId, headers, body, ct) =>
                     {
                         var path = headers.First(h => h.Name == ":path").Value;
                         return Task.FromResult<(List<(String, String)>, Byte[]?)>(path switch
                         {
                             "/large" => ([(":status", "200"), ("content-length", large.Length.ToString())], large),
                             "/sink"  => ([(":status", "200"), ("content-length", "2")],                     "ok"u8.ToArray()),
                             _        => ([(":status", "200"), ("content-length", small.Length.ToString())], small)
                         });
                     },
                     Cleartext:          cleartext,
                     MaxRequestBodySize: (Int64) (bodyMiB + 16) * 1024 * 1024);

    var serverTask = server.RunAsync();
    await WaitUntilListening(port);

    var authority = $"localhost:{port}";
    var scheme    = cleartext ? "http" : "https";
    var conn      = await HTTP2Client.ConnectAsync("localhost", port, (_, _, _, _) => true, Cleartext: cleartext);

    if (Wanted("requests"))
        foreach (var concurrency in new[] { 1, 8, 64 })
            await ReportRequests(conn, scheme, authority, concurrency, requests);

    // Bisecting the ~1 ms per-request floor (see docs/BUILD_LOG.md). Two splits,
    // both of which need no instrumentation inside the library:
    //
    //   * StartRequestAsync returns once our HEADERS are on the wire, so timing it
    //     separately from awaiting the response splits "our send path" from
    //     "server turnaround + our receive path".
    //   * Driving the same server with .NET's HttpClient replaces our client
    //     entirely. If that is fast, the cost is ours on the client side; if it is
    //     equally slow, the cost is in the server or the environment.
    if (Wanted("probe"))
    {

        const Int32 probes = 2_000;

        var toWire   = new Int64[probes];
        var toAnswer = new Int64[probes];

        for (var i = 0; i < probes; i++)
        {
            var t0     = Stopwatch.GetTimestamp();
            var handle = await conn.StartRequestAsync("GET", scheme, authority, "/");
            var t1     = Stopwatch.GetTimestamp();
            _          = await handle.Response;
            var t2     = Stopwatch.GetTimestamp();

            toWire[i]   = t1 - t0;
            toAnswer[i] = t2 - t1;
        }

        Percentiles("probe: our client, request onto the wire  (1 concurrent)", toWire);
        Percentiles("probe: our client, waiting for the response (1 concurrent)", toAnswer);

        // The same split under load. StartRequestAsync holds requestStartLock
        // across stream allocation *and* the HEADERS write, so if that serialised
        // section is what caps a connection's throughput, "onto the wire" is where
        // the time will pile up as concurrency rises — while "waiting for the
        // response" stays flat.
        var wireLoaded   = new Int64[probes];
        var answerLoaded = new Int64[probes];
        var slot         = -1;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(worker => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref slot)) < probes)
            {
                var t0       = Stopwatch.GetTimestamp();
                var handle   = await conn.StartRequestAsync("GET", scheme, authority, "/");
                var t1       = Stopwatch.GetTimestamp();
                var response = await handle.Response;
                var t2       = Stopwatch.GetTimestamp();

                wireLoaded[i]   = t1 - t0;
                answerLoaded[i] = t2 - t1;
            }
        })));

        Percentiles("probe: our client, request onto the wire  (64 concurrent)", wireLoaded);
        Percentiles("probe: our client, waiting for the response (64 concurrent)", answerLoaded);

        if (!cleartext)
        {

            using var handler = new SocketsHttpHandler {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };

            using var http = new HttpClient(handler) {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact
            };

            var url  = $"https://localhost:{port}/";
            var dotnet = new Int64[probes];

            _ = await http.GetAsync(url);   // warm the connection + TLS

            for (var i = 0; i < probes; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                using var response = await http.GetAsync(url);
                dotnet[i] = Stopwatch.GetTimestamp() - t0;
            }

            Percentiles("probe: .NET HttpClient -> our server", dotnet);

            // The control. Same client, same machine, same loopback, same TLS —
            // only the server differs. Without it, "0.6 ms" has no scale: it could
            // be our server being slow or it could be what a loopback HTTP/2 round
            // trip simply costs here.
            var kestrelPort = FreePort();
            var builder     = WebApplication.CreateBuilder();

            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenLocalhost(kestrelPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    listen.UseHttps(SelfSignedCertificate());
                }));

            var kestrel = builder.Build();
            kestrel.MapGet("/", () => Results.Text("hello, world!"));
            await kestrel.StartAsync();

            var kestrelUrl     = $"https://localhost:{kestrelPort}/";
            var kestrelSamples = new Int64[probes];

            _ = await http.GetAsync(kestrelUrl);

            for (var i = 0; i < probes; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                using var response = await http.GetAsync(kestrelUrl);
                kestrelSamples[i] = Stopwatch.GetTimestamp() - t0;
            }

            Percentiles("probe: .NET HttpClient -> Kestrel (control)", kestrelSamples);

            await kestrel.StopAsync();

        }

    }

    if (Wanted("throughput"))
    {
        // Large GETs: the flow-control and DATA-writer path, where per-frame
        // allocation shows up as GC pressure. Three transfers rather than one —
        // a single sample of a ~1 s operation is a number, not a measurement.
        const Int32 transfers = 3;

        Report($"throughput: GET {bodyMiB} MiB", transfers, iterations =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var response = conn.SendRequestAsync("GET", scheme, authority, "/large").GetAwaiter().GetResult();
                if (response.Body.Length != large.Length)
                    throw new Exception($"short read: {response.Body.Length} of {large.Length}");
            }
        }, unit: "transfers", bytes: (Int64) large.Length * transfers);
    }

    if (Wanted("upload"))
    {
        const Int32 uploads = 3;

        Report($"upload: POST {bodyMiB} MiB", uploads, iterations =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var response = conn.SendRequestAsync("POST", scheme, authority, "/sink", Body: large).GetAwaiter().GetResult();
                if (response.Status != 200)
                    throw new Exception($"status {response.Status}");
            }
        }, unit: "transfers", bytes: (Int64) large.Length * uploads);
    }

    await conn.CloseAsync();
    await server.StopAsync();
    try { await serverTask; } catch { }

}

Console.WriteLine();

#endregion


#region Measurement

/// <summary>
/// Time a body, reporting throughput, per-operation allocation and the GC
/// collections it provoked. A warmup pass runs first so JIT and first-use costs
/// are not attributed to the steady state.
/// </summary>
void Report(String Name, Int32 Iterations, Action<Int32> Body, String unit, Int64 bytes = 0)
{

    Body(Math.Max(1, Iterations / 20));          // warm up: JIT, tables, TLS session

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var gen0    = GC.CollectionCount(0);
    var gen1    = GC.CollectionCount(1);
    var gen2    = GC.CollectionCount(2);
    var before  = GC.GetTotalAllocatedBytes(precise: true);
    var watch   = Stopwatch.StartNew();

    Body(Iterations);

    watch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

    var perSecond = Iterations / watch.Elapsed.TotalSeconds;

    Console.WriteLine($"  {Name}");
    Console.WriteLine($"      {perSecond,12:N0} {unit}/s   ({watch.Elapsed.TotalMilliseconds:N1} ms for {Iterations:N0})");

    if (bytes > 0)
        Console.WriteLine($"      {bytes / 1024.0 / 1024.0 / watch.Elapsed.TotalSeconds,12:N1} MiB/s");

    Console.WriteLine($"      {allocated / (Double) Iterations,12:N0} bytes/{unit.TrimEnd('s')}   " +
                      $"({allocated / 1024.0 / 1024.0:N1} MiB total)");
    Console.WriteLine($"      {"GC",12}   gen0 {GC.CollectionCount(0) - gen0}, " +
                      $"gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}");
    Console.WriteLine();

}

/// <summary>
/// Report the distribution of a set of Stopwatch tick samples. A mean would hide
/// exactly what matters when hunting a fixed per-operation cost: whether the
/// whole distribution sits at the floor, or only its tail does.
/// </summary>
void Percentiles(String Name, Int64[] Samples)
{

    var sorted = (Int64[]) Samples.Clone();
    Array.Sort(sorted);

    Double At(Double p)
        => sorted[(Int32) (p * (sorted.Length - 1))] * 1000.0 / Stopwatch.Frequency;

    Console.WriteLine($"  {Name}");
    Console.WriteLine($"      ms   p50 {At(0.50):N3}, p90 {At(0.90):N3}, p99 {At(0.99):N3}, " +
                      $"min {At(0.00):N3}, max {At(1.00):N3}");
    Console.WriteLine();

}

/// <summary>
/// Drive Concurrency requests in flight at once until the budget is exhausted,
/// reporting throughput plus the latency distribution — a mean alone hides
/// exactly the tail behaviour multiplexing is supposed to protect.
/// </summary>
async Task ReportRequests(HTTP2ClientConnection Connection, String Scheme, String Authority, Int32 Concurrency, Int32 Total)
{

    var latencies = new Int64[Total];
    var next      = -1;

    async Task Worker()
    {
        int index;
        while ((index = Interlocked.Increment(ref next)) < Total)
        {
            var started = Stopwatch.GetTimestamp();
            _ = await Connection.SendRequestAsync("GET", Scheme, Authority, "/");
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }
    }

    // Warmup, discarded.
    next = Total - Math.Min(Total, 2_000) - 1;
    await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => Worker()));

    next = -1;
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var gen0   = GC.CollectionCount(0);
    var gen1   = GC.CollectionCount(1);
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch  = Stopwatch.StartNew();

    await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => Worker()));

    watch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

    Array.Sort(latencies);

    Double Percentile(Double p)
        => latencies[(Int32) (p * (latencies.Length - 1))] * 1000.0 / Stopwatch.Frequency;

    Console.WriteLine($"  requests: GET, {Concurrency} concurrent");
    Console.WriteLine($"      {Total / watch.Elapsed.TotalSeconds,12:N0} req/s   ({watch.Elapsed.TotalMilliseconds:N1} ms for {Total:N0})");
    Console.WriteLine($"      {"latency ms",12}   p50 {Percentile(0.50):N3}, p90 {Percentile(0.90):N3}, " +
                      $"p99 {Percentile(0.99):N3}, max {Percentile(1.00):N3}");
    Console.WriteLine($"      {allocated / (Double) Total,12:N0} bytes/request   ({allocated / 1024.0 / 1024.0:N1} MiB total)");
    Console.WriteLine($"      {"GC",12}   gen0 {GC.CollectionCount(0) - gen0}, gen1 {GC.CollectionCount(1) - gen1}");
    Console.WriteLine();

}

#endregion


#region Plumbing

static Int32 FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint) listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitUntilListening(Int32 Port)
{
    for (var i = 0; i < 100; i++)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, Port);
            return;
        }
        catch
        {
            await Task.Delay(50);
        }
    }
    throw new TimeoutException($"nothing listening on 127.0.0.1:{Port}");
}

static X509Certificate2 SelfSignedCertificate()
{

    using var rsa = RSA.Create(2048);

    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(san.Build());

    var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

    return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

}

#endregion
