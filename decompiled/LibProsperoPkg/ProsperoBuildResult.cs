using System.Collections.Generic;

namespace LibProsperoPkg;

/// <summary>The result of a build: the output path plus any non-fatal warnings.</summary>
public sealed class ProsperoBuildResult
{
	public required string OutputPath { get; init; }

	public required IReadOnlyList<string> Warnings { get; init; }
}
