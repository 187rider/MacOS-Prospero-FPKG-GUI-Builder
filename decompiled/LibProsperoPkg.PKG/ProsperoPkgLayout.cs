namespace LibProsperoPkg.PKG;

public static class ProsperoPkgLayout
{
	public static readonly byte[] CntMagic = new byte[4] { 127, 67, 78, 84 };

	public static readonly byte[] FihMagic = new byte[4] { 127, 70, 73, 72 };

	public const int EntryMetaSize = 32;

	public const int HeaderSize = 1440;

	public const int ContentIdSize = 48;

	public const uint EntryFlagEncrypted = 2147483648u;

	public const int FihHeaderRegionSize = 65536;

	public const int FihSignedByteOffset = 5;

	public const int FihRetailFinalizationOffset = 61440;

	public const int FihRetailFinalizationSize = 768;

	public const int FihFlexibleContentFinalizationSize = 2560;

	public const int FihPfsImageOffsetField = 16;

	public const int FihPfsImageSizeField = 24;

	public const int FihEmbeddedCntOffsetField = 88;

	public const int FihDataRegionBlockCountField = 80;

	public const int FihInnerImageBlockCountField = 144;

	public const int FihMetaBlockCountField = 148;

	public const int FihMetaBlockCountMirrorField = 152;

	public const int FihInnerImageSizeField = 160;

	public const int FihInnerImageLogicalSizeField = 168;

	public const int FihContentVersionField = 156;

	public const int FihOuterFileCountField = 240;

	public const int FihSparseAfidCountField = 244;

	public const int FihFlatPathTableBlockCountField = 248;

	public const int FihEmptyFileCountField = 252;

	public const uint FihOuterFileCount = 1u;

	public const uint FihFlatPathTableBlockCount = 2u;
}
