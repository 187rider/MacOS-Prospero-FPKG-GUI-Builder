namespace LibProsperoPkg.PKG;

public readonly record struct NapsCblockInfoEntry
{
	public byte[] Raw { get; init; }

	public bool IsRunBase { get; init; }

	public uint CoffsetStartMod256K { get; init; }

	public bool ReservedBit19 { get; init; }

	public bool IsTerminal
	{
		get
		{
			if (!IsRunBase)
			{
				return ReservedBit19;
			}
			return false;
		}
	}

	public uint UoffsetStart { get; init; }

	public uint ClenEvenMinus1 { get; init; }

	public byte Even { get; init; }

	public byte Odd { get; init; }

	public byte KdePredictor { get; init; }

	public bool ReservedBit67 { get; init; }

	public byte ShuffleIdx { get; init; }

	public uint CoffsetEndMod256K { get; init; }

	public uint TweakIdxStart { get; init; }

	public byte KeyTableIdx { get; init; }

	public uint CoffsetStart256K { get; init; }
}
