using NubArca.Api.Domain;
using NubArca.Api.Organizer;
using Xunit;

namespace NubArca.Api.Tests.Organizer;

// Pure unit tests for the date-taken planner: effective-date precedence,
// template generation, segment validation (no traversal), and deterministic
// conflict naming. No DB.
public sealed class PhotoDateTakenPlannerTests
{
    private static readonly DateTime Override = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Embedded = new(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2024, 11, 12, 13, 14, 15, DateTimeKind.Utc);

    [Fact]
    public void Resolve_Prefers_UserOverride()
    {
        var r = PhotoDateTakenPlanner.Resolve(Override, Embedded, "DateTimeOriginal", Created, MissingDateBehavior.Skip);
        Assert.Equal(PhotoOrganizerDateSources.UserOverride, r.Source);
        Assert.Equal(Override, r.BucketDate);
    }

    [Fact]
    public void Resolve_Uses_EmbeddedOriginal_When_No_Override()
    {
        var r = PhotoDateTakenPlanner.Resolve(null, Embedded, "DateTimeOriginal", Created, MissingDateBehavior.Skip);
        Assert.Equal(PhotoOrganizerDateSources.MetadataOriginal, r.Source);
        Assert.Equal(Embedded, r.BucketDate);
    }

    [Fact]
    public void Resolve_Maps_NonOriginal_Embedded_To_Fallback()
    {
        var r = PhotoDateTakenPlanner.Resolve(null, Embedded, "DateTimeDigitized", Created, MissingDateBehavior.Skip);
        Assert.Equal(PhotoOrganizerDateSources.MetadataFallback, r.Source);
        Assert.Equal(Embedded, r.BucketDate);
    }

    [Fact]
    public void Resolve_Missing_Skip_Marks_SkipMissing()
    {
        var r = PhotoDateTakenPlanner.Resolve(null, null, null, Created, MissingDateBehavior.Skip);
        Assert.Equal(PhotoOrganizerDateSources.Missing, r.Source);
        Assert.True(r.SkipMissing);
        Assert.False(r.UnknownFolder);
        Assert.Null(r.BucketDate);
    }

    [Fact]
    public void Resolve_Missing_FileCreated_Uses_CreatedAt()
    {
        var r = PhotoDateTakenPlanner.Resolve(null, null, null, Created, MissingDateBehavior.FileCreated);
        Assert.Equal(PhotoOrganizerDateSources.FileCreatedFallback, r.Source);
        Assert.Equal(Created, r.BucketDate);
        Assert.False(r.SkipMissing);
    }

    [Fact]
    public void Resolve_Missing_UnknownFolder_Routes_To_Unknown()
    {
        var r = PhotoDateTakenPlanner.Resolve(null, null, null, Created, MissingDateBehavior.UnknownFolder);
        Assert.Equal(PhotoOrganizerDateSources.Missing, r.Source);
        Assert.True(r.UnknownFolder);
        var segs = PhotoDateTakenPlanner.TargetSegments(r, OrganizerTemplate.YearDatedDay);
        Assert.Equal(new[] { OrganizerPaths.UnknownDateFolder }, segs);
    }

    [Theory]
    [InlineData(OrganizerTemplate.Year, "2024")]
    [InlineData(OrganizerTemplate.YearMonth, "2024/05")]
    [InlineData(OrganizerTemplate.YearMonthDay, "2024/05/17")]
    [InlineData(OrganizerTemplate.YearDatedDay, "2024/2024-05-17")]
    public void TemplateSegments_Generate_Expected_Paths(OrganizerTemplate template, string expected)
    {
        var segs = OrganizerPaths.TemplateSegments(template, new DateTime(2024, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(expected, string.Join('/', segs));
    }

    [Theory]
    [InlineData("Photos", true)]
    [InlineData("2024-05-17", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidSegment_Rejects_Traversal_And_Invalid(string segment, bool expected)
    {
        Assert.Equal(expected, OrganizerPaths.IsValidSegment(segment));
    }

    [Fact]
    public void PickName_Returns_Base_When_Free()
    {
        var taken = new HashSet<string>();
        Assert.Equal("IMG.jpg", PhotoDateTakenPlanner.PickName("IMG.jpg", taken, ConflictPolicy.KeepBoth));
    }

    [Fact]
    public void PickName_Skip_Returns_Null_On_Conflict()
    {
        var taken = new HashSet<string> { "IMG.jpg" };
        Assert.Null(PhotoDateTakenPlanner.PickName("IMG.jpg", taken, ConflictPolicy.Skip));
    }

    [Fact]
    public void PickName_KeepBoth_Suffixes_Before_Extension()
    {
        var taken = new HashSet<string> { "IMG.jpg", "IMG (1).jpg" };
        Assert.Equal("IMG (2).jpg", PhotoDateTakenPlanner.PickName("IMG.jpg", taken, ConflictPolicy.KeepBoth));
    }

    [Theory]
    [InlineData("IMG.jpg", "IMG", ".jpg")]
    [InlineData("archive.tar.gz", "archive.tar", ".gz")]
    [InlineData("README", "README", "")]
    [InlineData(".env", ".env", "")]
    public void SplitExtension_Splits_On_Last_Dot(string name, string stem, string ext)
    {
        var (s, e) = PhotoDateTakenPlanner.SplitExtension(name);
        Assert.Equal(stem, s);
        Assert.Equal(ext, e);
    }
}
