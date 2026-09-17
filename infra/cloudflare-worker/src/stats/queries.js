// Builds parameterized SQL for every dashboard chart. Pure string/array
// construction only -- no D1 binding touched here, so the shape of each
// query can be unit tested without a database. All queries default to
// `environment = 'Production'` so a developer's own Development-tagged runs
// never skew what the dashboard shows about real users, unless a caller
// explicitly asks for a different environment.
//
// Note on "active users": telemetry events carry no device or user
// identifier by design (see docs/telemetry.md) -- there is no way to count
// unique users from this data without violating that invariant. Every
// "per day" metric here counts *events* (optimization runs), not unique
// people; the dashboard must label it that way instead of implying a user
// count it cannot actually produce.

import { appendEnvironmentClause, appendDateRangeClauses } from '../filters.js';

const DEFAULT_TOP_N = 10;
const OPTIMIZATION_OUTCOMES = "event_name IN ('optimization-completed', 'optimization-failed', 'optimization-cancelled')";

function buildFilters({ from, to, appVersion, environment = 'Production' } = {}, versionColumn = 'app_version') {
  const clauses = [];
  const params = [];

  // 'All' is the explicit, opt-in escape hatch for looking across both
  // environments at once (e.g. while debugging the pipeline itself) --
  // every other value, including an unrecognized one, still defaults to
  // filtering by it so a typo never silently becomes "show everything".
  appendEnvironmentClause(clauses, params, environment);

  appendDateRangeClauses(clauses, params, { from, to });

  if (appVersion) {
    clauses.push(`${versionColumn} = ?`);
    params.push(appVersion);
  }

  return { whereSql: clauses.length > 0 ? clauses.join(' AND ') : '1=1', params };
}

function buildDateFilters(filters = {}, column = 'created_at') {
  const clauses = [];
  const params = [];
  appendDateRangeClauses(clauses, params, filters, column);
  return { whereSql: clauses.length > 0 ? clauses.join(' AND ') : '1=1', params };
}

// Updater events carry the version being installed, not the version running.
const buildUpdaterFilters = (filters) => buildFilters(filters, 'candidate_version');

/** Optimization runs (any outcome) per calendar day, oldest first. */
export function optimizationRunsPerDay(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT substr(received_at, 1, 10) AS day, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES}
          GROUP BY day
          ORDER BY day ASC`,
    params,
  };
}

/** Distribution of Windows versions among reported events. */
export function osVersionBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT os_version, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND os_version IS NOT NULL
          GROUP BY os_version
          ORDER BY runs DESC`,
    params,
  };
}

/** Distribution of Ralven app versions among reported events. */
export function appVersionBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT app_version, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES}
          GROUP BY app_version
          ORDER BY runs DESC`,
    params,
  };
}

/** Average and count of completed-optimization execution time, in ms. */
export function averageOptimizationTimeMs(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT AVG(execution_time_ms) AS average_ms, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'optimization-completed'`,
    params,
  };
}

/** Success rate: completed vs. every outcome (completed/failed/cancelled). */
export function successRate(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT
            SUM(CASE WHEN event_name = 'optimization-completed' THEN 1 ELSE 0 END) AS completed,
            COUNT(*) AS total
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES}`,
    params,
  };
}

/** Successful, consented app initialization health events, limited client-side to one per day/version. */
export function appInitializationsPerDay(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT substr(received_at, 1, 10) AS day, COUNT(*) AS initializations
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'app-initialized'
          GROUP BY day
          ORDER BY day ASC`,
    params,
  };
}

/** Started optional optimization flows that have no terminal event yet. */
export function abandonedOptimizationFlows(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT app_version, profile, COUNT(*) AS abandoned
          FROM telemetry_events AS started
          WHERE ${whereSql}
            AND started.event_name = 'optimization-started'
            AND started.operation_id IS NOT NULL
            AND NOT EXISTS (
              SELECT 1 FROM telemetry_events AS terminal
              WHERE terminal.operation_id = started.operation_id
                AND terminal.event_name IN ('optimization-completed', 'optimization-failed', 'optimization-cancelled')
            )
          GROUP BY app_version, profile
          ORDER BY abandoned DESC`,
    params,
  };
}

/** Benchmark adoption, failure rate and duration without collecting FPS or output files. */
export function gtaVBenchmarkOutcomes(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT event_name, COUNT(*) AS runs, AVG(execution_time_ms) AS average_ms
          FROM telemetry_events
          WHERE ${whereSql}
            AND event_name IN ('gtav-benchmark-completed', 'gtav-benchmark-failed')
          GROUP BY event_name
          ORDER BY event_name ASC`,
    params,
  };
}

