using System;

namespace LibProsperoPkg.PKG;

public interface IProsperoMetadataSignatureVerifier
{
	bool VerifySha256(ReadOnlySpan<byte> sha256Digest, ReadOnlySpan<byte> signature);
}
