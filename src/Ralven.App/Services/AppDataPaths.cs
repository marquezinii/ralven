using System.IO;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Raiz dos dados locais mutáveis do aplicativo, em
/// <c>%LOCALAPPDATA%\Ralven</c>. Existe porque o caminho era recomposto em mais
/// de dez lugares e parte deles escrevia <c>"Ralven"</c> literal em vez de
/// <see cref="ProductIdentity.Name"/>, o que faria a renomeação do produto
/// divergir silenciosamente.
/// </summary>
internal static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.Name);

    /// <summary>Combina <paramref name="parts"/> abaixo de <see cref="Root"/>.</summary>
    public static string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var segments = new string[parts.Length + 1];
        segments[0] = Root;
        parts.CopyTo(segments, 1);
        return Path.Combine(segments);
    }
}
