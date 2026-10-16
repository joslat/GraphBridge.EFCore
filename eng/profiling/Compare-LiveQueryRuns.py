"""Audit and summarize the retained FS-17 matched live benchmark archive (stdlib only)."""
import collections
import json
import math
from pathlib import Path
import statistics
import sys
import zipfile


def compare(archive):
    with zipfile.ZipFile(archive) as evidence:
        def read(name):
            return json.loads(evidence.read(name).decode('utf-8-sig'))

        manifest = read('manifest.json')
        assert len(manifest) == 6, 'Six independently launched processes are required.'
        assert [r['Phase'] for r in manifest] == ['baseline', 'prepared', 'prepared', 'baseline', 'baseline', 'prepared']
        assert len({r['HarnessSha256'] for r in manifest}) == 1, 'Harness binaries must match.'
        for phase, commit in [('baseline', '8d7488c'), ('prepared', '8f1b562')]:
            selected = [r for r in manifest if r['Phase'] == phase]
            assert len({r['ProviderSha256'] for r in selected}) == 1
            assert all(r['ProviderSource'] == commit and r['TieredCompilation'] == '0' for r in selected)
        assert manifest[0]['ProviderSha256'] != manifest[1]['ProviderSha256']
        modes = ['driver/full', 'driver/scalar', 'ef/tracked/full', 'ef/untracked/full',
                 'ef/identity/full', 'ef/tracked/scalar', 'ef/untracked/scalar', 'ef/identity/scalar']
        node_cases = {f'read/{n}/{mode}' for n in [1, 10000] for mode in modes}
        node_cases |= {f'write/graph-{n}/batch-1/{op}/{backend}' for n in [1, 10000]
                       for op in ['create', 'update', 'delete'] for backend in ['driver', 'ef']}
        edge_cases = {f'edge/read/1/{mode}' for mode in modes}
        summaries = collections.defaultdict(lambda: collections.defaultdict(list))
        commands = collections.defaultdict(set)
        command_texts = collections.defaultdict(set)
        minimum_memory = float('inf')
        observations = 0
        plans = 0
        protocol_count = 0
        for row in manifest:
            prefix = f"process-{row['Order']}/"
            runner = read(prefix + 'runner.json')
            assert runner['Outcome'] == 'completed-selected-matrix'
            assert runner['BinarySha256'].upper() == row['HarnessSha256'].upper()
            assert runner['ReserveBytes'] == 5 * 1024**3
            assert runner['SelectedMatrix']['Samples'] == 20 and runner['SelectedMatrix']['Warmups'] == 3
            memory = read(prefix + 'memory-samples.json')
            assert memory
            minimum_memory = min(minimum_memory, *(s['AvailableMemoryBytes'] for s in memory))
            assert all(s['AvailableMemoryBytes'] >= runner['ReserveBytes'] for s in memory)
            completed = read(prefix + 'data/completion.json')
            assert completed['Completed'] is True
            assert completed['Nodes']['GraphSizes'] == [1, 10000] and completed['Nodes']['WriteSizes'] == [1]
            assert read(prefix + 'data/edge-completion.json')['Completed'] is True
            for file, expected in [('raw-samples.json', node_cases), ('edge-raw-samples.json', edge_cases)]:
                samples = read(prefix + 'data/' + file)
                groups = collections.defaultdict(list)
                for sample in samples:
                    groups[sample['Case']].append(sample)
                    for key in ['TotalMilliseconds', 'ProcessAllocatedBytes']:
                        assert math.isfinite(sample[key]) and sample[key] >= 0
                assert set(groups) == expected, file
                observations += len(samples)
                for case, group in groups.items():
                    assert collections.Counter(s['Phase'] for s in group) == {'first-observed': 1, 'warmup': 3, 'warm': 20}
                    warm = [s for s in group if s['Phase'] == 'warm']
                    assert sorted(s['Iteration'] for s in warm) == list(range(20))
                    assert all(s['CompilationCount'] == 0 for s in warm), case
                    for sample in group:
                        commands[case].add(tuple(sorted(sample['Commands'].items())))
                        if case.startswith(('read/', 'edge/read/')):
                            size = int(case.split('/')[1 if case.startswith('read/') else 2])
                            assert sample['ResultCount'] == size and sample['Checksum'] == size * (size - 1) // 2
                    summaries[case][row['Phase']].append({
                        'process': row['Order'],
                        'meanMilliseconds': statistics.mean(s['TotalMilliseconds'] for s in warm),
                        'medianMilliseconds': statistics.median(s['TotalMilliseconds'] for s in warm),
                        'meanAllocatedBytes': statistics.mean(s['ProcessAllocatedBytes'] for s in warm),
                        'meanFirstRowMilliseconds': statistics.mean(s['FirstRowMilliseconds'] for s in warm)
                            if all(s['FirstRowMilliseconds'] is not None for s in warm) else None,
                    })
            for file, expected in [('protocol-samples.json', 28), ('edge-protocol-samples.json', 8)]:
                protocols = read(prefix + 'data/' + file)
                assert len(protocols) == expected and all(p['Measurement']['IsValid'] for p in protocols)
                assert {p['Case'] for p in protocols} == (node_cases if expected == 28 else edge_cases)
                protocol_count += len(protocols)
            for file, expected in [('explanations.json', 26), ('profiles.json', 26), ('edge-plans.json', 16)]:
                actual = read(prefix + 'data/' + file)
                assert len(actual) == expected, file
                if file == 'edge-plans.json':
                    assert collections.Counter(p['Case'] for p in actual) == {case: 2 for case in edge_cases}
                    assert {p['Mode'] for p in actual} == {'EXPLAIN', 'PROFILE'}
                else:
                    expected_plans = collections.Counter({case: 1 for case in node_cases if case.startswith('read/')})
                    for size in [1, 10000]:
                        for operation in ['create', 'lock', 'update', 'lock', 'delete']:
                            expected_plans[f'write/graph-{size}/representative-single-node/{operation}'] += 1
                    assert collections.Counter(p['Case'] for p in actual) == expected_plans
                for plan in actual:
                    assert plan['Plan']['OperatorType']
                    command_texts[plan['Case']].add(plan['Command'])
                plans += len(actual)
        # Driver-call counts must be stable for a given workload across phases and revisions.
        assert all(len(value) == 1 for value in commands.values()), 'Command-count regression or unstable workload.'
        assert all(len(value) == 1 for value in command_texts.values()), 'Generated command shape changed.'
        cases = []
        for case, phases in sorted(summaries.items()):
            before = statistics.mean(x['meanMilliseconds'] for x in phases['baseline'])
            after = statistics.mean(x['meanMilliseconds'] for x in phases['prepared'])
            old_bytes = statistics.mean(x['meanAllocatedBytes'] for x in phases['baseline'])
            new_bytes = statistics.mean(x['meanAllocatedBytes'] for x in phases['prepared'])
            cases.append({'case': case, 'processes': phases, 'baselineMeanMilliseconds': before,
                          'preparedMeanMilliseconds': after, 'throughputRatio': before / after,
                          'baselineMeanAllocatedBytes': old_bytes, 'preparedMeanAllocatedBytes': new_bytes,
                          'allocationRatio': new_bytes / old_bytes,
                          'commands': dict(next(iter(commands[case])))})
        return {'scope': 'Six processes, unchanged harness, fixed JIT; descriptive process means, no production SLO or causal attribution of all timing differences.',
                'observations': observations, 'warmObservations': len(cases) * 20 * 6,
                'plans': plans, 'protocolCases': protocol_count, 'minimumAvailableMemoryBytes': minimum_memory,
                'cases': cases}


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python eng/profiling/Compare-LiveQueryRuns.py ARCHIVE.zip')
    print(json.dumps(compare(Path(sys.argv[1])), indent=2, allow_nan=False))
