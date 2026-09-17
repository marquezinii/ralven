namespace Ralven.Windows.Actions;

/// <summary>
/// Regras compartilhadas do arquivo de configurações que
/// <see cref="DisplayPreferencesAction"/> e
/// <see cref="LegacyGraphicsPresetAction"/> editam: qual nome de arquivo cada
/// alvo aceita e como um arquivo ainda inexistente é reconhecido. Ficam aqui
/// para que as duas ações não possam divergir em nenhuma das duas regras.
/// </summary>
internal static class GraphicsSettingsFile
{
    /// <summary>Executável que identifica uma instalação do GTA V.</summary>
    public const string GtaVExecutableName = "GTA5.exe";

    private const string FiveMSettingsFileName = "gta5_settings.xml";
    private const string GtaVSettingsFileName = "settings.xml";

    public static string ExpectedFileName(GraphicsSettingsTarget target)
    {
        return target == GraphicsSettingsTarget.FiveM
            ? FiveMSettingsFileName
            : GtaVSettingsFileName;
    }

    /// <summary>
    /// Confirma que o caminho aponta para o arquivo esperado do alvo e devolve
    /// a forma absoluta usada pela ação.
    /// </summary>
    public static string ValidatePath(
        string settingsPath,
        GraphicsSettingsTarget target,
        string targetDescription,
        string parameterName)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        var expectedFileName = ExpectedFileName(target);
        if (!Path.GetFileName(fullPath).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"O alvo {targetDescription} deve apontar para {expectedFileName}.",
                parameterName);
        }

        return fullPath;
    }

    /// <summary>
    /// Sonda a existência do arquivo sem abri-lo. Somente arquivo/diretório
    /// ausente vira <c>false</c>; qualquer outra falha de I/O continua
    /// propagando para o motor transacional tratá-la como erro real.
    /// </summary>
    public static bool Exists(string settingsPath)
    {
        try
        {
            _ = File.GetAttributes(settingsPath);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
