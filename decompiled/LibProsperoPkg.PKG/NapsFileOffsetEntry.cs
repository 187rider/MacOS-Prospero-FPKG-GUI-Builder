namespace LibProsperoPkg.PKG;

public readonly record struct NapsFileOffsetEntry(byte Type, ulong UncompressedOffsetStart)
{
	public bool Continuation => (Type & 0x40) != 0;
}