/** Failed-run error categories, broken down by app version. */
export function errorsByVersion(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT app_version, error_category, COUNT(*) AS occurrences
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'optimization-failed' AND error_category IS NOT NULL
          GROUP BY app_version, error_category
          ORDER BY app_version DESC, occurrences DESC`,
    params,
  };
}

/** Most common CPU models, top N by count. */
export function topCpuModels(filters, topN = DEFAULT_TOP_N) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT cpu_model, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND cpu_model IS NOT NULL
          GROUP BY cpu_model
          ORDER BY runs DESC
          LIMIT ?`,
    params: [...params, topN],
  };
}

/** Most common GPU models, top N by count. */
export function topGpuModels(filters, topN = DEFAULT_TOP_N) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT gpu_model, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND gpu_model IS NOT NULL
          GROUP BY gpu_model
          ORDER BY runs DESC
          LIMIT ?`,
    params: [...params, topN],
  };
}

/** Distribution of RAM buckets (GiB) among reported events. */
export function ramBucketBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT ram_bucket_gib, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND ram_bucket_gib IS NOT NULL
          GROUP BY ram_bucket_gib
          ORDER BY ram_bucket_gib ASC`,
    params,
  };
}

/**
 * Error category counts across every version at once -- complements
 * {@link errorsByVersion} (which breaks the same data down per version) with
 * a single, quick "what's failing the most, overall" view.
 */
export function errorCategoryBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT error_category, COUNT(*) AS occurrences
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'optimization-failed' AND error_category IS NOT NULL
          GROUP BY error_category
          ORDER BY occurrences DESC`,
    params,
  };
}

/** Exact allowlisted failure codes, suitable for grouping concrete incidents. */
export function bugCodeBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT bug_code, COUNT(*) AS occurrences
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'optimization-failed' AND bug_code IS NOT NULL
          GROUP BY bug_code
          ORDER BY occurrences DESC`,
    params,
  };
}

/**
 * A raw feed of the most recent failed runs (not aggregated) -- the
 * fastest way to see exactly what environment a fresh bug is showing up in
 * without waiting for it to accumulate enough volume to appear in the
 * aggregate charts above.
 */
export function recentFailures(filters, limit = 20) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT event_id, received_at, app_version, error_category, bug_code, environment,
                 execution_time_ms, os_version, system_architecture, cpu_model, gpu_model,
                 profile, optimization_target_count,
                 (SELECT GROUP_CONCAT(action_id, ',')
                    FROM telemetry_event_actions
                   WHERE telemetry_event_id = telemetry_events.id) AS action_ids
          FROM telemetry_events
          WHERE ${whereSql} AND event_name = 'optimization-failed'
          ORDER BY received_at DESC
          LIMIT ?`,
    params: [...params, limit],
  };
}

// --- v5: expanded diagnostic fields. The app DOES populate and send every
// column these read (see the telemetry payload in CloudflareTelemetryService
// and the INSERT in index.js), and validateEvent accepts them, so the data is
// in D1 today. What is still missing is only the dashboard wiring: they are
// deliberately absent from STATS_BUILDERS until a panel consumes them. Keep
// them covered by test/stats/queries.test.js so they stay usable. ---

/** Distribution of GTA V editions (Legacy/Enhanced/Unknown). */
export function gtaEditionBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT gta_edition, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND gta_edition IS NOT NULL
          GROUP BY gta_edition
          ORDER BY runs DESC`,
    params,
  };
}

/** FiveM installation detection rate. */
export function fiveMInstallDetectionRate(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT
            SUM(CASE WHEN five_m_install_detected = 1 THEN 1 ELSE 0 END) AS detected,
            COUNT(*) AS total
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND five_m_install_detected IS NOT NULL`,
    params,
  };
}

/** Distribution of disk types (HDD/SSD/NVMe/Unknown). */
export function diskTypeBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT disk_type, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND disk_type IS NOT NULL
          GROUP BY disk_type
          ORDER BY runs DESC`,
    params,
  };
}

/** Average optimization target count. */
export function averageOptimizationTargetCount(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT AVG(optimization_target_count) AS average_count, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND optimization_target_count IS NOT NULL`,
    params,
  };
}

/** Backup creation and restoration rates. */
export function backupStats(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT
            SUM(CASE WHEN backup_created = 1 THEN 1 ELSE 0 END) AS created,
            SUM(CASE WHEN backup_restored = 1 THEN 1 ELSE 0 END) AS restored,
            COUNT(*) AS total
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND (backup_created IS NOT NULL OR backup_restored IS NOT NULL)`,
    params,
  };
}

/** Elevation usage rate. */
export function elevationUsageRate(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT
            SUM(CASE WHEN elevation_used = 1 THEN 1 ELSE 0 END) AS elevated,
            COUNT(*) AS total
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND elevation_used IS NOT NULL`,
    params,
  };
}

/** Windows build distribution. */
export function windowsBuildBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT windows_build, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND ${OPTIMIZATION_OUTCOMES} AND windows_build IS NOT NULL
          GROUP BY windows_build
          ORDER BY runs DESC
          LIMIT 10`,
    params,
  };
}

