using NubArca.Api.Organizer;
using Xunit;

namespace NubArca.Api.Tests.Organizer;

// Validation gate for the organizer request → options. Rejects unknown enum
// values, bad scope combinations, and unsafe target-root names.
public sealed class OrganizerOptionsTests
{
    private static PhotoOrganizerRequest Valid(
        string scope = "all", string template = "yyyy/yyyy-MM-dd",
        string? missing = "skip", string? conflict = "keep_both",
        IReadOnlyList<Guid>? fileIds = null, string? targetRootName = null)
        => new(scope, null, fileIds, null, targetRootName, template, missing, conflict);

    [Fact]
    public void Parses_A_Valid_Request()
    {
        Assert.True(OrganizerOptions.TryParse(Valid(), out var options, out _));
        Assert.Equal(OrganizerScopeKind.All, options.Scope);
        Assert.Equal(OrganizerTemplate.YearDatedDay, options.Template);
        Assert.Equal(MissingDateBehavior.Skip, options.MissingBehavior);
        Assert.Equal(ConflictPolicy.KeepBoth, options.Conflict);
        Assert.Equal("Photos", options.TargetRootName); // default
    }

    [Theory]
    [InlineData("bogus", "yyyy", "skip", "skip")]
    [InlineData("all", "yyyy/bad", "skip", "skip")]
    [InlineData("all", "yyyy", "weird", "skip")]
    [InlineData("all", "yyyy", "skip", "overwrite")]
    public void Rejects_Unknown_Enum_Values(string scope, string template, string missing, string conflict)
    {
        Assert.False(OrganizerOptions.TryParse(Valid(scope, template, missing, conflict), out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Selected_Scope_Requires_File_Ids()
    {
        Assert.False(OrganizerOptions.TryParse(Valid(scope: "selected", fileIds: Array.Empty<Guid>()), out _, out var error));
        Assert.Contains("file id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selected_Scope_Accepts_File_Ids()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        Assert.True(OrganizerOptions.TryParse(Valid(scope: "selected", fileIds: ids), out var options, out _));
        Assert.Equal(ids, options.FileIds);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    public void Rejects_Unsafe_Target_Root_Name(string name)
    {
        Assert.False(OrganizerOptions.TryParse(Valid(targetRootName: name), out _, out var error));
        Assert.Contains("target root", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blank_Target_Root_Name_Means_No_Extra_Segment()
    {
        Assert.True(OrganizerOptions.TryParse(Valid(targetRootName: "  "), out var options, out _));
        Assert.Null(options.TargetRootName);
    }

    [Fact]
    public void Missing_And_Conflict_Default_When_Omitted()
    {
        Assert.True(OrganizerOptions.TryParse(Valid(missing: null, conflict: null), out var options, out _));
        Assert.Equal(MissingDateBehavior.Skip, options.MissingBehavior);
        Assert.Equal(ConflictPolicy.KeepBoth, options.Conflict);
    }
}
