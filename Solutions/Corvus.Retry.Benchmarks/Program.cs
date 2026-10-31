// <copyright file="Program.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

// Run from the repository root, always in Release:
//   dotnet run -c Release --project Solutions/Corvus.Retry.Benchmarks -- --filter "*"
// Pass --filter to choose benchmarks (for example "*Retriable*"), and --job short for a quick exploratory run.
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

// A retry library spends much of its time on exceptions, so every benchmark also counts the exceptions per operation.
IConfig config = DefaultConfig.Instance.AddDiagnoser(ExceptionDiagnoser.Default);

BenchmarkSwitcher.FromAssembly(typeof(Corvus.Retry.Benchmarks.RetriableBenchmarks).Assembly).Run(args, config);