/** Outcome mix makes cancellations visible instead of folding them into failures. */
export function optimizationOutcomeBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT event_name, COUNT(*) AS occurrences
          FROM telemetry_events
          WHERE ${whereSql}
          GROUP BY event_name
          ORDER BY occurrences DESC`,
    params,
  };
}

export function profileBreakdown(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT profile, COUNT(*) AS runs
          FROM telemetry_events
          WHERE ${whereSql} AND profile IS NOT NULL
          GROUP BY profile
          ORDER BY runs DESC`,
    params,
  };
}

export function actionUsage(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT telemetry_event_actions.action_id, COUNT(*) AS runs
          FROM telemetry_event_actions
          JOIN telemetry_events ON telemetry_events.id = telemetry_event_actions.telemetry_event_id
          WHERE ${whereSql}
          GROUP BY telemetry_event_actions.action_id
          ORDER BY runs DESC
          LIMIT 12`,
    params,
  };
}

export function reliabilityByVersion(filters) {
  const { whereSql, params } = buildFilters(filters);
  return {
    sql: `SELECT app_version,
                 COUNT(*) AS total,
                 SUM(CASE WHEN event_name = 'optimization-completed' THEN 1 ELSE 0 END) AS completed,
                 SUM(CASE WHEN event_name = 'optimization-failed' THEN 1 ELSE 0 END) AS failed,
                 SUM(CASE WHEN event_name = 'optimization-cancelled' THEN 1 ELSE 0 END) AS cancelled
          FROM telemetry_events
          WHERE ${whereSql}
          GROUP BY app_version
          ORDER BY MAX(received_at) DESC
          LIMIT 10`,
    params,
  };
}

/** Aggregate account growth only; no UID or profile field leaves D1. */
export function accountSummary(filters) {
  const { whereSql, params } = buildDateFilters(filters);
  return {
    sql: `SELECT
            (SELECT COUNT(*) FROM account_profiles) AS total_accounts,
            COUNT(*) AS new_accounts
          FROM account_profiles
          WHERE ${whereSql}`,
    params,
  };
}

export function accountsPerDay(filters) {
  const { whereSql, params } = buildDateFilters(filters);
  return {
    sql: `SELECT substr(created_at, 1, 10) AS day, COUNT(*) AS accounts
          FROM account_profiles
          WHERE ${whereSql}
          GROUP BY day
          ORDER BY day ASC`,
    params,
  };
}

/** Aggregate AI operations and cost; interactive content and account IDs stay private. */
export function aiUsageSummary(filters) {
  const { whereSql, params } = buildDateFilters(filters);
  return {
    sql: `SELECT COUNT(*) AS requests,
                 COUNT(DISTINCT account_uid) AS active_accounts,
                 SUM(CASE WHEN state = 'completed' THEN 1 ELSE 0 END) AS completed,
                 SUM(CASE WHEN state = 'failed' THEN 1 ELSE 0 END) AS failed,
                 COALESCE(SUM(actual_cost_microusd), 0) AS cost_microusd,
                 COALESCE(SUM(input_tokens), 0) AS input_tokens,
                 COALESCE(SUM(output_tokens), 0) AS output_tokens
          FROM ralven_ai_usage
          WHERE ${whereSql}`,
    params,
  };
}

export function aiUsagePerDay(filters) {
  const { whereSql, params } = buildDateFilters(filters);
  return {
    sql: `SELECT substr(created_at, 1, 10) AS day,
                 COUNT(*) AS requests,
                 COUNT(DISTINCT account_uid) AS active_accounts,
                 COALESCE(SUM(actual_cost_microusd), 0) AS cost_microusd
          FROM ralven_ai_usage
          WHERE ${whereSql}
          GROUP BY day
          ORDER BY day ASC`,
    params,
  };
}

export function billingSubscriptionBreakdown() {
  return {
    sql: `SELECT state, COUNT(*) AS subscriptions
          FROM billing_subscriptions
          GROUP BY state
          ORDER BY subscriptions DESC`,
    params: [],
  };
}

export function billingPaymentSummary(filters) {
  const { whereSql, params } = buildDateFilters(filters, 'updated_at');
  return {
    sql: `SELECT COUNT(*) AS payments,
                 SUM(CASE WHEN state IN ('approved', 'refunded') THEN amount_cents - refunded_cents ELSE 0 END) AS net_revenue_cents,
                 SUM(CASE WHEN state IN ('rejected', 'charged_back') THEN 1 ELSE 0 END) AS problem_payments,
                 COALESCE(SUM(refunded_cents), 0) AS refunded_cents
          FROM billing_payments
          WHERE ${whereSql}`,
    params,
  };
}

export function updaterSummary(filters) {
  const { whereSql, params } = buildUpdaterFilters(filters);
  return {
    sql: `SELECT COUNT(*) AS events,
                 SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END) AS failed,
                 COUNT(DISTINCT candidate_version) AS candidate_versions,
                 MAX(received_at) AS last_event_at
          FROM updater_events
          WHERE ${whereSql}`,
    params,
  };
}
