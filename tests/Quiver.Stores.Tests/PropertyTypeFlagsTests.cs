using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public sealed class PropertyTypeFlagsTests
{
    [Theory]
    [InlineData((byte)PropertyValueType.Bool,   PropertyTypeFlags.Bool)]
    [InlineData((byte)PropertyValueType.Int32,  PropertyTypeFlags.Int32)]
    [InlineData((byte)PropertyValueType.Int64,  PropertyTypeFlags.Int64)]
    [InlineData((byte)PropertyValueType.Double, PropertyTypeFlags.Double)]
    [InlineData((byte)PropertyValueType.String, PropertyTypeFlags.String)]
    [InlineData((byte)PropertyValueType.Bytes,  PropertyTypeFlags.Bytes)]
    public void ToFlags_maps_every_PropertyValueType_to_its_bit(
        byte rawType, PropertyTypeFlags expected)
    {
        var type = (PropertyValueType)rawType;
        type.ToFlags().Should().Be(expected);
    }

    [Fact]
    public void ToFlags_handles_invalid_input_as_None()
    {
        ((PropertyValueType)0).ToFlags().Should().Be(PropertyTypeFlags.None);
        ((PropertyValueType)200).ToFlags().Should().Be(PropertyTypeFlags.None);
    }

    [Theory]
    [InlineData((byte)PropertyValueType.Int32,  true)]
    [InlineData((byte)PropertyValueType.Int64,  true)]
    [InlineData((byte)PropertyValueType.Double, true)]
    [InlineData((byte)PropertyValueType.Bool,   false)]
    [InlineData((byte)PropertyValueType.String, false)]
    [InlineData((byte)PropertyValueType.Bytes,  false)]
    public void IsCompatibleWith_Numeric_only_matches_numeric_types(
        byte rawType, bool expected)
    {
        var type = (PropertyValueType)rawType;
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
