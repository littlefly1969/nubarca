using NubArca.Api.Files;
using NubArca.Api.MediaLibrary;
using Xunit;

namespace NubArca.Api.Tests.MediaLibrary;

// Slice 3: the media-library scope participates in the gallery cursor identity,
// so an Active cursor can never silently replay in the Excluded tab.
public sealed class MediaLibraryScopeQueryTests
{
    [Fact]
    public void Active_Scope_With_No_Other_Filters_Is_Empty_And_Unbound()
    {
        var filters = new ImageFilters { Scope = MediaLibraryScope.Active };
        Assert.True(filters.IsEmpty);
        Assert.Null(filters.Fingerprint());
    }

    [Fact]
    public void Excluded_Scope_Alone_Forces_A_Distinct_Cursor_Fingerprint()
    {
        var excluded = new ImageFilters { Scope = MediaLibraryScope.Excluded };
        Assert.False(excluded.IsEmpty);
        Assert.NotNull(excluded.Fingerprint());

        // Active (empty) and Excluded fingerprints must differ so a cursor issued
        // for one scope is rejected when replayed under the other.
        var active = new ImageFilters { Scope = MediaLibraryScope.Active };
        Assert.NotEqual(active.Fingerprint(), excluded.Fingerprint());
    }

    [Fact]
    public void Scope_Changes_The_Fingerprint_Even_With_Identical_Other_Filters()
    {
        var baseFilters = new ImageFilters { Favorite = true, MinRating = 3 };
        var active = baseFilters with { Scope = MediaLibraryScope.Active };
        var excluded = baseFilters with { Scope = MediaLibraryScope.Excluded };
        Assert.NotEqual(active.Fingerprint(), excluded.Fingerprint());
    }
}
