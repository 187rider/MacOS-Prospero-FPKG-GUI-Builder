namespace LibProsperoPkg.PKG;

public readonly record struct NapsSectionMap(NapsSection Header, NapsSection OuterBlockDigest, NapsSection ShufflePattern, NapsSection UncompressedOffsetStartByFileIdx, NapsSection CblockInfoOffsetByUblockIdxCompressed, NapsSection CblockInfo, long TotalSize);
