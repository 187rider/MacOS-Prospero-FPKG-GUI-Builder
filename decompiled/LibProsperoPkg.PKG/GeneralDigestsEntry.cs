using System;
using System.Collections.Generic;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class GeneralDigestsEntry : Entry
{
	public ushort unk1 = 53846;

	public ushort type = 256;

	public GeneralDigest set_digests;

	public Dictionary<GeneralDigest, byte[]> Digests = new Dictionary<GeneralDigest, byte[]>
	{
		{
			GeneralDigest.ContentDigest,
			new byte[32]
		},
		{
			GeneralDigest.GameDigest,
			new byte[32]
		},
		{
			GeneralDigest.HeaderDigest,
			new byte[32]
		},
		{
			GeneralDigest.SystemDigest,
			new byte[32]
		},
		{
			GeneralDigest.MajorParamDigest,
			new byte[32]
		},
		{
			GeneralDigest.ParamDigest,
			new byte[32]
		},
		{
			GeneralDigest.PlaygoDigest,
			new byte[32]
		},
		{
			GeneralDigest.TrophyDigest,
			new byte[32]
		},
		{
			GeneralDigest.ManualDigest,
			new byte[32]
		},
		{
			GeneralDigest.KeymapDigest,
			new byte[32]
		},
		{
			GeneralDigest.OriginDigest,
			new byte[32]
		},
		{
			GeneralDigest.TargetDigest,
			new byte[32]
		},
		{
			GeneralDigest.OriginGameDigest,
			new byte[32]
		},
		{
			GeneralDigest.TargetGameDigest,
			new byte[32]
		}
	};

	public override EntryId Id => EntryId.GENERAL_DIGESTS;

	public override uint Length
	{
		get
		{
			if (type != 256)
			{
				if (type != 257)
				{
					return 480u;
				}
				return 448u;
			}
			return 384u;
		}
	}

	public override string Name => null;

	public void Set(GeneralDigest flag, byte[] value)
	{
		Buffer.BlockCopy(value, 0, Digests[flag], 0, 32);
		set_digests |= flag;
	}

	public override void Write(Stream s)
	{
		s.WriteUInt16BE(unk1);
		s.WriteUInt16BE(type);
		s.Position += 24L;
		s.WriteInt32BE((int)set_digests);
		GeneralDigest[] array = new GeneralDigest[14]
		{
			GeneralDigest.ContentDigest,
			GeneralDigest.GameDigest,
			GeneralDigest.HeaderDigest,
			GeneralDigest.SystemDigest,
			GeneralDigest.MajorParamDigest,
			GeneralDigest.ParamDigest,
			GeneralDigest.PlaygoDigest,
			GeneralDigest.TrophyDigest,
			GeneralDigest.ManualDigest,
			GeneralDigest.KeymapDigest,
			GeneralDigest.OriginDigest,
			GeneralDigest.TargetDigest,
			GeneralDigest.OriginGameDigest,
			GeneralDigest.TargetGameDigest
		};
		int num = (int)((Length - 32) / 32);
		for (int i = 0; i < num && i < array.Length; i++)
		{
			s.Write(Digests[array[i]], 0, 32);
		}
	}

	public static GeneralDigestsEntry Read(Stream s)
	{
		GeneralDigestsEntry generalDigestsEntry = new GeneralDigestsEntry();
		generalDigestsEntry.unk1 = s.ReadUInt16BE();
		generalDigestsEntry.type = s.ReadUInt16BE();
		s.Position += 24L;
		generalDigestsEntry.set_digests = (GeneralDigest)s.ReadUInt32BE();
		for (GeneralDigest generalDigest = GeneralDigest.ContentDigest; generalDigest < (GeneralDigest)32768; generalDigest = (GeneralDigest)((int)generalDigest << 1))
		{
			s.ReadExactly(generalDigestsEntry.Digests[generalDigest], 0, 32);
		}
		return generalDigestsEntry;
	}
}
