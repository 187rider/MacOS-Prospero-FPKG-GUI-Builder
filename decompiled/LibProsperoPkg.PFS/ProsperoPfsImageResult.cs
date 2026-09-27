namespace LibProsperoPkg.PFS;

/// <summary>The outcome of an encrypt/decrypt operation.</summary>
public sealed class ProsperoPfsImageResult
{
	/// <summary>The path the image was written to.</summary>
	public required string OutputPath { get; init; }

	/// <summary>Total image size in bytes.</summary>
	public required long ImageSize { get; init; }

	/// <summary>Block size used (bytes).</summary>
	public required uint BlockSize { get; init; }

	/// <summary>Number of XTS sectors transformed.</summary>
	public required long SectorsTransformed { get; init; }

	/// <summary>The 16-byte seed the keys were derived from.</summary>
	public required byte[] Seed { get; init; }
}
