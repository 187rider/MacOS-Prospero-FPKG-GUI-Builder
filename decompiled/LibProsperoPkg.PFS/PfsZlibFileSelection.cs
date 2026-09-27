namespace LibProsperoPkg.PFS;

/// <summary>Selects which regular files are eligible for classic PFSC/zlib compression.</summary>
public enum PfsZlibFileSelection
{
	/// <summary>Compress every otherwise eligible file.</summary>
	AllEligibleFiles,
	/// <summary>Compress only files whose logical size is an exact multiple of 64 KiB.</summary>
	SizeMultipleOf64KiB
}
