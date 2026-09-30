using System;

namespace LibProsperoPkg.PKG;

public interface IProsperoMetadataSigner
{
	string ProfileName { get; }

	byte[] SignSha256(ReadOnlySpan<byte> sha256Digest);
}
