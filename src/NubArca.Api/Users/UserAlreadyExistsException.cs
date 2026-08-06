namespace NubArca.Api.Users;

public sealed class UserAlreadyExistsException : Exception
{
    public string Email { get; }

    public UserAlreadyExistsException(string email)
        : base($"A user with email '{email}' already exists.")
    {
        Email = email;
    }
}
