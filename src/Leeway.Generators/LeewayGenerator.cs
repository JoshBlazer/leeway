using Microsoft.CodeAnalysis;

namespace Leeway.Generators;

/// <summary>Scaffold only: emits nothing. Attribute-driven merge code arrives in milestone M5.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class LeewayGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
    }
}
