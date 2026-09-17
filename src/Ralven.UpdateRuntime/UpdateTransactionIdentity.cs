namespace Ralven.UpdateRuntime;

/// <summary>
/// Forma exigida do par identificador/nonce de uma transação de atualização.
/// </summary>
/// <remarks>
/// É esta verificação que impede um recibo de saúde forjado de confirmar uma
/// versão candidata arbitrária. O journal de recuperação e o repositório de
/// recibos precisam concordar exatamente sobre o que aceitam, e cada um
/// mantinha sua própria cópia da regra.
/// </remarks>
internal static class UpdateTransactionIdentity
{
    private const int IdHexLength = 32;
    private const int NonceHexLength = 64;

    public static bool IsValid(string? id, string? nonce) =>
        IsHex(id, IdHexLength) && IsHex(nonce, NonceHexLength);

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);
}
