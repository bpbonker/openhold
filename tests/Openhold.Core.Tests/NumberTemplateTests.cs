using Openhold.Core.Numbering;

namespace Openhold.Core.Tests;

public sealed class NumberTemplateTests
{
    [Fact]
    public void A_check_digit_follows_the_counter()
    {
        var template = NumberTemplate.Parse("P-{seq:000000}-{check}");

        // 572 → 4 is the published Damm example; 104527 is a six-digit part number.
        Assert.Equal(4, Damm.CheckDigit("572"));
        Assert.Equal("P-104527-" + Damm.CheckDigit("104527"), template.Format(104527, NumberContext.For("AU")));
    }

    [Fact]
    public void The_check_digit_catches_typos_and_swapped_digits()
    {
        var good = "104527" + Damm.CheckDigit("104527");
        Assert.True(Damm.IsValid(good));
        // Every single-digit change and every swap of neighbouring digits is caught.
        for (var i = 0; i < good.Length; i++)
        {
            for (var d = '0'; d <= '9'; d++)
            {
                if (d != good[i])
                    Assert.False(Damm.IsValid(good[..i] + d + good[(i + 1)..]));
            }
            if (i + 1 < good.Length && good[i] != good[i + 1])
                Assert.False(Damm.IsValid(good[..i] + good[i + 1] + good[i] + good[(i + 2)..]));
        }
    }

    [Fact]
    public void A_template_has_at_most_one_check_digit() =>
        Assert.Throws<FormatException>(() => NumberTemplate.Parse("P-{seq:0000}-{check}{check}"));
}
