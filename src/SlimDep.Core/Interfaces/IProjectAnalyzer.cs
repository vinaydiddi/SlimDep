namespace SlimDep.Core.Interfaces;

/// <summary>Analyzes a .NET project for unused NuGet package references.</summary>
public interface IProjectAnalyzer
{
    Task<ScanResult> AnalyzeAsync(ScanOptions options);
}
