namespace LibProsperoPkg.PFS;

/// <summary>
/// Classification of an outer-PFS block for AES-XTS sector numbering.
/// </summary>
public enum ProsperoOuterBlockKind : byte
{
	/// <summary>Plain file-data block: XTS sector = block index.</summary>
	Data,
	/// <summary>
	/// Signed / metadata block: XTS sector = <see cref="F:LibProsperoPkg.PFS.ProsperoOuterPfsSignature.SignedBlockTweakFlag" />
	/// | block index.
	/// </summary>
	Signed,
	/// <summary>Superblock / metadata block stored plaintext (not encrypted).</summary>
	Plaintext
}
