using NubArca.Api.Ai.NaturalGallery;
using static NubArca.Api.Ai.NaturalGallery.PersonNameResolver;

namespace NubArca.Api.Tests.Ai.NaturalGallery;

// Pure resolution tests (owner snapshot supplied directly — the loader is
// owner-scoped and tested via the endpoint tests). Covers exact/normalized/
// fuzzy/ambiguous/unresolved and confirms invented names never resolve.
public sealed class PersonNameResolverTests
{
    private static readonly PersonRecord Anna = new(Guid.NewGuid(), "Anna", 12);
    private static readonly PersonRecord MarcoRossi = new(Guid.NewGuid(), "Marco Rossi", 30);
    private static readonly PersonRecord MarcoBianchi = new(Guid.NewGuid(), "Marco Bianchi", 8);
    private static readonly PersonRecord Andre = new(Guid.NewGuid(), "André", 5);

    private static readonly PersonRecord[] People = { Anna, MarcoRossi, MarcoBianchi, Andre };

    [Fact]
    public void Exact_Normalized_Match()
    {
        var r = Resolve(People, "anna", PeopleTermModes.Include);
        Assert.Equal(ResolutionStatus.Resolved, r.Status);
        Assert.Equal(Anna.Id, r.PersonId);
    }

    [Fact]
    public void Diacritic_And_Case_Insensitive()
    {
        var r = Resolve(People, "ANDRE", PeopleTermModes.Include);
        Assert.Equal(ResolutionStatus.Resolved, r.Status);
        Assert.Equal(Andre.Id, r.PersonId);
    }

    [Fact]
    public void Ambiguous_First_Name_Requires_Clarification()
    {
        var r = Resolve(People, "Marco", PeopleTermModes.Include);
        Assert.Equal(ResolutionStatus.Ambiguous, r.Status);
        Assert.Null(r.PersonId);
        Assert.Equal(2, r.Candidates.Count);
        // Most-faces first for a TV-friendly default order.
        Assert.Equal(MarcoRossi.Id, r.Candidates[0].Id);
    }

    [Fact]
    public void Full_Name_Resolves_Unambiguously()
    {
        var r = Resolve(People, "Marco Bianchi", PeopleTermModes.Exclude);
        Assert.Equal(ResolutionStatus.Resolved, r.Status);
        Assert.Equal(MarcoBianchi.Id, r.PersonId);
        Assert.Equal(PeopleTermModes.Exclude, r.Mode);
    }

    [Fact]
    public void Typo_Within_Edit_Distance_One_Resolves()
    {
        var r = Resolve(People, "Ana", PeopleTermModes.Include); // 1 deletion from Anna
        Assert.Equal(ResolutionStatus.Resolved, r.Status);
        Assert.Equal(Anna.Id, r.PersonId);
    }

    [Fact]
    public void Invented_Name_Is_Unresolved()
    {
        var r = Resolve(People, "Zenobia", PeopleTermModes.Include);
        Assert.Equal(ResolutionStatus.Unresolved, r.Status);
        Assert.Null(r.PersonId);
        Assert.Empty(r.Candidates);
    }

    [Fact]
    public void Empty_Snapshot_Never_Resolves()
    {
        var r = Resolve(Array.Empty<PersonRecord>(), "Anna", PeopleTermModes.Include);
        Assert.Equal(ResolutionStatus.Unresolved, r.Status);
    }
}
