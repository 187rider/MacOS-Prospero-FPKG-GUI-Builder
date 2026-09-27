namespace LibProsperoPkg.PFS;

/// <summary>
/// Describes one file that lives at the <em>outer</em> level of a PS5 nwonly finalized image.
/// An nwonly outer PFS always contains exactly two files - <c>pfs_image.dat</c> (the nested inner
/// image) and <c>naps_pkg_layout.dat</c> - but this type keeps the list general.
/// </summary>
public sealed class ProsperoOuterFile
{
	/// <summary>The outer file name (e.g. <c>pfs_image.dat</c>), stored lowercase in dirents.</summary>
	public required string Name { get; init; }

	/// <summary>The raw file bytes. Laid out block-by-block (zero-padded to the block size).</summary>
	public required byte[] Data { get; init; }

	/// <summary>
	/// The dinode <c>SizeCompressed</c> field (dinode+0x10). For <c>naps_pkg_layout.dat</c> this
	/// equals the file length; for <c>pfs_image.dat</c> it is the nested image's logical
	/// (uncompressed) size as recorded by the inner-image builder. Defaults to <see cref="P:LibProsperoPkg.PFS.ProsperoOuterFile.Data" />'s length.
	/// </summary>
	public long? SizeCompressed { get; init; }

	/// <summary>
	/// Whether this file's data blocks are AES-XTS encrypted as <em>signed</em> blocks (sector =
	/// bit 47 | block index). <c>pfs_image.dat</c> is plain data and
	/// <c>naps_pkg_layout.dat</c> is signed.
	/// </summary>
	public bool Signed { get; init; }
}
