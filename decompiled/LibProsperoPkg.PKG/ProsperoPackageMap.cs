namespace LibProsperoPkg.PKG;

public readonly record struct ProsperoPackageMap(long FihOffset, long FihSize, long OuterPfsOffset, long OuterPfsSize, long CntOffset, long CntSize, long SupplementOffset, long SupplementSize, int OuterSuperblockIndex);
