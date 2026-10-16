"""Audit retained bulk benchmark runs and recompute process-level comparisons (stdlib only)."""
import argparse
import hashlib
import json
import math
import statistics
import zipfile
from pathlib import Path

FILES = ["runner.json", "memory-samples.json", "stdout.log", "stderr.log",
         *["data/" + name + ".json" for name in
           ("environment", "bulk-completion", "raw-samples", "protocol-samples", "commands", "summary")]]
SIZES = (1, 100, 1000)
OPERATIONS = ("update", "delete")
BACKENDS = ("tracked", "bulk", "driver-batch")
CASES = {f"bulk/batch-{size}/{operation}/{backend}": (size, operation, backend)
         for size in SIZES for operation in OPERATIONS for backend in BACKENDS}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def audit(archive):
    runs = []
    hashes = {}
    with zipfile.ZipFile(archive) as source:
        expected_files = {f"run-{i}/{name}" for i in range(1, 4) for name in FILES}
        require(set(source.namelist()) == expected_files and len(source.namelist()) == len(expected_files), "Archive inventory differs")
        for i in range(1, 4):
            prefix = f"run-{i}/"
            content = {name: source.read(prefix + name) for name in FILES}
            hashes.update({prefix + name: hashlib.sha256(data).hexdigest() for name, data in content.items()})
            data = {name: json.loads(raw.decode("utf-8-sig")) for name, raw in content.items() if name.endswith(".json")}
            runner, environment = data["runner.json"], data["data/environment.json"]
            completion = data["data/bulk-completion.json"]
            require(runner["Outcome"] == "completed-bulk-node-experiment" and runner["Bulk"] and not runner["Tracing"], "Runner did not complete")
            require(all(completion.get(k) is True for k in ("Completed", "Cleaned", "ShapesStable")), "Incomplete or unverified run")
            require(completion["Mode"] == "bulk-nodes" and completion["BatchSizes"] == list(SIZES), "Wrong scope")
            require(completion["Samples"] == 6 and completion["Warmups"] == 2 and completion["SampleCount"] == 162
                    and completion["ProtocolCount"] == 18, "Wrong completion counts")
            options = environment["Options"]
            require(options["WriteSizes"] == list(SIZES) and options["GraphSizes"] == [1] and options["EdgeSizes"] == [1]
                    and options["Samples"] == 6 and options["Warmups"] == 2, "Environment disagrees with matrix")
            require(environment["TieredCompilation"] == "0", "JIT comparison differs")
            observed = environment["Environment"]
            require(observed["GitCommit"] == runner["Commit"] and observed["ServerVersion"] == "2026.09.0"
                    and observed["ServerEdition"].lower() == "community", "Server/source provenance differs")
            memory = data["memory-samples.json"]
            require(memory and all(m["AvailableMemoryBytes"] >= runner["ReserveBytes"] + runner["MonitorHeadroomBytes"] for m in memory), "Memory reserve failed")
            require(runner["ReserveBytes"] >= 5 * 1024**3 and runner["Container"]["Disposable"] == "true"
                    and runner["Container"]["Service"] == "neo4j", "Ownership/reserve contract differs")
            samples = data["data/raw-samples.json"]
            protocols = data["data/protocol-samples.json"]
            commands = data["data/commands.json"]
            summary = data["data/summary.json"]
            require(len(samples) == 162 and len(protocols) == len(commands) == len(summary) == 18, "Evidence counts differ")
            for collection in (samples, protocols, commands, summary):
                require({item["Case"] for item in collection} == CASES.keys(), "Case coverage differs")
            cases = {}
            for name, (size, operation, backend) in CASES.items():
                rows = sorted((s for s in samples if s["Case"] == name), key=lambda s: s["Iteration"])
                require([s["Iteration"] for s in rows] == list(range(-3, 6)), "Missing/duplicate iteration")
                expected_runs = 2 * size + 2 if backend == "tracked" else 4 if backend == "bulk" else 2
                for row in rows:
                    phase = "first-observed" if row["Iteration"] == -3 else "warmup" if row["Iteration"] < 0 else "warm"
                    require(row["Phase"] == phase and row["ResultCount"] == size, "Sample phase/result mismatch")
                    require(math.isfinite(row["TotalMilliseconds"]) and row["TotalMilliseconds"] > 0
                            and row["ProcessAllocatedBytes"] >= 0, "Invalid sample measurement")
                    counts = row["Commands"]
                    require(counts["RunAsync"] == expected_runs and counts["Begin"] == counts["Commit"] == 1
                            and counts["Rollback"] == 0, "Driver call/transaction count differs")
                    require(phase != "warm" or row["CompilationCount"] == 0, "Warm query recompiled")
                protocol = next(p["Measurement"] for p in protocols if p["Case"] == name)
                require(protocol["IsValid"] and protocol["InvalidReason"] is None
                        and protocol["CompletedSerialExchanges"] > 0 and protocol["FailureResponses"] == protocol["IgnoredResponses"] == 0
                        and protocol["RequestsByKind"]["Run"] == expected_runs
                        and protocol["RequestsByKind"]["Begin"] == protocol["RequestsByKind"]["Commit"] == 1
                        and protocol["RequestsByKind"]["Rollback"] == 0, "Protocol contradicts actual calls")
                text = next(c["Cypher"] for c in commands if c["Case"] == name)
                require(len(text) == (2 if backend == "driver-batch" else 4) and len(set(text)) == len(text)
                        and all(t.strip() and "DETACH DELETE" not in t for t in text), "Command inventory invalid")
                warm = [s for s in rows if s["Phase"] == "warm"]
                mean = statistics.mean(s["TotalMilliseconds"] for s in warm)
                allocated = statistics.mean(s["ProcessAllocatedBytes"] for s in warm)
                stored = next(s for s in summary if s["Case"] == name)
                require(math.isclose(stored["MeanMilliseconds"], mean, rel_tol=1e-12)
                        and math.isclose(stored["MeanAllocatedBytes"], allocated, rel_tol=1e-12), "Summary does not reproduce")
                cases[name] = dict(meanMilliseconds=mean, meanAllocatedBytes=allocated, runCalls=expected_runs,
                                   boltExchanges=protocol["CompletedSerialExchanges"], commandText=text)
            runs.append(dict(commit=runner["Commit"], harnessSha256=runner["BinarySha256"],
                             providerSha256=environment["ProviderBinarySha256"], seed=options["Seed"],
                             minimumAvailableGiB=min(m["AvailableMemoryBytes"] for m in memory) / 1024**3, cases=cases))
    require(len({r["seed"] for r in runs}) == 3, "Expected three order seeds")
    for field in ("commit", "harnessSha256", "providerSha256"):
        require(len({r[field] for r in runs}) == 1, f"{field} changed across processes")
    comparisons = []
    for size in SIZES:
        for operation in OPERATIONS:
            backends = {}
            for backend in BACKENDS:
                key = f"bulk/batch-{size}/{operation}/{backend}"
                values = [r["cases"][key] for r in runs]
                require(all(v["commandText"] == values[0]["commandText"] for v in values), "Cypher changed across processes")
                means = [v["meanMilliseconds"] for v in values]
                backends[backend] = dict(processMeanMilliseconds=means, meanMilliseconds=statistics.mean(means),
                    rowsPerSecond=size * 1000 / statistics.mean(means),
                    meanAllocatedBytes=statistics.mean(v["meanAllocatedBytes"] for v in values),
                    runCalls=values[0]["runCalls"], observedBoltExchanges=[v["boltExchanges"] for v in values])
            comparisons.append(dict(rows=size, operation=operation, backends=backends,
                bulkVsTrackedThroughputRatio=backends["tracked"]["meanMilliseconds"] / backends["bulk"]["meanMilliseconds"]))
    return dict(formatVersion=1, scope="Node workloads; three serial processes on one shared fixture/host. No edge or production-SLO claim.",
                sourceCommit=runs[0]["commit"], harnessSha256=runs[0]["harnessSha256"], providerSha256=runs[0]["providerSha256"],
                processCount=3, timedObservations=486, warmObservations=324, validProtocolProbes=54,
                minimumAvailableGiB=min(r["minimumAvailableGiB"] for r in runs), comparisons=comparisons,
                archiveSha256=hashlib.sha256(Path(archive).read_bytes()).hexdigest(), fileSha256=hashes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--collect", nargs=3, type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.collect:
        require(not args.check and not args.archive.exists(), "Collect requires a new archive")
        args.archive.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
            for i, root in enumerate(args.collect, 1):
                for name in FILES:
                    entry = zipfile.ZipInfo(f"run-{i}/{name}", date_time=(2000, 1, 1, 0, 0, 0))
                    entry.compress_type = zipfile.ZIP_DEFLATED
                    bundle.writestr(entry, (root / name).read_bytes())
    result = json.dumps(audit(args.archive), indent=2) + "\n"
    if args.check:
        require(args.output.read_text(encoding="utf-8") == result, "Retained comparison does not reproduce")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result, encoding="utf-8")
    print("Verified 486 timed observations, 324 warm observations, 54 valid protocol probes and identical binaries.")


if __name__ == "__main__":
    main()
