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

// h2interop — our CLIENT against foreign HTTP/2 servers.
//
// Unlike the other harnesses here, this one IS our stack: h2attack, h2connect and
// h2priority drive the opposite direction and deliberately borrow nothing from
// us. The reason this exists is an asymmetry worth naming: the server has been
// interop-tested against three independent peers (.NET HttpClient, curl/nghttp2,
// Grpc.Net.Client), while the client had exactly one — Kestrel, which shares our
// BCL and our .NET lineage. Every wire-visible decision the client makes (HPACK
// encoding choices, SETTINGS, flow control, GOAWAY handling, ALPN) had therefore
// only ever been judged by one implementation family.
//
// Each target gets a fresh connection with FULL certificate validation — chain
// and hostname, no bypass. The callback below observes rather than relaxes: it
// returns true only for SslPolicyErrors.None, which is exactly what passing no
// callback at all would do.
//
//   dotnet run --project tests/h2interop
//   dotnet run --project tests/h2interop -- nginx.org cloudflare.com   # subset
//
// WHAT COUNTS AS A PASS
//
// Any HTTP status. A 301, 302 or 403 is a regular HTTP response and proves the
// whole stack ran end to end: TLS + ALPN h2, the connection preface, their
// SETTINGS parsed, our HEADERS accepted, their HPACK-encoded response decoded,
// flow control, END_STREAM. Bot protection answering 403 says something about
// the target's opinion of datacentre IPs, not about our framing. What is NOT a
// pass is a timeout, a GOAWAY, a decode failure or a TLS/ALPN rejection.

using System.Diagnostics;
using System.Net.Security;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// The expected stack is a label, not an assertion — the `server:` column shows
// what the target actually said, so the matrix reports ground truth rather than
// this list's guess. Some of these front-end behind a CDN and will disagree.
var allTargets = new (String Host, String Expected)[] {
    ("www.nginx.com",          "nginx"),
    ("www.google.com",         "GFE"),
    ("www.cloudflare.com",     "Cloudflare"),
    ("www.haproxy.org",        "HAProxy"),
    ("www.litespeedtech.com",  "LiteSpeed"),
    ("caddyserver.com",        "Caddy (Go)"),
    ("www.apache.org",         "Apache httpd"),
    ("github.com",             "GitHub"),
};

var targets = args.Length > 0
                  ? allTargets.Where(t => args.Any(a => t.Host.Contains(a, StringComparison.OrdinalIgnoreCase))).ToArray()
                  : allTargets;

if (targets.Length == 0)
{
    Console.Error.WriteLine($"No target matched {String.Join(", ", args)}.");
    return 2;
}

var timeout = TimeSpan.FromSeconds(20);

Console.WriteLine("== HTTP/2 interop matrix — our client against foreign servers (full certificate validation) ==");
Console.WriteLine();
Console.WriteLine($"{"Target",-24} {"Expected",-14} {"server:",-20} {"MCS",5}  {"Alt-Svc",-12} {"ORIGIN",-6}  Result");
Console.WriteLine(new String('-', 110));

var reachable = 0;

foreach (var (host, expected) in targets)
{
    var r = await Probe(host, timeout);

    if (r.Ok)
        reachable++;

    Console.WriteLine($"{host,-24} {expected,-14} {Trim(r.Server, 20),-20} {r.MaxConcurrentStreams,5}  " +
                      $"{Trim(r.AltSvc, 12),-12} {r.Origin,-6}  {(r.Ok ? "✓ " : "✗ ")}{r.Result}");
}

Console.WriteLine(new String('-', 110));
Console.WriteLine();
Console.WriteLine($"{reachable}/{targets.Length} foreign servers reached (any HTTP status = the stack ran end to end; " +
                  "3xx/4xx are regular responses such as redirects or bot protection).");
Console.WriteLine("MCS = the peer's advertised MAX_CONCURRENT_STREAMS. Alt-Svc marked '*' arrived as an " +
                  "ALTSVC frame, unmarked as an alt-svc header field.");

