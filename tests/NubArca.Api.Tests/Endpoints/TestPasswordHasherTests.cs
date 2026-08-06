using Microsoft.AspNetCore.Identity;
using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Endpoints;

public sealed class TestPasswordHasherTests
{
    [Fact]
    public void Hash_Does_Not_Contain_The_Password_And_Verifies_The_Right_Value()
    {
        var hasher = new TestPasswordHasher<User>();
        var user = new User { Id = Guid.NewGuid(), Email = "test@example.com", DisplayName = "Test" };

        var hash = hasher.HashPassword(user, "secret-value");

        Assert.DoesNotContain("secret-value", hash, StringComparison.Ordinal);
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, hash, "secret-value"));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, hash, "wrong-value"));
    }

    [Fact]
    public void Hashes_Are_Domain_Separated_By_User_Type()
    {
        var password = "same-password";
        var userHash = new TestPasswordHasher<User>().HashPassword(
            new User { Id = Guid.NewGuid(), Email = "test@example.com", DisplayName = "Test" },
            password);
        var vaultHash = new TestPasswordHasher<PrivateVault>().HashPassword(
            new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), PasswordHash = "_" },
            password);

        Assert.NotEqual(userHash, vaultHash);
    }
}
