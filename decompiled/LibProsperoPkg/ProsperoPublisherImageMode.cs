namespace LibProsperoPkg;

/// <summary>Selects the runtime representation of the publisher outer PFS.</summary>
public enum ProsperoPublisherImageMode
{
	/// <summary>
	/// Stock representation: the outer PFS advertises encryption and all non-superblock blocks
	/// are transformed with AES-XTS.
	/// </summary>
	Native,
	/// <summary>
	/// Research representation: the outer PFS is stored as plaintext and the keyed NAPS
	/// outer-block authentication tags are omitted. Ordinary SHA3 PFS hashes and the superblock
	/// ICV remain enabled.
	/// </summary>
	PlaintextNoAuth
}
