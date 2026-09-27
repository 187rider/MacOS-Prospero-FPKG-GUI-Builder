namespace LibProsperoPkg;

/// <summary>The representation a built inner-PFS image is rendered in.</summary>
public enum InnerImageForm
{
	/// <summary>An unsigned, unencrypted PFS image (raw layout).</summary>
	Plaintext,
	/// <summary>An AES-XTS-encrypted PFS image (plaintext superblock + encrypted filesystem).</summary>
	Encrypted,
	/// <summary>A PFSC-compressed PFS image (the <c>pfs_image.dat</c> form).</summary>
	Compressed,
	/// <summary>
	/// A PS5 PFSv3 Kraken-compressed PFS image — the codec the
	/// <c>nwonly</c> path uses for the inner image. The container is self-describing
	/// (magic <c>PFSC</c>, format version 3, 0x40000 blocks, SHA3-256 digests) and is round-trip
	/// validated in-process with the managed Kraken decoder. Distinct from <see cref="F:LibProsperoPkg.InnerImageForm.Compressed" />,
	/// which is the zlib PFSC used for the installable inner image.
	/// </summary>
	KrakenCompressed
}