// Non-zero only when every single target failed, which means the network is gone
// rather than the stack. A night where six of eight answered says something about
// those two hosts; turning that into a red run would train people to click past
// it. Tightening this needs a baseline of how many of these answer a hosted
// runner on a normal night — see the note in .github/workflows/nightly.yml.
return reachable > 0 ? 0 : 1;


// One attempt: fresh connection, GET /, full validation, everything read back.
static async Task<Probed> Probe(String Host, TimeSpan Timeout)
{

    using var cts = new CancellationTokenSource(Timeout);
    var       sw  = Stopwatch.StartNew();

    try
    {

        // Observing, not relaxing: true only for None is what the default (no
        // callback) does. Passing one lets us fail loudly rather than silently
        // accept a chain we would not accept in production.
        var connection = await HTTP2Client.ConnectAsync(
                                   Host,
                                   443,
                                   (_, _, _, sslPolicyErrors) => sslPolicyErrors == SslPolicyErrors.None,
                                   CancellationToken: cts.Token
                               );

        try
        {

            var response = await connection.SendRequestAsync(
                                     HTTPMethod.GET,
                                     URIScheme.https,
                                     Host,
                                     "/",
                                     CancellationToken: cts.Token
                                 );

            // Read AFTER the round trip, not right after connect: ALTSVC and
            // ORIGIN arrive as their own frames, and a server that sends them
            // does so around the first response rather than before the preface
            // is even answered.
            //
            // RFC 7838 allows either carrier, and the two are worth telling
            // apart: the ALTSVC *frame* is the HTTP/2-specific path our frame
            // dispatch has to handle, while the alt-svc *header field* is what
            // the deployed world actually uses to advertise h3. A frame-borne
            // value is marked with a trailing '*'.
            var frameAltSvc = connection.AlternativeServices
                                        .SelectMany(entry => entry.Value)
                                        .Select    (service => service.ProtocolId)
                                        .Distinct()
                                        .ToArray();

            var altSvc = frameAltSvc.Length > 0
                             ? String.Join(",", frameAltSvc) + "*"
                             : response.HeaderValue("alt-svc") is String header && header.Length > 0
                                   ? String.Join(",", HTTPAlternativeService.Parse(header).Select(s => s.ProtocolId).Distinct())
                                   : "—";

            return new Probed(
                       Ok:                   true,
                       Server:               response.HeaderValue("server") ?? "—",
                       MaxConcurrentStreams: connection.AvailableStreamSlots,
                       AltSvc:               altSvc,
                       Origin:               connection.OriginSet is not null ? "yes" : "—",
                       Result:               $"{response.Status}, {response.Body.Length} B, {sw.ElapsedMilliseconds} ms"
                   );

        }
        finally
        {
            // GOAWAY on the way out rather than dropping the socket: these are
            // other people's servers, and a harness that leaves half-open
            // connections behind on eight of them is a bad guest.
            try { await connection.CloseAsync(); } catch { /* closing is best-effort */ }
        }

    }
    catch (OperationCanceledException)
    {
        return Probed.Failed($"timeout after {Timeout.TotalSeconds:F0} s");
    }
    catch (Exception e)
    {
        // The inner message matters here: a TLS failure surfaces as a bare
        // "Authentication failed, see inner exception", and the inner one is
        // what distinguishes "the server refused our ALPN" from "the chain did
        // not validate" — a difference this harness exists to tell apart.
        var detail = e.InnerException is not null ? $" ({e.InnerException.Message})" : "";
        return Probed.Failed($"{e.GetType().Name}: {e.Message}{detail}");
    }

}

static String Trim(String Text, Int32 Max)
    => Text.Length <= Max ? Text : Text[..(Max - 1)] + "…";


record Probed(Boolean Ok,
              String  Server,
              Int32   MaxConcurrentStreams,
              String  AltSvc,
              String  Origin,
              String  Result)
{

    public static Probed Failed(String Reason)
        => new (false, "—", 0, "—", "—", Reason);

}
