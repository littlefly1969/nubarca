namespace NubArca.Api.Albums;

public class DuplicateAlbumNameException : Exception
{
    public DuplicateAlbumNameException(string name)
        : base($"An album named '{name}' already exists.") { }
}
