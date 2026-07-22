using BenchmarkDotNet.Running;

// `--verify` smoke-checks the benchmark corpus instead of measuring it: the
// generator silently skips invalid shapes, so a typo in the benchmark source
// would otherwise just benchmark a smaller workload without anyone noticing.
if (args.Length == 1 && args[0] == "--verify")
    return AutoStaticsCleanup.Benchmarks.GeneratorBenchmarks.VerifyCorpus();

BenchmarkRunner.Run<AutoStaticsCleanup.Benchmarks.GeneratorBenchmarks>(null, args);
return 0;
