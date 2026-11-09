using System.Globalization;
using MedTest.Core;

namespace MedTest.Tests;

public class NumbersTests
{
    [Theory]
    [InlineData("6,4", 6.4)] [InlineData("6.4", 6.4)] [InlineData(" 152 ", 152)] [InlineData("5,0", 5.0)]
    [InlineData("1 234,5", 1234.5)] [InlineData("−3,2", -3.2)] [InlineData("-0.25", -0.25)] [InlineData("0", 0)]
    public void Comma_and_point_are_decimal_separators(string text, double expected)
    {
        foreach (var culture in new[] { "ru-RU", "en-US", "de-DE" })
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.True(Numbers.TryParse(text, out var v), culture + " " + text);
            Assert.Equal((decimal)expected, v);
        }
    }

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("1,2,3")] [InlineData("6,4 ммоль")] [InlineData("1e3")]
    public void Not_a_number(string text) => Assert.False(Numbers.TryParse(text, out _));
}
