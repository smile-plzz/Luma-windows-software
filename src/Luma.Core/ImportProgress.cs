namespace Luma.Core;

public sealed record DiscoveryProgress(string Folder,long ExaminedFiles,long SavedFiles,long CompletedFolders,long SkippedFolders);
public sealed record MetadataProgress(string Path,long Checked,long Total,long Saved,long Skipped);
