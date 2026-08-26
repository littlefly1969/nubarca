using NubArca.Api.Assistant;
using Xunit;

namespace NubArca.Api.Tests.Assistant;

// The capability policy, and the distinction the whole slice rests on:
//
//   ELIGIBLE BY TRUST  is not  USED BY A FEATURE.
//
// A LocalTrusted model is eligible for private context. Help does not give it
// any, because Help's operation policy is public product knowledge. Both halves
// are asserted, because a future change that collapsed them would look like a
// simplification and would be a data leak.
public sealed class AssistantCapabilityPolicyTests
{
    [Fact]
    public void External_May_Receive_Public_Product_Context_And_Nothing_Else()
    {
        var capabilities = AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.External);

        Assert.True(capabilities.CanReceivePublicContext);
        Assert.False(capabilities.CanReceivePrivateContext);
        Assert.False(capabilities.CanUsePrivateRag);
        Assert.False(capabilities.CanUseReadTools);
        Assert.False(capabilities.CanProposeActions);
        Assert.False(capabilities.CanUseWriteTools);
        Assert.False(capabilities.CanExecuteWithoutConfirmation);
    }

    [Fact]
    public void LocalTrusted_Is_Eligible_For_Private_Context_And_Read_Tools()
    {
        var capabilities = AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.LocalTrusted);

        Assert.True(capabilities.CanReceivePublicContext);
        Assert.True(capabilities.CanReceivePrivateContext);
        Assert.True(capabilities.CanUsePrivateRag);
        Assert.True(capabilities.CanUseReadTools);
        Assert.True(capabilities.CanProposeActions);
    }

    [Theory]
    [InlineData(AssistantModelTrust.External)]
    [InlineData(AssistantModelTrust.LocalTrusted)]
    [InlineData(AssistantModelTrust.ManagedLocal)]
    public void No_Trust_Level_Grants_Write_Or_Unconfirmed_Execution(AssistantModelTrust trust)
    {
        // Writing is not a trust question. Nothing in NubArca changes because a
        // model suggested it, at any trust level: a proposal is shown to a
        // person, and the person acts.
        var capabilities = AssistantCapabilityPolicy.ForTrust(trust);
        Assert.False(capabilities.CanUseWriteTools);
        Assert.False(capabilities.CanExecuteWithoutConfirmation);
    }

    [Fact]
    public void ManagedLocal_Grants_Nothing_Rather_Than_Inheriting_LocalTrusted()
    {
        // A future runtime has to state its own policy. Inheriting one written
        // before it existed would mean the first version of it shipped with
        // capabilities nobody reviewed against what it actually isolates.
        Assert.Equal(
            AssistantCapabilities.None,
            AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.ManagedLocal));
    }

    [Fact]
    public void No_Profile_Grants_Nothing()
        => Assert.Equal(AssistantCapabilities.None, AssistantCapabilityPolicy.For(null));

    [Fact]
    public void Intersection_Can_Only_Narrow()
    {
        var local = AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.LocalTrusted);
        var external = AssistantCapabilityPolicy.ForTrust(AssistantModelTrust.External);

        // A feature cannot widen what its model is eligible for, in either
        // direction of the operation.
        Assert.Equal(external, local.Intersect(external));
        Assert.Equal(external, external.Intersect(local));
        Assert.Equal(AssistantCapabilities.None, AssistantCapabilities.None.Intersect(local));
    }

    [Theory]
    [InlineData(AssistantModelTrust.External)]
    [InlineData(AssistantModelTrust.LocalTrusted)]
    public void Help_Uses_Public_Product_Context_Only_Whatever_The_Model_Allows(
        AssistantModelTrust trust)
    {
        // THE POINT OF THE SLICE. Configuring a trusted local endpoint makes
        // Help local. It does not make Help able to see anything new.
        var profile = AssistantTextModelContractTests.ExternalProfile() with { Trust = trust };
        var effective = HelpOperationPolicy.Effective(profile);

        Assert.True(effective.CanReceivePublicContext);
        Assert.False(effective.CanReceivePrivateContext);
        Assert.False(effective.CanUsePrivateRag);
        Assert.False(effective.CanUseReadTools);
        Assert.False(effective.CanProposeActions);
        Assert.False(effective.CanUseWriteTools);
        Assert.False(effective.CanExecuteWithoutConfirmation);
    }
}
