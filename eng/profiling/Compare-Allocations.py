"""Recompute FS-11 process-level allocation intervals from retained observations.

Three independent EF processes per revision, each with ten warm reads. Student-t
intervals describe process means under the usual independent normal-mean model;
they do not establish a population-wide performance guarantee from one machine.
"""
import argparse
import json
import math
from pathlib import Path
import statistics


def t2_quantile(probability):
    # For df=2, F(t) = 1/2 + t / (2 * sqrt(t*t + 2)).
    z = 2 * probability - 1
    return z * math.sqrt(2 / (1 - z * z))


def process_interval(values, confidence=0.95):
    if len(values) != 3:
        raise ValueError("Exactly three independent EF process means are required.")
    mean = statistics.mean(values)
    radius = t2_quantile((1 + confidence) / 2) * statistics.stdev(values) / math.sqrt(3)
    return [mean - radius, mean + radius]


def summarize(evidence):
    groups = {(revision, backend): [] for revision in ("before", "after") for backend in ("ef", "driver")}
    identities = set()
    for run in evidence["Runs"]:
        identity = run["EvidenceId"]
        if identity in identities:
            raise ValueError("Repeated evidence directory is not an independent process.")
        identities.add(identity)
        group = (run["Revision"], run["Backend"])
        samples = run["Samples"]
        if not run["Completed"] or not run["Cleaned"] or run["Tracing"]:
            raise ValueError("Only completed, cleaned, untraced experiments are accepted.")
        if len(samples) != 14:
            raise ValueError("Expected one first-observed, three warmups and ten warm reads.")
        if [s["Iteration"] for s in samples if s["Phase"] == "warm"] != list(range(10)):
            raise ValueError("Missing or repeated warm observation.")
        if [s["Iteration"] for s in samples if s["Phase"] == "warmup"] != [-3, -2, -1]:
            raise ValueError("Missing warmup observations.")
        if sum(s["Phase"] == "first-observed" for s in samples) != 1:
            raise ValueError("Missing first-observed read.")
        expected_case = "read/100000/" + ("ef/untracked/full" if run["Backend"] == "ef" else "driver/full")
        for s in samples:
            if s["Case"] != expected_case or s["ResultCount"] != 100000 or s["Checksum"] != 4999950000:
                raise ValueError("Different workload or failed result validation.")
            if not isinstance(s["ProcessAllocatedBytes"], int) or s["ProcessAllocatedBytes"] <= 0:
                raise ValueError("Allocation measurement must be a positive integer byte count.")
            if s["Phase"] == "warm" and s["CompilationCount"] != 0:
                raise ValueError("Warm observations include query compilation.")
        warm = [s for s in samples if s["Phase"] == "warm"]
        groups[group].append(statistics.mean(s["ProcessAllocatedBytes"] for s in warm) / 2**20)
    for revision in ("before", "after"):
        if len(groups[revision, "ef"]) != 3 or len(groups[revision, "driver"]) != 1:
            raise ValueError("Each revision requires three EF processes and one driver control.")
    before, after = groups["before", "ef"], groups["after", "ef"]
    before95, after95 = process_interval(before), process_interval(after)
    # Simultaneous 97.5% component intervals yield an at-least-95% difference
    # interval by the union bound. This does not pair unrelated warm iterations.
    before_joint, after_joint = process_interval(before, 0.975), process_interval(after, 0.975)
    if before_joint[0] <= 0:
        raise ValueError("Baseline interval cannot support a percentage reduction.")
    difference = [before_joint[0] - after_joint[1], before_joint[1] - after_joint[0]]
    reduction = [100 * (1 - after_joint[1] / before_joint[0]), 100 * (1 - after_joint[0] / before_joint[1])]
    return {
        "Unit": "MiB allocated per complete 100000-row read, process-wide",
        "IndependentEfProcessesPerRevision": 3,
        "WarmReadsPerProcess": 10,
        "Before": {"ProcessMeans": before, "Mean": statistics.mean(before), "Mean95Interval": before95},
        "After": {"ProcessMeans": after, "Mean": statistics.mean(after), "Mean95Interval": after95},
        "ReductionMiB": statistics.mean(before) - statistics.mean(after),
        "ReductionMiB95ConservativeInterval": difference,
        "ReductionPercent": 100 * (1 - statistics.mean(after) / statistics.mean(before)),
        "ReductionPercent95ConservativeInterval": reduction,
        "DriverControlBefore": groups["before", "driver"][0],
        "DriverControlAfter": groups["after", "driver"][0],
        "Limitations": "Three independent process means per revision, same machine; normal-mean assumption; sequential before/after, no production or latency guarantee. Driver controls are descriptive single-process means."
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = json.dumps(summarize(json.loads(args.evidence.read_text(encoding="utf-8"))), indent=2) + "\n"
    if args.output:
        args.output.write_text(report, encoding="utf-8")
    else:
        print(report, end="")
