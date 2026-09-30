namespace LibProsperoPkg.PKG;

public readonly record struct NapsLayoutCounts(int NumFiles, byte CompressionType, int NumKeys, int NumShufflePatterns, int NumUBlocks, int NumOuterBlocks, int NumCblockInfo)
{
	public int LogicalFileCount => checked(NumFiles - 1);

	public int UBlockCount => checked(NumUBlocks + 1);

	public int NumU2cEntries => checked(NumUBlocks + 8) >> 3;
}
