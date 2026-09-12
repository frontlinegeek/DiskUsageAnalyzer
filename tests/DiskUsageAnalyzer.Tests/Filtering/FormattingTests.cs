using DiskUsageAnalyzer.Core.Formatting;
using DiskUsageAnalyzer.Core.Filtering;
using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Tests.Filtering;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    public void SizeBoundaries(long bytes, string expected) => Assert.Equal(expected, SizeFormatter.Format(bytes));

    [Theory]
    [InlineData(" zip ")]
    [InlineData(".ZIP")]
    public void Extension_IsTrimmedAndCaseInsensitive(string extension) => Assert.True(new DiskItemFilter { Extension = extension }.Matches(
        new DiskItem { Name = "file.zip", FullPath = "file.zip", ItemType = DiskItemType.File }));
}
