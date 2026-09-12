using DiskUsageAnalyzer.Core.Filtering;
using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Tests.Filtering;

public sealed class DiskItemFilterTests
{
    [Fact]
    public void Matches_FiltersBySearchText()
    {
        var item = CreateItem("archive.zip", @"C:\temp\archive.zip", 10);
        var filter = new DiskItemFilter { SearchText = "archive" };

        Assert.True(filter.Matches(item));
    }

    [Fact]
    public void Matches_FiltersByMinimumSize()
    {
        var item = CreateItem("archive.zip", @"C:\temp\archive.zip", 10);
        var filter = new DiskItemFilter { MinimumSizeBytes = 11 };

        Assert.False(filter.Matches(item));
    }

    [Fact]
    public void Matches_FiltersByExtension()
    {
        var item = CreateItem("archive.zip", @"C:\temp\archive.zip", 10);
        var filter = new DiskItemFilter { Extension = "zip" };

        Assert.True(filter.Matches(item));
    }

    private static DiskItem CreateItem(string name, string fullPath, long size)
    {
        return new DiskItem
        {
            Name = name,
            FullPath = fullPath,
            ItemType = DiskItemType.File,
            LogicalSizeBytes = size
        };
    }
}
