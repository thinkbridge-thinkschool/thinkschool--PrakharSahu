namespace EfCorePerformance.Models;

public class BenchmarkRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
}