using Openhold.Core.Properties;

namespace Openhold.Core.Tests;

public class PropertyValuesTests
{
    [Fact]
    public void Text_is_trimmed_and_blank_text_is_skipped()
    {
        Assert.Equal("AXLE ASSEMBLY", PropertyValues.Format("  AXLE ASSEMBLY "));
        Assert.Null(PropertyValues.Format("   "));
        Assert.Null(PropertyValues.Format(null));
    }

    [Fact]
    public void Inventor_placeholder_dates_are_skipped_and_real_dates_are_iso()
    {
        Assert.Null(PropertyValues.Format(new DateTime(1601, 1, 1)));
        Assert.Equal("2024-01-05", PropertyValues.Format(new DateTime(2024, 1, 5)));
        Assert.Equal("2024-01-05 14:30", PropertyValues.Format(new DateTime(2024, 1, 5, 14, 30, 0)));
    }

    [Fact]
    public void Booleans_and_numbers_read_naturally()
    {
        Assert.Equal("Yes", PropertyValues.Format(true));
        Assert.Equal("No", PropertyValues.Format(false));
        Assert.Equal("3.5", PropertyValues.Format(3.5));
        Assert.Equal("42", PropertyValues.Format(42));
    }

    [Fact]
    public void Binary_values_like_thumbnails_are_skipped() =>
        Assert.Null(PropertyValues.Format(new object()));
}
