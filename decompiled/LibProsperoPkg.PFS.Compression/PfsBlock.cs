using System;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// One block of a parsed <see cref="T:LibProsperoPkg.PFS.Compression.CompressedPfsFile" />.
/// </summary>
public readonly struct PfsBlock
{
	/// <summary>The zero-based block index.</summary>
	public int Index { get; init; }

	/// <summary>The absolute byte offset of this block's compressed data within the container file.</summary>
	public long CompressedOffset { get; init; }

	/// <summary>The compressed size, in bytes, of this block within the container.</summary>
	public int CompressedSize { get; init; }

	/// <summary>The logical (uncompressed) byte offset this block expands to.</summary>
	public long UncompressedOffset { get; init; }

	/// <summary>The uncompressed size, in bytes, this block expands to.</summary>
	public int UncompressedSize { get; init; }

	/// <summary>
	/// The block's stored SHA3-256 digest (32 bytes), taken over the <i>uncompressed</i> block bytes.
	/// Verify a decoded block with <see cref="M:LibProsperoPkg.PFS.Compression.PfsDigest.VerifyBlockDigest(System.ReadOnlySpan{System.Byte},System.ReadOnlySpan{System.Byte})" />.
	/// </summary>
	public ReadOnlyMemory<byte> Hash { get; init; }

	/// <summary>The raw compressed bytes of this block (a slice of the container buffer).</summary>
	public ReadOnlyMemory<byte> CompressedData { get; init; }

	/// <summary>
	/// <c>true</c> when the block is stored uncompressed (<see cref="P:LibProsperoPkg.PFS.Compression.PfsBlock.CompressedSize" /> equals
	/// <see cref="P:LibProsperoPkg.PFS.Compression.PfsBlock.UncompressedSize" />), as the format does for incompressible data; otherwise the
	/// block is a Kraken bitstream.
	/// </summary>
	public bool IsStored => CompressedSize == UncompressedSize;

	/// <summary>
	/// <c>true</c> when a compressed block is split into two newLZ chunks (boundary flag <c>0x26</c>
	/// rather than <c>0x06</c>), which happens for blocks larger than 128 KiB. The first chunk's
	/// compressed size is then <see cref="P:LibProsperoPkg.PFS.Compression.PfsBlock.FirstChunkCompressedSize" />.
	/// </summary>
	public bool IsMultiChunk { get; init; }

	/// <summary>
	/// <c>true</c> when a compressed block is a single bare-entropy array rather than an LZ stream
	/// (boundary flag bit <c>0x02</c> clear). The whole block payload is then one Kraken entropy array
	/// (typically a type-2 Huffman array) that expands directly to the uncompressed bytes — no seed,
	/// no LZ table, no literal model. The encoder emits this form when entropy coding helps but LZ matching
	/// does not (skewed-frequency data). Always <c>false</c> for stored and newLZ blocks.
	/// </summary>
	public bool IsBareEntropy { get; init; }

	/// <summary>
	/// For a two-chunk compressed block, the compressed size in bytes of the first chunk (recovered
	/// from the boundary size hint); zero otherwise. The second chunk occupies the remaining bytes.
	/// </summary>
	public int FirstChunkCompressedSize { get; init; }

	/// <summary>
	/// The newLZ literal model for this compressed block, recovered from the boundary flag's low bit
	/// (the bit the PFS layer feeds into the reconstructed Oodle chunk header): <c>1</c> = raw literals,
	/// <c>0</c> = sub/delta literals. Irrelevant for stored blocks.
	/// </summary>
	public int LiteralMode { get; init; }

	/// <summary>
	/// The raw boundary-table flag byte for this block (bits 48..55 of the id=3 entry). This is the
	/// authoritative per-sub-chunk type/literal-model selector consumed by
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder.DecodeBlock(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32,System.Span{System.Byte})" />: chunk0 newLZ = bit <c>0x02</c> (literal model
	/// bit <c>0x01</c>); chunk1 newLZ = bit <c>0x20</c> (literal model bit <c>0x10</c>), bare-entropy
	/// chunk1 = bit <c>0x40</c>. Zero for stored blocks.
	/// </summary>
	public int Flags { get; init; }

	/// <summary>
	/// The pre-compression shuffle applied to this block. Containers produced without region hints
	/// (all known default-output containers) always use <see cref="F:LibProsperoPkg.PFS.Compression.PfsShufflePattern.None" />.
	/// </summary>
	/// <remarks>
	/// The per-block storage location of a non-<see cref="F:LibProsperoPkg.PFS.Compression.PfsShufflePattern.None" /> pattern has not
	/// been validated against reference output, so this reader
	/// reports <see cref="F:LibProsperoPkg.PFS.Compression.PfsShufflePattern.None" />. Do not rely on it to detect shuffled blocks.
	/// </remarks>
	public PfsShufflePattern ShufflePattern => PfsShufflePattern.None;
}
