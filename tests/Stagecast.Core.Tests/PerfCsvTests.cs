using System.Globalization;
using Stagecast.Core.Metrics;

namespace Stagecast.Core.Tests;

public class PerfCsvTests
{
    private static PerfSample Sample(double value = 1.5, double? gpu = 2.5, TimingSnapshot timing = default) => new(
        "steady", 30, value, 2, 3, 120, timing, timing, timing, timing, value, value, timing, value, value, value, gpu, 1, 0);

    [Fact]
    public void Header_and_line_have_the_same_number_of_columns()
    {
        Assert.Equal(PerfCsv.Header.Split(',').Length, PerfCsv.Line(Sample()).Split(',').Length);
    }

    [Fact]
    public void Uses_dot_decimals_under_a_comma_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("id-ID");
            string line = PerfCsv.Line(Sample(1.5));
            Assert.Contains(",1.5,", line);
            Assert.DoesNotContain("1,5", line);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Null_gpu_is_an_empty_column()
    {
        var columns = PerfCsv.Line(Sample(gpu: null)).Split(',');
        int gpu = Array.IndexOf(PerfCsv.Header.Split(','), "gpu_percent");
        Assert.Equal("", columns[gpu]);
    }

    [Fact]
    public void Empty_snapshots_write_zeros_not_NaN()
    {
        string line = PerfCsv.Line(Sample());
        Assert.DoesNotContain("NaN", line);
        Assert.DoesNotContain("Infinity", line);
    }

    [Fact]
    public void Non_finite_values_are_written_as_zero()
    {
        string line = PerfCsv.Line(Sample(double.NaN, double.PositiveInfinity));
        Assert.DoesNotContain("NaN", line);
        Assert.DoesNotContain("Infinity", line);
    }
}
