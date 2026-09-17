using System.Net.Http;
using System.Net.Http.Json;
using Ralven.Contracts;

namespace Ralven.App.Services;

/// <summary>
/// Sends a bug report to the Cloudflare Worker's <c>/bugs</c> route (D1 only
/// -- there is no attachment/screenshot support and no R2 dependency).
/// Validation mirrors what the old service enforced client-side; the Worker
/// re-validates everything server-side regardless (see
/// <c>infra/cloudflare-worker/src/bugReports/</c>).
/// </summary>
public sealed class CloudflareBugReportService : IBugReportService
{
    private const int MaxCategoryLength = 60;
    private const int MaxTechnicalSummaryLength = 512;
    private const int MaxEmailLength = 254;

    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "optimization", "games", "windows", "interface", "crash", "other"
    };
    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly string environment;
    private readonly ILocalizationService localization;

    public CloudflareBugReportService(Uri endpoint, string environment, ILocalizationService? localization = null)
        : this(SharedClient, endpoint, environment, localization)
    {
    }

    internal CloudflareBugReportService(
        HttpClient httpClient,
        Uri endpoint,
        string environment,
        ILocalizationService? localization = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        this.environment = environment;
        this.localization = localization ?? LocalizationService.Current;
    }

    public async Task<BugReportSendResult> SendAsync(
        BugReportSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ValidateSubmission(submission);

        var payload = new
        {
            reportId = submission.ReportId.ToString("D"),
            category = submission.Category,
            bugCode = submission.BugCode.ToString(),
            summary = submission.Summary.Trim(),
            description = submission.Description.Trim(),
            appVersion = submission.AppVersion,
            profile = submission.Profile,
            technicalSummary = submission.TechnicalSummary,
            email = submission.Email,
            logText = submission.LogText,
            environment
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new BugReportSendResult(
                false,
                localization.GetString("BugReport.Service.Unconfirmed"));
        }

        using (response)
        {
            if ((int)response.StatusCode == 429)
            {
                return new BugReportSendResult(false, localization.GetString("BugReport.Service.RateLimited"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new BugReportSendResult(
                    false,
                    localization.Format("BugReport.Service.HttpError", (int)response.StatusCode));
            }

            return new BugReportSendResult(true, localization.GetString("BugReport.Service.Accepted"));
        }
    }

    private static void ValidateSubmission(BugReportSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.ReportId == Guid.Empty)
        {
            throw new ArgumentException("O relato precisa de um identificador.", nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.Category)
            || submission.Category.Length > MaxCategoryLength
            || !AllowedCategories.Contains(submission.Category))
        {
            throw new ArgumentException("Escolha um tipo de problema válido.", nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.Summary)
            || submission.Summary.Trim().Length is < 5 or > 120)
        {
            throw new ArgumentException("O resumo deve ter entre 5 e 120 caracteres.", nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.Description)
            || submission.Description.Trim().Length is < 20 or > 8000)
        {
            throw new ArgumentException("A descrição deve ter entre 20 e 8.000 caracteres.", nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.AppVersion)
            || submission.AppVersion.Length > 32
            || submission.AppVersion.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("A versão do app é inválida.", nameof(submission));
        }

        if (string.IsNullOrWhiteSpace(submission.Profile) || submission.Profile.Length > 32)
        {
            throw new ArgumentException("O perfil do relato é inválido.", nameof(submission));
        }

        if (submission.BugCode == BugCode.Unknown || !Enum.IsDefined(submission.BugCode))
        {
            throw new ArgumentException("O código do bug é inválido.", nameof(submission));
        }

        if (submission.TechnicalSummary?.Length > MaxTechnicalSummaryLength)
        {
            throw new ArgumentException("As informações técnicas excedem o limite.", nameof(submission));
        }

        if (submission.Email is { Length: > 0 } email
            && (email.Length > MaxEmailLength || !AccountValidation.IsValidEmail(email)))
        {
            throw new ArgumentException("O e-mail informado é inválido.", nameof(submission));
        }

        if (submission.LogText is { } logText
            && System.Text.Encoding.UTF8.GetByteCount(logText) > BugReportSubmission.MaxLogTextBytes)
        {
            throw new ArgumentException("O log excede o limite de 100 KB.", nameof(submission));
        }
    }

    private static Uri ValidateEndpoint(Uri value) =>
        CloudflareTransportDefaults.ValidateHttpsEndpoint(value, "Endpoint de relato inválido.");

    private static HttpClient CreateClient() => CloudflareTransportDefaults.CreateClient(TimeSpan.FromSeconds(30));
}
