using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ralven.App.Services;
using Ralven.Contracts;
using Ralven.UpdateRuntime;

namespace Ralven.App.Services;

public sealed class SignedManifestUpdateService : IReleaseUpdateService, IDisposable
{
    private readonly HttpClient client;
    private readonly byte[] publicKey;
    private readonly string updatesRoot;
    private readonly VersionFloorStore versionFloor;
    private readonly UpdaterDiagnostics diagnostics;
    private readonly string dataRoot;
    private readonly ReleasePackageKind packageKind;
    private readonly Uri manifestUri;

    public SignedManifestUpdateService(ReleasePackageKind packageKind = ReleasePackageKind.Runtime)
        : this(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions =
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                },
            },
            AppDataPaths.Root,
            packageKind)
    {
    }

    internal SignedManifestUpdateService(
        HttpMessageHandler handler,
        string dataRoot,
        ReleasePackageKind packageKind = ReleasePackageKind.Runtime)
    {
        this.dataRoot = dataRoot;
        this.packageKind = packageKind;
        manifestUri = new Uri(
            packageKind == ReleasePackageKind.Runtime
                ? "https://vemryx.com/Ralven/releases/runtime-manifest.json"
                : "https://vemryx.com/Ralven/releases/installer-manifest.json");
        client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ralven-Updater", "2.0"));
        updatesRoot = Path.Combine(dataRoot, "Updates");
        versionFloor = new VersionFloorStore(dataRoot);
        diagnostics = new UpdaterDiagnostics(dataRoot);
        using var keyStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Ralven.App.Assets.update-manifest-public-key.pem")
            ?? throw new InvalidOperationException("A chave pública de releases não foi incorporada.");
        using var reader = new StreamReader(keyStream);
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(reader.ReadToEnd());
        publicKey = verifier.ExportSubjectPublicKeyInfo();
    }

    public async Task<ReleaseUpdate?> CheckForUpdateAsync(
        StableSemanticVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        try { return await CheckForUpdateCoreAsync(currentVersion, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await RecordFailureAsync("manifest", currentVersion.CoreVersion, currentVersion.CoreVersion, exception);
            throw;
        }
    }

    private async Task<ReleaseUpdate?> CheckForUpdateCoreAsync(
        StableSemanticVersion currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return null;
        if (response.StatusCode != HttpStatusCode.OK
            || response.RequestMessage?.RequestUri is not { } effectiveUri
            || !effectiveUri.Equals(manifestUri))
            throw new UpdateSecurityException($"A fonte assinada respondeu com HTTP {(int)response.StatusCode}.", UpdaterEventCodes.ManifestSourceRejected);
        if (response.Content.Headers.ContentLength is > 65_536)
            throw new UpdateSecurityException("O manifesto assinado excede 64 KiB.", UpdaterEventCodes.ManifestTooLarge);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (limited.Length + read > 65_536) throw new UpdateSecurityException("O manifesto assinado excede 64 KiB.", UpdaterEventCodes.ManifestTooLarge);
            limited.Write(buffer, 0, read);
        }
        var manifestBytes = limited.ToArray();
        using (var document = JsonDocument.Parse(manifestBytes))
        {
            var allowed = new HashSet<string>([
                "channel", "version", "minimumAllowedVersion", "packageUrl",
                "packageSha256", "packageSizeBytes", "signatureBase64"
            ], StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != allowed.Count
                || document.RootElement.EnumerateObject().Any(property => !allowed.Remove(property.Name))
                || allowed.Count != 0)
                throw new UpdateSecurityException("O contrato do manifesto assinado é inválido.", UpdaterEventCodes.ManifestSchemaInvalid);
        }
        var manifest = JsonSerializer.Deserialize<SignedReleaseManifest>(
            manifestBytes,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new UpdateSecurityException("O manifesto assinado é inválido.", UpdaterEventCodes.ManifestSchemaInvalid);
        var floor = StableSemanticVersion.Parse(versionFloor.Read(currentVersion.CoreVersion));
        var highest = floor.CompareTo(currentVersion) > 0 ? floor : currentVersion;
        try { ReleaseTrustPolicy.Verify(manifest, publicKey, highest.CoreVersion); }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            throw new UpdateSecurityException(exception.Message, UpdaterEventCodes.ManifestTrustInvalid);
        }
        var version = StableSemanticVersion.Parse(manifest.Version);
        if (version.CompareTo(currentVersion) <= 0) return null;
        var uri = new Uri(manifest.PackageUrl);
        ValidateDownloadUri(uri, version);
        var assetName = packageKind == ReleasePackageKind.Runtime
            ? $"Ralven-Runtime-{version.CoreVersion}-win-x64.zip"
            : $"Ralven-Setup-{version.CoreVersion}-win-x64.exe";
        return new ReleaseUpdate(
            version,
            $"v{version.CoreVersion}",
            assetName,
            uri,
            manifest.PackageSizeBytes,
            manifest.PackageSha256,
            new Uri("https://vemryx.com/Ralven/"));
    }

    public async Task<DownloadedUpdate> DownloadUpdateAsync(
        ReleaseUpdate update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try { return await DownloadUpdateCoreAsync(update, progress, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await RecordFailureAsync("download", null, update.Version.CoreVersion, exception);
            throw;
        }
    }

    private async Task<DownloadedUpdate> DownloadUpdateCoreAsync(
        ReleaseUpdate update,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        UpdatePathSafety.EnsureNoReparsePoints(updatesRoot);
        PruneStaleDownloads(update.Version.CoreVersion);
        var directory = Path.Combine(updatesRoot, update.Version.CoreVersion);
        Directory.CreateDirectory(directory);
        UpdatePathSafety.EnsureNoReparsePoints(directory);
        var finalPath = Path.Combine(directory, update.AssetName);
        if (await MatchesAsync(finalPath, update, cancellationToken))
            return new DownloadedUpdate(update.Version, finalPath, update.SizeBytes, update.Sha256Hex, true);

        var temporary = finalPath + $".{Guid.NewGuid():N}.part";
        try
        {
            using var response = await SendDownloadAsync(
                update.DownloadUri,
                update.Version,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UpdateSecurityException($"O download respondeu com HTTP {(int)response.StatusCode}.", UpdaterEventCodes.PackageResponseRejected);
            if (response.Content.Headers.ContentLength is long length && length != update.SizeBytes)
                throw new UpdateSecurityException("O tamanho HTTP do pacote difere do manifesto.", UpdaterEventCodes.PackageSizeMismatch);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            // O handle do .part precisa estar fechado antes do File.Move: ele é
            // aberto com FileShare.None, então um `await using` de método inteiro
            // deixaria o arquivo em uso justamente na hora de movê-lo.
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(65_536);
                long total = 0;
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        total = checked(total + read);
                        if (total > update.SizeBytes) throw new UpdateSecurityException("O pacote excede o tamanho assinado.", UpdaterEventCodes.PackageSizeMismatch);
                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report(new UpdateDownloadProgress(total, update.SizeBytes));
                    }
                    if (total != update.SizeBytes
                        || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateSecurityException("A integridade do pacote baixado falhou.", UpdaterEventCodes.PackageHashMismatch);
                    await destination.FlushAsync(cancellationToken);
                    destination.Flush(true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, true); }
            }
            UpdatePathSafety.EnsureNoReparsePoints(finalPath);
            File.Move(temporary, finalPath, true);
            return new DownloadedUpdate(update.Version, finalPath, update.SizeBytes, update.Sha256Hex, false);
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => client.Dispose();

    private Task RecordFailureAsync(string stage, string? previous, string candidate, Exception exception) =>
        diagnostics.RecordAsync(
            new UpdaterEvent(
                Guid.NewGuid().ToString("N"), stage, "failed", Classify(exception),
                previous, candidate, UpdaterDiagnostics.ResolveEnvironment()),
            exception.ToString(),
            telemetryAuthorized: UpdaterDiagnostics.IsTelemetryAuthorized(dataRoot));

    private static string Classify(Exception exception) => exception switch
    {
        UpdateSecurityException security => security.DiagnosticCode,
        JsonException or FormatException => UpdaterEventCodes.ManifestSchemaInvalid,
        CryptographicException => UpdaterEventCodes.ManifestTrustInvalid,
        HttpRequestException => UpdaterEventCodes.NetworkFailed,
        TaskCanceledException or TimeoutException => UpdaterEventCodes.RequestTimedOut,
        IOException => UpdaterEventCodes.LocalIoFailed,
        _ => UpdaterEventCodes.Unexpected,
    };

    private async Task<HttpResponseMessage> SendDownloadAsync(
        Uri initial,
        StableSemanticVersion version,
        CancellationToken token)
    {
        var current = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateDownloadUri(current, version);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 5) throw new UpdateSecurityException("Redirecionamento de download inválido.", UpdaterEventCodes.PackageRedirectRejected);
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new UpdateSecurityException("Redirecionamentos demais.", UpdaterEventCodes.PackageRedirectRejected);
    }

    private void ValidateDownloadUri(Uri uri, StableSemanticVersion version)
    {
        var expectedPath = packageKind == ReleasePackageKind.Runtime
            ? $"/Ralven/releases/v{version.CoreVersion}/Ralven-Runtime-{version.CoreVersion}-win-x64.zip"
            : $"/Ralven/releases/v{version.CoreVersion}/Ralven-Setup-{version.CoreVersion}-win-x64.exe";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.Host.Equals("vemryx.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new UpdateSecurityException("O download saiu da rota oficial permitida.", UpdaterEventCodes.PackageSourceRejected);
    }

    private static async Task<bool> MatchesAsync(string path, ReleaseUpdate update, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != update.SizeBytes) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
            .Equals(update.Sha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneStaleDownloads(string currentVersion)
    {
        try
        {
            UpdatePathSafety.EnsureNoReparsePoints(updatesRoot);
            foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
            {
                var version = Path.GetFileName(directory);
                if (version.Equals(currentVersion, StringComparison.OrdinalIgnoreCase)
                    || !Version.TryParse(version, out _)) continue;
                try
                {
                    UpdatePathSafety.EnsureNoReparsePoints(directory);
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A próxima atualização tenta novamente a pasta bloqueada.
                }
            }
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Cache velho nunca pode impedir o download verificado atual.
        }
    }
}

public enum ReleasePackageKind
{
    Runtime,
    Installer,
}
