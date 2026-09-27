namespace LibProsperoPkg.PFS;

/// <summary>
/// Addressing geometry of one signed outer-PFS inode. Index 0 in both arrays describes
/// <c>ib[0]</c> (single indirect), index 1 describes <c>ib[1]</c> (double indirect), and so on.
/// This is useful for checking very large image layouts without materializing their payload.
/// </summary>
public sealed class ProsperoOuterAddressingGeometry
{
	public required long DataBlocks { get; init; }

	public required long DirectDataBlocks { get; init; }

	public required long[] DataBlocksByIndirectLevel { get; init; }

	public required long[] MetadataBlocksByIndirectLevel { get; init; }

	public required int HighestIndirectLevel { get; init; }

	public long TotalIndirectMetadataBlocks
	{
		get
		{
			long num = 0L;
			long[] metadataBlocksByIndirectLevel = MetadataBlocksByIndirectLevel;
			foreach (long num2 in metadataBlocksByIndirectLevel)
			{
				num = checked(num + num2);
			}
			return num;
		}
	}
}
