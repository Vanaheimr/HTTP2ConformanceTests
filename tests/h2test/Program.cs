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
using System.Text;

// Suspect: SocketsHttpHandler's HTTP/2 dynamic receive window sizing (RTT pings)
AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2FlowControl.DisableDynamicWindowSizing", true);

var handler = new SocketsHttpHandler {
    SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
        RemoteCertificateValidationCallback = (_, _, _, _) => true
    }
};

using var client = new HttpClient(handler) {
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact,
    Timeout               = TimeSpan.FromSeconds(8)
};

var path = args.Length > 0 ? args[0] : "/";
var body = args.Length > 1 ? args[1] : null;

// Multiplexing test: /slow (2 s) and / concurrently on ONE connection.
// SocketsHttpHandler multiplexes HTTP/2 requests to the same origin on a
// single connection by default (EnableMultipleHttp2Connections = false).
if (path == "multi")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    async Task<string> Fetch(string p)
    {
        var started = sw.ElapsedMilliseconds;
        var r = await client.GetAsync($"https://localhost:8443{p}", HttpCompletionOption.ResponseHeadersRead);
        var headersAt = sw.ElapsedMilliseconds;
        var c = await r.Content.ReadAsByteArrayAsync();
        return $"{p,-6} -> {(int) r.StatusCode} ({c.Length,6} bytes)  sent={started,4} ms  headers={headersAt,5} ms  done={sw.ElapsedMilliseconds,5} ms";
    }

    var mode = args.Length > 1 ? args[1] : "default";

    if (mode == "seq")
    {
        // Sequential requests on one connection
        Console.WriteLine(await Fetch("/"));
        Console.WriteLine(await Fetch("/large"));
        Console.WriteLine(await Fetch("/slow"));
        return;
    }

    if (mode == "largestorm")
    {
        // Four concurrent 128 KiB responses — stresses the shared
        // connection-level send window between competing streams.
        var tasks = Enumerable.Range(0, 4).Select(_ => Fetch("/large")).ToArray();
        foreach (var t in tasks)
            Console.WriteLine(await t);
        return;
    }

    if (mode == "fast3")
    {
        // Three concurrent fast requests, no /slow involved
        var a = Fetch("/");
        var b = Fetch("/large");
        var c = Fetch("/echo");
        Console.WriteLine(await a);
        Console.WriteLine(await b);
        Console.WriteLine(await c);
        return;
    }

    if (mode == "many")
    {
        // Many sequential requests on one connection — exercises stream-dictionary
        // pruning (each new HEADERS sweeps out already-closed streams).
        var count = args.Length > 2 ? int.Parse(args[2]) : 200;
        var ok = 0;

        for (var i = 0; i < count; i++)
        {
            var r = await client.GetAsync($"https://localhost:8443/");
            if (r.StatusCode == System.Net.HttpStatusCode.OK)
                ok++;
            else
                Console.WriteLine($"  request {i} -> {(int) r.StatusCode}");
        }

        Console.WriteLine($"{ok}/{count} requests returned 200 on one connection");
        return;
    }

    var slow  = Fetch("/slow");
    var fast  = Fetch("/");
    var large = mode == "nolarge" ? null : Fetch("/large");

    Console.WriteLine(await fast);
    if (large is not null)
        Console.WriteLine(await large);
    Console.WriteLine(await slow);
    return;
}

try
{
    HttpResponseMessage resp;

    if (body is not null)
        resp = await client.PostAsync($"https://localhost:8443{path}", new StringContent(body, Encoding.UTF8));
    else
        resp = await client.GetAsync($"https://localhost:8443{path}");

    var content = await resp.Content.ReadAsByteArrayAsync();

    Console.WriteLine($"HTTP {resp.Version} {(int) resp.StatusCode} {resp.StatusCode}");
    Console.WriteLine($"Body: {content.Length} bytes");

    if (content.Length <= 2048)
        Console.WriteLine(Encoding.UTF8.GetString(content));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    Environment.Exit(1);
}
