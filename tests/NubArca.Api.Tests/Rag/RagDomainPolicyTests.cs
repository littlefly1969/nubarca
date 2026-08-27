using NubArca.Api.Assistant;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The domain policy, and the intersection of that policy with model trust.
//
// This is the file that decides where evidence may travel, so it is asserted
// directly rather than inferred from what a retrieval happened to return: a test
// that only checks "the repository chunk did not appear in the prompt" passes
// for the wrong reason on any day retrieval finds nothing.
public sealed class RagDomainPolicyTests
{
    private static readonly IRagDomainRegistry Registry = RagDomainRegistry.Instance;

    [Fact]
    public void ProductHelp_IsPublicSystemDomain()
    {
        var domain = Registry.GetRequired(RagDomains.ProductHelp);

        Assert.Equal(RagDomainScope.System, domain.Scope);
        Assert.Equal(RagPrivacyClass.Public, domain.PrivacyClass);
        Assert.False(domain.RequiresOwner);
        Assert.True(domain.ExternalGenerationAllowed);
    }

    [Fact]
    public void Repository_IsSystemInternalDomain()
    {
        var domain = Registry.GetRequired(RagDomains.NubArcaRepository);

        Assert.Equal(RagDomainScope.System, domain.Scope);
        Assert.Equal(RagPrivacyClass.SystemInternal, domain.PrivacyClass);
        Assert.False(domain.RequiresOwner);

        // Deliberate, and the single most load-bearing assertion in this file.
        // NubArca is public on GitHub TODAY. That is a fact about this month's
        // hosting, not a property of the domain: the same code has to stay
        // correct for an installation carrying local patches, for a private
        // fork, and for whatever system-internal domain is added next.
        Assert.False(domain.ExternalGenerationAllowed);
    }

