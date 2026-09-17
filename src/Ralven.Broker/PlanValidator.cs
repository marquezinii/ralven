using System.Diagnostics.CodeAnalysis;
using Ralven.Contracts;
using Ralven.Core.Catalog;
using Ralven.Core.Planning;

namespace Ralven.Broker;

internal sealed record ValidatedPlan(
    OptimizationPlanDto Plan,
    IReadOnlyList<PlannedActionDto> AdministratorActions);

internal sealed class PlanValidator
{
    private static readonly TimeSpan MaximumPlanAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private readonly TimeProvider timeProvider;

    public PlanValidator()
        : this(TimeProvider.System)
    {
    }

    internal PlanValidator(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValidatedPlan Validate(OptimizationPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Require(plan.PlanId != Guid.Empty, "plan-id-invalid", "The plan ID cannot be empty.");
        Require(
            plan.SchemaVersion == ProductIdentity.PlanSchemaVersion,
            "plan-schema-unsupported",
            "The plan schema version is not supported.");
        Require(
            plan.CatalogVersion == ActionCatalog.CurrentVersion,
            "plan-catalog-unsupported",
            "The action catalog version is not supported.");
        Require(
            string.Equals(plan.ProductName, ProductIdentity.Name, StringComparison.Ordinal)
                && string.Equals(plan.ProductSubtitle, ProductIdentity.Subtitle, StringComparison.Ordinal),
            "plan-product-mismatch",
            "The plan product identity is invalid.");
        Require(
            PlanScopeSupport.IsSupported(plan.Scope, plan.Edition),
            "plan-scope-unsupported",
            "The optimization scope and FiveM edition combination is not supported by this broker.");
        Require(plan.IsExecutable, "plan-not-executable", "The plan is not executable.");
        Require(plan.Blocks is { Count: 0 }, "plan-is-blocked", "A blocked plan cannot be executed.");
        Require(plan.Actions is not null, "plan-actions-missing", "The plan action list is missing.");
        Require(plan.Notices is not null, "plan-notices-missing", "The plan notice list is missing.");
        Require(plan.Options is not null, "plan-options-missing", "The plan options are missing.");

        var now = timeProvider.GetUtcNow();
        Require(
            plan.CreatedAtUtc.Offset == TimeSpan.Zero
                && plan.CreatedAtUtc >= now - MaximumPlanAge
                && plan.CreatedAtUtc <= now + MaximumFutureSkew,
            "plan-expired",
            "The plan timestamp is outside the accepted execution window.");

        OptimizationPlanDto expected;
        try
        {
            expected = PlanBuilder.Build(
                PlanBuilder.CanonicalRequestFor(plan),
                PlanBuildContext.For(plan, ActionCatalog.Current));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new BrokerRequestException(
                "plan-options-invalid",
                "The plan options are invalid.",
                exception);
        }

        Require(expected.IsExecutable, "plan-rebuild-failed", "The plan cannot be rebuilt safely.");
        Require(expected.Options == plan.Options, "plan-options-invalid", "The plan options do not match its preferences.");
        Require(
            expected.Scope == plan.Scope,
            "plan-scope-mismatch",
            "The plan scope does not match the rebuilt plan.");
        Require(
            plan.RequiresElevation == expected.RequiresElevation
                && plan.ContainsNonReversibleActions == expected.ContainsNonReversibleActions
                && plan.MaximumRisk == expected.MaximumRisk,
            "plan-summary-mismatch",
            "The plan summary does not match the current catalog.");
        Require(
            ActionsMatch(plan.Actions, expected.Actions),
            "plan-actions-mismatch",
            "The plan actions or metadata do not match the current catalog.");
        Require(
            NoticesMatch(plan.Notices, expected.Notices),
            "plan-notices-mismatch",
            "The plan notices do not match the current catalog.");

        var administratorActions = plan.Actions
            .Where(action => action.Metadata.RequiredPrivilege == RequiredPrivilege.Administrator)
            .ToArray();
        Require(
            administratorActions.Length > 0 && plan.RequiresElevation,
            "plan-has-no-administrator-actions",
            "The plan does not contain an administrator action.");

        return new ValidatedPlan(plan, administratorActions);
    }

    private static bool ActionsMatch(
        IReadOnlyList<PlannedActionDto> actual,
        IReadOnlyList<PlannedActionDto> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < actual.Count; index++)
        {
            var actualAction = actual[index];
            var expectedAction = expected[index];
            if (actualAction is null
                || actualAction.Metadata is null
                || actualAction.Sequence != index + 1
                || expectedAction.Sequence != actualAction.Sequence
                || !ids.Add(actualAction.Metadata.Id)
                || !actualAction.Metadata.MatchesExactly(expectedAction.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NoticesMatch(
        IReadOnlyList<PlanNoticeDto> actual,
        IReadOnlyList<PlanNoticeDto> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (actual[index] is null
                || !string.Equals(actual[index].Code, expected[index].Code, StringComparison.Ordinal)
                || actual[index].Severity != expected[index].Severity
                || !string.Equals(actual[index].Message, expected[index].Message, StringComparison.Ordinal)
                || !string.Equals(actual[index].ActionId, expected[index].ActionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        string errorCode,
        string message)
    {
        if (!condition)
        {
            throw new BrokerRequestException(errorCode, message);
        }
    }
}
