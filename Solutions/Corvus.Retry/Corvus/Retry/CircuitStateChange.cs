// <copyright file="CircuitStateChange.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Corvus.Retry;

using System;

/// <summary>
/// Describes a change of the state of a <see cref="CircuitBreaker"/>.
/// </summary>
/// <param name="From">The state before the change.</param>
/// <param name="To">The state after the change.</param>
/// <param name="LastException">
/// The exception of the failure that opened the circuit, or <see langword="null"/> when the change has no exception (for
/// example, when a result that is a failure opened the circuit, or when the change is not to
/// <see cref="CircuitState.Open"/>).
/// </param>
public readonly record struct CircuitStateChange(CircuitState From, CircuitState To, Exception? LastException);