    [Fact]
    public void External_CannotUseRepositoryDomain()
    {
        var repository = Registry.GetRequired(RagDomains.NubArcaRepository);

        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.External, repository));
        Assert.Equal(
            RagFailureReasons.DomainNotAllowed,
            AssistantRagPolicy.Refuse(
                AssistantModelTrust.External, repository, Array.Empty<RagEvidence>()));
    }

    [Fact]
    public void External_CanUseProductHelp()
    {
        var help = Registry.GetRequired(RagDomains.ProductHelp);

        Assert.True(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.External, help));
        Assert.Null(AssistantRagPolicy.Refuse(
            AssistantModelTrust.External, help, new[] { Evidence(RagDomainKey.ProductHelp) }));
    }

    [Fact]
    public void LocalTrusted_CanUseRepositoryDomain()
    {
        var repository = Registry.GetRequired(RagDomains.NubArcaRepository);

        Assert.True(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.LocalTrusted, repository));
        Assert.Null(AssistantRagPolicy.Refuse(
            AssistantModelTrust.LocalTrusted, repository,
            new[] { Evidence(RagDomainKey.NubArcaRepository) }));
    }

    [Fact]
    public void ManagedLocal_GetsNothing()
    {
        // The enum value exists so the shape does not have to change when a
        // NubArca-owned runtime arrives. It is answered as "nothing" rather
        // than as "LocalTrusted, but more", so whoever adds that runtime has to
        // state its policy rather than inherit one written before it existed.
        foreach (var domain in Registry.List())
        {
            Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.ManagedLocal, domain));
        }
    }

    [Fact]
    public void UnknownDomain_FailsClosed()
    {
        Assert.False(Registry.TryGet("private-library", out _));
        Assert.False(Registry.TryGet(string.Empty, out _));
        Assert.False(Registry.TryGet("PRODUCT-HELP", out _));  // keys are ordinal
        Assert.Throws<RagDomainUnknownException>(() => Registry.GetRequired("nope"));
    }

    [Fact]
    public void DatabaseMetadata_CannotOverrideCodePrivacyPolicy()
    {
        // The policy is a compiled table. A hand-edited row, a careless admin
        // endpoint or a backup restored from a fork cannot turn SystemInternal
        // into Public, because there is no statement to edit — only a commit to
        // review.
        //
        // Asserted three ways, because "cannot be changed at runtime" is a
        // property of the whole class rather than of any one member.

        // 1. Every property is init-only: a definition cannot be mutated after
        //    construction, so nothing can hand one out and then widen it.
        foreach (var property in typeof(RagDomainDefinition).GetProperties())
        {
            if (!property.CanWrite) continue;
            Assert.Contains(
                typeof(System.Runtime.CompilerServices.IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }

        // 2. The registry only reads. There is no Add, Set, Update or Register.
        Assert.DoesNotContain(
            typeof(RagDomainRegistry).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly),
            m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                 || m.Name.StartsWith("Add", StringComparison.Ordinal)
                 || m.Name.StartsWith("Register", StringComparison.Ordinal)
                 || m.Name.StartsWith("Remove", StringComparison.Ordinal));

        // 3. Nothing on this path reads configuration or a database. Asserted on
        //    the source, because the property is an ABSENCE — a registry that
        //    consulted a table would satisfy every behavioural test above.
        var source = File.ReadAllText(Path.Combine(
            RagTestHarness.RepositoryRoot(),
            "src/NubArca.Api/Rag/Domains/RagDomainRegistry.cs"));
        Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IOptions", source, StringComparison.Ordinal);

        // …and two reads return the same objects, so there is no per-call
        // resolution that could ever differ.
        Assert.Same(Registry.GetRequired(RagDomains.ProductHelp), RagDomainRegistry.ProductHelp);
        Assert.Same(Registry.List(), Registry.List());
    }

    [Fact]
    public void The_Owner_Private_Domain_Is_UserDocuments_And_Every_Field_Is_Restrictive()
    {
        // `user-documents` exists as POLICY. Its definition is the whole
        // statement — there is no configuration key, database column or client
        // parameter that can soften any of these four fields.
        var owned = Assert.Single(
            Registry.List().Where(d => d.PrivacyClass == RagPrivacyClass.OwnerPrivate));

        Assert.Equal(RagDomains.UserDocuments, owned.Key);
        Assert.Equal(RagDomainScope.Owner, owned.Scope);
        Assert.True(owned.RequiresOwner);
        Assert.False(owned.ExternalGenerationAllowed);

        // Every OTHER domain is installation-wide and needs no owner, so the
        // owner requirement cannot be satisfied by accident somewhere else.
        Assert.All(Registry.List().Where(d => d.Key != RagDomains.UserDocuments), d =>
        {
            Assert.Equal(RagDomainScope.System, d.Scope);
            Assert.NotEqual(RagPrivacyClass.OwnerPrivate, d.PrivacyClass);
            Assert.False(d.RequiresOwner);
        });
    }

    [Fact]
    public void No_Model_May_Yet_Be_Grounded_On_Owner_Private_Knowledge()
    {
        // Registering the domain does NOT activate it. There is no private
        // Assistant operation and no owner-scoped retrieval path yet, so the
        // gate refuses owner-private evidence at every trust level — including
        // LocalTrusted, which will be the one allowed once the operation that
        // derives the owner server-side exists.
        var owned = RagDomainRegistry.UserDocuments;

        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.External, owned));
        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.LocalTrusted, owned));

        // …and a definition somebody wrote optimistically stays refused, because
        // the owner-scope check runs before the trust switch.
        var optimistic = owned with { ExternalGenerationAllowed = true };
        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.External, optimistic));
        Assert.False(AssistantRagPolicy.MayGroundOn(AssistantModelTrust.LocalTrusted, optimistic));
    }

    [Fact]
    public void OwnerScopedRetrieval_RefusesAQueryWithNoOwner()
    {
        // The structural half of the same statement, and the one that survives a
        // future caller: retrieval refuses before it reads anything, so a
        // forgotten owner is a rejected request rather than a broad answer.
        Assert.True(Registry.GetRequired(RagDomains.UserDocuments).RequiresOwner);
        Assert.Null(new RagQuery(RagDomainKey.UserDocuments, "qualsiasi domanda", 5, 4000).OwnerUserId);
    }

    [Fact]
    public void Evidence_From_Another_Domain_Is_Refused_Even_When_Both_Are_Allowed()
    {
        // Both domains are readable by a LocalTrusted model. Retrieval still
        // must not hand a caller that asked for `product-help` a chunk stamped
        // `nubarca-repository`: the requested domain is what the operation was
        // reviewed against.
        var help = Registry.GetRequired(RagDomains.ProductHelp);

        Assert.Equal(
            RagFailureReasons.DomainNotAllowed,
            AssistantRagPolicy.Refuse(
                AssistantModelTrust.LocalTrusted, help,
                new[] { Evidence(RagDomainKey.ProductHelp), Evidence(RagDomainKey.NubArcaRepository) }));
    }

    private static RagEvidence Evidence(RagDomainKey domain) => new(
        Id: "x#1", Domain: domain, Path: "p", Title: "t", Section: "s", Text: "body",
        Feature: "f", SourceKind: RagSourceKinds.UserGuide, Audience: RagAudiences.User,
        Intent: RagIntents.HowTo, Language: RagLanguages.Italian, Score: 1.0);
}
