namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// PFS compression file-format version recorded in the container header.
/// </summary>
/// <remarks>
/// This is the version of the <i>compression container</i>, not the PFS filesystem superblock
/// version. Only <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version2" /> and <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version3" /> are valid for PS5.
/// </remarks>
public enum ProsperoPfsCompressionFormat
{
	/// <summary>PFSv0. Deprecated.</summary>
	Version0,
	/// <summary>PFSv1. Deprecated.</summary>
	Version1,
	/// <summary>PFSv2. A PS5 compression file format.</summary>
	Version2,
	/// <summary>
	/// PFSv3. The <b>default</b> PS5 compression file format. Supports region hints and
	/// pre-compression shuffles for a greater compression ratio and includes metadata for
	/// efficient conversion to package format (i.e. <c>naps_pkg_layout.dat</c>).
	/// </summary>
	Version3
}
