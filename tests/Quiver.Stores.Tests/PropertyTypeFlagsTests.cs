using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public sealed class PropertyTypeFlagsTests
{
    [Theory]
    [InlineData(PropertyValueType.Bool,   PropertyTypeFlags.Bool)]
    [InlineData(PropertyValueType.Int32,  PropertyTypeFlags.Int32)]
    [InlineData(PropertyValueType.Int64,  PropertyTypeFlags.Int64)]
    [InlineData(PropertyValueType.Double, PropertyTypeFlags.Double)]
    [InlineData(PropertyValueType.String, PropertyTypeFlags.String)]
    [InlineData(PropertyValueType.Bytes,  PropertyTypeFlags.Bytes)]
    public void ToFlags_maps_every_PropertyValueType_to_its_bit(
        PropertyValueType type, PropertyTypeFlags expected)
    {
        type.ToFlags().Should().Be(expected);
    }

    [Fact]
    public void ToFlags_handles_invalid_input_as_None()
    {
        ((PropertyValueType)0).ToFlags().Should().Be(PropertyTypeFlags.None);
        ((PropertyValueType)200).ToFlags().Should().Be(PropertyTypeFlags.None);
    }

    [Theory]
    [InlineData(PropertyValueType.Int32,  true)]
    [InlineData(PropertyValueType.Int64,  true)]
    [InlineData(PropertyValueType.Double, true)]
    [InlineData(PropertyValueType.Bool,   false)]
    [InlineData(PropertyValueType.String, false)]
    [InlineData(PropertyValueType.Bytes,  false)]
    public void IsCompatibleWith_Numeric_only_matches_numeric_types(
        PropertyValueType type, bool expected)
    {
        type.IsCompatibleWith(PropertyTypeFlags.Numeric).Should().Be(expected);
    }

    [Fact]
    public void Composite_masks_are_unions_of_their_bits()
    {
        PropertyTypeFlags.Numeric.Should().Be(
            PropertyTypeFlags.Int32 | PropertyTypeFlags.Int64 | PropertyTypeFlags.Double);

        PropertyTypeFlags.Scalar.Should().Be(
            PropertyTypeFlags.Bool   | PropertyTypeFlags.Int32  | PropertyTypeFlags.Int64
          | PropertyTypeFlags.Double | PropertyTypeFlags.String | PropertyTypeFlags.Bytes);

        PropertyTypeFlags.Comparable.Should().Be(
            PropertyTypeFlags.Numeric | PropertyTypeFlags.String);

        (PropertyTypeFlags.Array & PropertyTypeFlags.Scalar).Should().Be(PropertyTypeFlags.None);
    }
}
