using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using Xunit;

namespace CarbidePwsh.Tests;

public class CoercionTests
{
    [Theory]
    [InlineData(42, typeof(int), 42)]
    [InlineData(42, typeof(long), 42L)]
    [InlineData(42, typeof(double), 42.0)]
    [InlineData(3.14, typeof(int), 3)]
    public void NumericConversions(object input, Type target, object expected)
    {
        Assert.Equal(expected, Coercion.To(input, target));
    }

    [Fact]
    public void StringToInt()
    {
        Assert.Equal(42, Coercion.To("42", typeof(int)));
    }

    [Fact]
    public void StringToDouble()
    {
        Assert.Equal(3.14, Coercion.To("3.14", typeof(double)));
    }

    [Fact]
    public void StringToIntFailsCleanly()
    {
        Assert.Throws<PwshCoercionException>(() => Coercion.To("not a number", typeof(int)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("anything", true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(0.0, false)]
    public void CoerceToBool(object? input, bool expected)
    {
        Assert.Equal(expected, Coercion.CoerceToBool(input));
    }

    [Fact]
    public void NullToString()
    {
        Assert.Equal("", Coercion.To(null, typeof(string)));
    }

    [Fact]
    public void NumberToString()
    {
        Assert.Equal("3.14", Coercion.To(3.14, typeof(string)));
    }

    [Fact]
    public void BoolToStringCapitalizes()
    {
        Assert.Equal("True", Coercion.FormatAsString(true));
        Assert.Equal("False", Coercion.FormatAsString(false));
    }

    [Fact]
    public void StringToConsoleColor()
    {
        Assert.Equal(ConsoleColor.DarkBlue, Coercion.To("DarkBlue", typeof(ConsoleColor)));
    }

    [Fact]
    public void BoolToInt()
    {
        Assert.Equal(1L, Coercion.ToInt64(true));
        Assert.Equal(0L, Coercion.ToInt64(false));
    }

    [Fact]
    public void NullIsZeroForNumerics()
    {
        Assert.Equal(0.0, Coercion.ToDouble(null));
        Assert.Equal(0L, Coercion.ToInt64(null));
    }
}
