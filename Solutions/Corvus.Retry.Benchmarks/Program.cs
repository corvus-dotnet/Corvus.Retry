// <copyright file="Program.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

// Run from the repository root, always in Release:
//   dotnet run -c Release --project Solutions/Corvus.Retry.Benchmarks -- --filter "*"
// Pass --filter to choose benchmarks (for example "*Retriable*"), and --job short for a quick exploratory run.
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Corvus.Retry.Benchmarks.RetriableBenchmarks).Assembly).Run(args);
