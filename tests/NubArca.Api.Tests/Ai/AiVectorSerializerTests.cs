using NubArca.Api.Ai;

namespace NubArca.Api.Tests.Ai;

public sealed class AiVectorSerializerTests
{
    private readonly AiVectorSerializer _serializer = new();

    [Fact]
    public void Serialize_Then_Deserialize_Roundtrips_Exactly()
    {
        var vector = new[] { 1.5f, -2.25f, 0f, 3.0f, 0.125f };

        var bytes = _serializer.Serialize(vector);
        var roundtripped = _serializer.Deserialize(bytes);

        Assert.Equal(vector.Length * sizeof(float), bytes.Length);
        Assert.Equal(vector, roundtripped); // float32 packing is exact
    }

    [Fact]
    public void GetDimension_Returns_Component_Count()
    {
        var bytes = _serializer.Serialize(new[] { 1f, 2f, 3f });
        Assert.Equal(3, _serializer.GetDimension(bytes));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Serialize_Rejects_Non_Finite_Values(float bad)
    {
        Assert.Throws<ArgumentException>(() => _serializer.Serialize(new[] { 1f, bad, 2f }));
    }

    [Fact]
    public void Serialize_Rejects_Empty_Vector()
    {
        Assert.Throws<ArgumentException>(() => _serializer.Serialize(Array.Empty<float>()));
    }

    [Fact]
    public void Serialize_With_Expected_Dimension_Catches_Mismatch()
    {
        Assert.Throws<ArgumentException>(() => _serializer.Serialize(new[] { 1f, 2f, 3f }, expectedDimension: 4));
    }

    [Fact]
    public void Deserialize_With_Expected_Dimension_Catches_Mismatch()
    {
        var bytes = _serializer.Serialize(new[] { 1f, 2f, 3f });
        Assert.Throws<ArgumentException>(() => _serializer.Deserialize(bytes, expectedDimension: 8));
    }

    [Fact]
    public void Deserialize_Rejects_Length_Not_Multiple_Of_Four()
    {
        Assert.Throws<ArgumentException>(() => _serializer.Deserialize(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Normalize_Produces_Unit_Length_Vector()
    {
        var normalized = _serializer.Normalize(new[] { 3f, 4f }); // norm 5

        var norm = Math.Sqrt(normalized.Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, precision: 5);
        Assert.Equal(0.6f, normalized[0], precision: 5);
        Assert.Equal(0.8f, normalized[1], precision: 5);
    }

    [Fact]
    public void Normalize_Rejects_Non_Finite_Values()
    {
        Assert.Throws<ArgumentException>(() => _serializer.Normalize(new[] { 1f, float.NaN }));
    }

    [Fact]
    public void Normalize_Returns_Zero_Vector_Unchanged()
    {
        var normalized = _serializer.Normalize(new[] { 0f, 0f, 0f });
        Assert.All(normalized, v => Assert.Equal(0f, v));
    }
}
