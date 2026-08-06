namespace NubArca.Api.Storage;

// Slice 72: marker for the physical store that holds DERIVED media artifacts
// (image thumbnails, medium previews, video posters). Identical contract to
// IBlobStorage — only the root path differs (Storage:DerivedRootPath, or
// Storage:RootPath when that is unset). Keeping it a distinct type lets the
// DI container hand BlobService a second, independently-rooted store without
// ambiguity against the original IBlobStorage.
public interface IDerivedBlobStorage : IBlobStorage
{
}

// Concrete derived store: a LocalFileSystemBlobStorage rooted at the derived
// path. Same on-disk layout (objects/{a}/{b}/{sha256}) as the original store,
// so a derived artifact's StorageKey resolves under whichever root opens it.
public sealed class DerivedFsBlobStorage : LocalFileSystemBlobStorage, IDerivedBlobStorage
{
    public DerivedFsBlobStorage(string rootPath, long maxUploadBytes)
        : base(rootPath, maxUploadBytes)
    {
    }
}
