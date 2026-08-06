using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Slice 57 — pure unit tests for the centralized exposure policy. These do
// not exercise any endpoint; they assert that the policy table itself is
// internally consistent so the per-endpoint no-leak scans (which import the
// same lists) cannot silently drift.
public sealed class MetadataExposurePolicyTests
{
    [Fact]
    public void Owner_May_See_OwnerCurated_Fields()
    {
        Assert.True(MetadataExposurePolicy.IsAllowed(
            MetadataFieldSensitivity.OwnerCurated, MetadataAudience.Owner));
    }

    [Theory]
    [InlineData(MetadataFieldSensitivity.InternalOnly, MetadataAudience.Owner)]
    [InlineData(MetadataFieldSensitivity.InternalOnly, MetadataAudience.ShareLinkPublic)]
    [InlineData(MetadataFieldSensitivity.InternalOnly, MetadataAudience.AdminAggregate)]
    [InlineData(MetadataFieldSensitivity.InternalOnly, MetadataAudience.Internal)]
    [InlineData(MetadataFieldSensitivity.Sensitive, MetadataAudience.Owner)]
    [InlineData(MetadataFieldSensitivity.Sensitive, MetadataAudience.ShareLinkPublic)]
    [InlineData(MetadataFieldSensitivity.Sensitive, MetadataAudience.AdminAggregate)]
    [InlineData(MetadataFieldSensitivity.OwnerCurated, MetadataAudience.ShareLinkPublic)]
    [InlineData(MetadataFieldSensitivity.OwnerCurated, MetadataAudience.AdminAggregate)]
    public void Default_Deny_Holds(
        MetadataFieldSensitivity sensitivity, MetadataAudience audience)
    {
        Assert.False(MetadataExposurePolicy.IsAllowed(sensitivity, audience));
    }

    [Fact]
    public void Internal_Needles_Include_Storage_Token_And_Raw_Metadata_Names()
    {
        var needles = MetadataExposurePolicy.InternalOnlyNeedles;

        // A meaningful subset must be present; if any of these disappear
        // from the policy without being replaced, callers' no-leak scans
        // become weaker and that should be a deliberate change.
        Assert.Contains("StorageKey", needles);
        Assert.Contains("storageKey", needles);
        Assert.Contains("BlobObjectId", needles);
        Assert.Contains("OwnerUserId", needles);
        Assert.Contains("PasswordHash", needles);
        Assert.Contains("TokenHash", needles);
        Assert.Contains("RawMetadataJson", needles);
        Assert.Contains("Sha256", needles);
        Assert.Contains("objects/", needles);
    }

    [Fact]
    public void Sensitive_Embedded_Needles_Include_Gps_And_Serials()
    {
        var needles = MetadataExposurePolicy.SensitiveEmbeddedNeedles;

        Assert.Contains("GpsLatitude", needles);
        Assert.Contains("gpsLatitude", needles);
        Assert.Contains("GpsLongitude", needles);
        Assert.Contains("GpsAltitude", needles);
        Assert.Contains("BodySerialNumber", needles);
        Assert.Contains("LensSerialNumber", needles);
        Assert.Contains("Software", needles);
        Assert.Contains("LensMake", needles);
        Assert.Contains("DateTakenOffset", needles);
    }

    [Fact]
    public void Forbidden_In_Responses_Is_Union_Of_Internal_And_Sensitive()
    {
        var forbidden = MetadataExposurePolicy.ForbiddenInResponses.ToHashSet();

        foreach (var n in MetadataExposurePolicy.InternalOnlyNeedles)
        {
            Assert.Contains(n, forbidden);
        }
        foreach (var n in MetadataExposurePolicy.SensitiveEmbeddedNeedles)
        {
            Assert.Contains(n, forbidden);
        }
    }

    [Fact]
    public void ShareLink_Bytes_Include_Embedded_Metadata_Is_Documented_As_True()
    {
        // The constant is part of the public policy. If a future slice ever
        // adds metadata stripping/redaction on the public download path,
        // this flag flips to false and the share-link UI warning becomes
        // unnecessary. Today it stays true.
        Assert.True(MetadataExposurePolicy.ShareLinkBytesIncludeEmbeddedMetadata);
    }
}
