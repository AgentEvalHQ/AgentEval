// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Indexing;

if (args.Length < 2) return 2;
var options = new EvidenceIndexOptions { DatabasePath = Path.GetFullPath(args[1]) };
var schema = new SqliteEvidenceIndexSchema();
var promotion = new SqliteEvidenceIndexRebuildPromotion();
var recovery = new SqliteEvidenceIndexRebuildRecovery();
try
{
    switch (args[0])
    {
        case "initialize":
            using (var writer = new SqliteEvidenceIndexWriter(options, schema))
            { writer.BindRoots([Root()]); writer.Upsert(Source("old")); }
            Print(schema.EnsureCurrent(options)); break;
        case "capture": Print(promotion.CaptureTarget(options)); break;
        case "prepare":
        case "hold":
            Print(new EvidenceIndexRebuildPreparation(schema).Prepare(options, (writer, token) =>
            {
                writer.BindRoots([Root()], token);
                writer.Upsert(Source("new"), token);
                if (args[0] == "hold")
                {
                    Console.WriteLine("READY_TO_KILL"); Console.Out.Flush();
                    Thread.Sleep(Timeout.Infinite); // The parent fixture kills this process after reading the marker.
                }
                return true;
            }));
            break;
        case "inspect": Print(recovery.Inspect(options, args[2])); break;
        case "discard": recovery.Discard(options, args[2]); Print(new { discarded = true }); break;
        case "rebind":
            using (var writer = new SqliteEvidenceIndexWriter(options, schema))
                writer.BindRoots([Root() with { Directory = args[2] }]);
            Print(new { rebound = true }); break;
        case "promote":
        case "promote-exit":
            var checkpoint = JsonSerializer.Deserialize<EvidenceIndexTargetCheckpoint>(File.ReadAllText(args[3]))!;
            var result = promotion.Promote(options, args[2], checkpoint);
            if (args[0] == "promote-exit") Environment.Exit(74); // No result reaches the caller after the commit.
            Print(result); break;
        case "read":
            var info = schema.EnsureCurrent(options);
            using (var writer = new SqliteEvidenceIndexWriter(options, schema))
                Print(new { info.InstanceId, info.Generation, old = writer.Find("root", "old") is not null, @new = writer.Find("root", "new") is not null });
            break;
        default: return 2;
    }
    return 0;
}
catch (EvidenceIndexException error) { Print(new { error.Code }); return 3; }

static EvidenceIndexSource Source(string path) => new("root", path, new string('a', 64), IndexedEvidenceKind.Legacy,
    IndexedVerification.Unverified, new(path, null, "agent:fixture", null, null, null, "PASS", "unknown"), [], [], null);
static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
EvidenceIndexRoot Root() => new("root", Path.GetDirectoryName(options.DatabasePath)!, IndexedEvidenceKind.Legacy);
