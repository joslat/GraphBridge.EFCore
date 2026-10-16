import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("allocation_comparison", Path(__file__).with_name("Compare-Allocations.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class AllocationComparisonTests(unittest.TestCase):
    def evidence(self):
        runs = []
        for revision, base in (("before", 100), ("after", 50)):
            for backend, means in (("ef", [base - 1, base, base + 1]), ("driver", [40])):
                for index, mean in enumerate(means):
                    samples = [{"Case": "read/100000/" + ("ef/untracked/full" if backend == "ef" else "driver/full"),
                                "Phase": phase, "Iteration": i, "ProcessAllocatedBytes": mean * 2**20,
                                "ResultCount": 100000, "Checksum": 4999950000, "CompilationCount": 0}
                               for phase, i in [("first-observed", 0)] + [("warmup", i) for i in range(-3, 0)] + [("warm", i) for i in range(10)]]
                    runs.append({"Revision": revision, "Backend": backend, "EvidenceId": f"{revision}-{backend}-{index}",
                                 "Completed": True, "Cleaned": True, "Tracing": False, "Samples": samples})
        return {"Runs": runs}

    def test_known_t_quantile_and_process_mean_interval(self):
        self.assertAlmostEqual(4.302652729911275, comparison.t2_quantile(0.975), places=8)
        low, high = comparison.process_interval([99, 100, 101])
        self.assertAlmostEqual(97.51586228828046, low, places=8)
        self.assertAlmostEqual(102.48413771171954, high, places=8)

    def test_reduction_uses_process_means_and_positive_conservative_interval(self):
        report = comparison.summarize(self.evidence())
        self.assertEqual(50, report["ReductionMiB"])
        self.assertEqual(50, report["ReductionPercent"])
        low, high = report["ReductionMiB95ConservativeInterval"]
        self.assertGreater(low, 40)
        self.assertLess(high, 60)
        self.assertLess(low, 50)
        self.assertGreater(high, 50)

    def test_duplicate_failed_traced_corrupt_or_recompiled_runs_are_rejected(self):
        changes = [lambda e: e["Runs"].append(copy.deepcopy(e["Runs"][0])),
                   lambda e: e["Runs"][0].update(Cleaned=False),
                   lambda e: e["Runs"][0].update(Tracing=True),
                   lambda e: e["Runs"][0]["Samples"][-1].update(ResultCount=99999),
                   lambda e: e["Runs"][0]["Samples"][-1].update(CompilationCount=1),
                   lambda e: e["Runs"][0]["Samples"][-1].update(Iteration=0)]
        for change in changes:
            with self.subTest(change=change):
                evidence = self.evidence()
                change(evidence)
                with self.assertRaises(ValueError):
                    comparison.summarize(evidence)


if __name__ == "__main__":
    unittest.main()
