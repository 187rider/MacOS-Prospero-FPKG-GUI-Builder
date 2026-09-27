using System;
using System.IO;

namespace LibProsperoPkg.PFS;

/// <summary>One regular file to place in the PS5 nwonly inner image.</summary>
public sealed class ProsperoPs5InnerFile
{
	/// <summary>Absolute path from the user root, e.g. <c>/sce_sys/keystone</c> or <c>/application.ps.bundle</c>.</summary>
	public required string Path { get; init; }

	/// <summary>The uncompressed file bytes. Can be null or empty when OpenStream is supplied.</summary>
	public byte[]? Data { get; init; }

	/// <summary>The uncompressed size in bytes.</summary>
	public long Size { get; init; }

	/// <summary>Factory to open a readable stream for this file's uncompressed bytes.</summary>
	public Func<Stream>? OpenStream { get; init; }
}
