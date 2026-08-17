namespace SlimDep.Cli.Reporters;

/// <summary>Renders one or more <see cref="ScanResult"/> values to a string in a specific format.</summary>
public interface IReporter
{
    string Render(ScanResult result);

    /// <summary>
    /// Renders all results as a single combined output.
    /// The default implementation joins individual <see cref="Render"/> calls with a newline.
    /// Batch formats such as SARIF override this to produce a single well-formed document.
    /// </summary>
    string RenderAll(IReadOnlyList<ScanResult> results) =>
        string.Join(Environment.NewLine, results.Select(Render));
}
