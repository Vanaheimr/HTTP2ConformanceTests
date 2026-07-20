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

using System.Reflection;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Unit-level verification of HTTP2StreamManager.PruneClosedStreams, run in
// isolation (no network) since it's pure in-memory state.

var failures = 0;

void Check(bool condition, string description)
{
    if (condition)
        Console.WriteLine($"  ✓ {description}");
    else
    {
        Console.WriteLine($"  ✗ FAIL: {description}");
        failures++;
    }
}

int DictCount(HTTP2StreamManager mgr)
{
    var field = typeof(HTTP2StreamManager).GetField("streams", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var dict  = (System.Collections.IDictionary) field.GetValue(mgr)!;
    return dict.Count;
}

Console.WriteLine("[test] Basic prune: closed streams removed, open streams kept");
{
    var mgr = new HTTP2StreamManager();

    // 1, 3, 5 will be fully closed (simulating completed request/response);
    // 7, 9 stay in-flight (Open).
    foreach (var id in new UInt32[] { 1, 3, 5, 7, 9 })
        mgr.GetOrCreateStream(id).Open();

    mgr.TryGetStream(1)!.CloseRemote(); mgr.TryGetStream(1)!.CloseLocal();
    mgr.TryGetStream(3)!.CloseRemote(); mgr.TryGetStream(3)!.CloseLocal();
    mgr.TryGetStream(5)!.Reset();

    Check(DictCount(mgr) == 5, "dictionary has all 5 streams before pruning");

    mgr.PruneClosedStreams();

    Check(DictCount(mgr) == 2, $"dictionary shrank to 2 after pruning (was {DictCount(mgr)})");
    Check(mgr.TryGetStream(1) is null, "stream 1 (closed) removed");
    Check(mgr.TryGetStream(3) is null, "stream 3 (closed) removed");
    Check(mgr.TryGetStream(5) is null, "stream 5 (reset) removed");
    Check(mgr.TryGetStream(7) is not null, "stream 7 (open) kept");
    Check(mgr.TryGetStream(9) is not null, "stream 9 (open) kept");
}

Console.WriteLine("[test] LastPeerStreamId ordering unaffected by pruning");
{
    var mgr = new HTTP2StreamManager();

    mgr.GetOrCreateStream(1).Open();
    mgr.TryGetStream(1)!.CloseRemote(); mgr.TryGetStream(1)!.CloseLocal();
    mgr.PruneClosedStreams();

    Check(mgr.TryGetStream(1) is null, "stream 1 pruned");
    Check(mgr.LastPeerStreamId == 1, "LastPeerStreamId still reflects the highest stream ever opened");

    // A client trying to reuse stream 1 after it was pruned must still be
    // rejected — pruning must not resurrect a "closed" stream ID as if it
    // were fresh/idle.
    var threw = false;
    try { mgr.GetOrCreateStream(1); }
    catch (HTTP2ConnectionException) { threw = true; }
    Check(threw, "re-using stream ID 1 after pruning is still rejected (ID <= LastPeerStreamId)");

    // A genuinely new, higher stream ID still works fine.
    var s3 = mgr.GetOrCreateStream(3);
    Check(s3.StreamId == 3 && mgr.LastPeerStreamId == 3, "opening a new higher stream ID still works after pruning");
}

Console.WriteLine("[test] AdjustAllStreamWindows only touches remaining (non-pruned) streams");
{
    var mgr = new HTTP2StreamManager();

    mgr.GetOrCreateStream(1).Open();
    mgr.TryGetStream(1)!.CloseRemote(); mgr.TryGetStream(1)!.CloseLocal();
    var s3 = mgr.GetOrCreateStream(3);
    s3.Open();

    mgr.PruneClosedStreams();

    var before = s3.SendWindow;
    mgr.AdjustAllStreamWindows(1000);
    Check(s3.SendWindow == before + 1000, "remaining open stream's window was adjusted");
    Check(DictCount(mgr) == 1, "pruned stream did not resurface / dictionary still has only 1 entry");
}

Console.WriteLine(failures == 0 ? "\n[test] ALL PASS" : $"\n[test] {failures} FAILURE(S)");
Environment.Exit(failures == 0 ? 0 : 1);
