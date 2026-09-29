using Hail.Core.Layout;

namespace Hail.Core.Tests;

public sealed class BoxPlacementTests
{
    [Fact]
    public void Centred_across_and_a_third_of_the_way_down()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        Assert.Equal((620, 344), BoxPlacement.TopLeft(work, 680));
    }

    [Fact]
    public void A_monitor_to_the_left_of_the_primary_has_negative_coordinates()
    {
        var work = new PixelRect(-2560, 0, 0, 1392);
        Assert.Equal((-2560 + 850, 464), BoxPlacement.TopLeft(work, 860));
    }

    [Fact]
    public void A_box_wider_than_the_monitor_starts_at_its_left_edge()
    {
        var work = new PixelRect(100, 0, 700, 600);
        Assert.Equal(100, BoxPlacement.TopLeft(work, 1000).X);
    }
}
