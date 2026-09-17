using System.Text.Json;
using System.Security.Cryptography;

namespace Ralven.UpdateRuntime;

public sealed record UpdateTransaction(
    string Id,
    string PreviousVersion,
    string CandidateVersion,
    DateTimeOffset CreatedAtUtc,
    string Nonce,
    DateTimeOffset? CandidateLaunchedAtUtc = null);

/// <summary>Durable recovery record; rollback never accepts an arbitrary version.</summary>
public sealed class UpdateRecoveryJournal
{
    private readonly string path;
    public UpdateRecoveryJournal(string runtimeRoot) =>
        path = Path.Combine(UpdatePathSafety.EnsureNoReparsePoints(runtimeRoot), "recovery.json");

    public UpdateTransaction Begin(string previousVersion, string candidateVersion)
    {
        if (!Version.TryParse(previousVersion, out var previous)
            || !Version.TryParse(candidateVersion, out var candidate)
            || candidate <= previous)
            throw new ArgumentException("Transação de atualização inválida.");
        var transaction = new UpdateTransaction(Guid.NewGuid().ToString("N"), previousVersion, candidateVersion, DateTimeOffset.UtcNow, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Write(transaction);
        return transaction;
    }

    public UpdateTransaction Read()
    {
        UpdatePathSafety.EnsureNoReparsePoints(path);
        var transaction = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Journal de recuperação inválido.");
        if (!UpdateTransactionIdentity.IsValid(transaction.Id, transaction.Nonce)
            || !Version.TryParse(transaction.PreviousVersion, out var previous)
            || !Version.TryParse(transaction.CandidateVersion, out var candidate)
            || candidate <= previous)
            throw new InvalidDataException("Journal de recuperação inválido.");
        return transaction;
    }

    public UpdateTransaction MarkCandidateLaunched(UpdateTransaction transaction)
    {
        var updated = transaction with { CandidateLaunchedAtUtc = DateTimeOffset.UtcNow };
        Write(updated);
        return updated;
    }

    public bool TryRead(out UpdateTransaction transaction)
    {
        transaction = null!;
        UpdatePathSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return false;
        try
        {
            transaction = Read();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            try { File.Move(path, $"{path}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", false); }
            catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException) { }
            return false;
        }
        // A transient read failure (another process mid-File.Replace, a short-lived
        // AV lock) does not mean the journal is corrupt: leave the file alone so the
        // next reconciliation attempt can read it, instead of quarantining a healthy
        // journal and losing the recovery transaction.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Complete()
    {
        try
        {
            UpdatePathSafety.EnsureNoReparsePoints(path);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void Write(UpdateTransaction transaction)
    {
        UpdatePathSafety.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UpdatePathSafety.EnsureNoReparsePoints(path);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(transaction));
    }
}
