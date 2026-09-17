// Pure, dependency-free validation for one bug report submission. Mirrors
// Ralven.App.Services.CloudflareBugReportService's client-side rules --
// the Worker never trusts the client alone, every field is re-checked here.
//
// No attachment/screenshot support: reports are text-only (D1 only, no R2).

import { ALLOWED_ENVIRONMENTS } from '../environments.js';
import { ALLOWED_BUG_CODES } from '../bugCodes.js';

export const MAX_CATEGORY_LENGTH = 60;
export const MAX_BUG_CODE_LENGTH = 48;
export const ALLOWED_BUG_REPORT_CATEGORIES = new Set([
  'optimization', 'games', 'windows', 'interface', 'crash', 'other',
]);
export const MAX_SUMMARY_LENGTH = 120;
export const MIN_SUMMARY_LENGTH = 5;
export const MAX_DESCRIPTION_LENGTH = 8000;
export const MIN_DESCRIPTION_LENGTH = 20;
export const MAX_APP_VERSION_LENGTH = 32;
export const MAX_PROFILE_LENGTH = 32;
export const MAX_TECHNICAL_SUMMARY_LENGTH = 512;
export const MAX_EMAIL_LENGTH = 254;
export const MAX_LOG_TEXT_BYTES = 100 * 1024;

const EMAIL_PATTERN = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

function isValidAppVersion(value) {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value.length <= MAX_APP_VERSION_LENGTH &&
    /^[A-Za-z0-9.-]+$/.test(value)
  );
}

function utf8ByteLength(value) {
  return new TextEncoder().encode(value).length;
}

/**
 * Validates and normalizes one bug report submission. Returns `null` when
 * it does not match the closed schema -- never throws.
 */
export function validateBugReport(payload) {
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) {
    return null;
  }

  const {
    reportId,
    category,
    bugCode,
    summary,
    description,
    appVersion,
    profile,
    technicalSummary,
    email,
    logText,
    environment,
  } = payload;

  if (typeof reportId !== 'string' || reportId.length === 0 || reportId.length > 64) {
    return null;
  }

  if (
    typeof category !== 'string' ||
    category.length === 0 ||
    category.length > MAX_CATEGORY_LENGTH ||
    !ALLOWED_BUG_REPORT_CATEGORIES.has(category)
  ) {
    return null;
  }

  if (
    typeof bugCode !== 'string' ||
    bugCode.length === 0 ||
    bugCode.length > MAX_BUG_CODE_LENGTH ||
    !ALLOWED_BUG_CODES.has(bugCode)
  ) {
    return null;
  }

  if (
    typeof summary !== 'string' ||
    summary.trim().length < MIN_SUMMARY_LENGTH ||
    summary.trim().length > MAX_SUMMARY_LENGTH
  ) {
    return null;
  }

  if (
    typeof description !== 'string' ||
    description.trim().length < MIN_DESCRIPTION_LENGTH ||
    description.trim().length > MAX_DESCRIPTION_LENGTH
  ) {
    return null;
  }

  if (!isValidAppVersion(appVersion)) {
    return null;
  }

  if (typeof profile !== 'string' || profile.length === 0 || profile.length > MAX_PROFILE_LENGTH) {
    return null;
  }

  if (
    technicalSummary !== undefined &&
    technicalSummary !== null &&
    (typeof technicalSummary !== 'string' || technicalSummary.length > MAX_TECHNICAL_SUMMARY_LENGTH)
  ) {
    return null;
  }

  if (
    email !== undefined &&
    email !== null &&
    email !== '' &&
    (typeof email !== 'string' || email.length > MAX_EMAIL_LENGTH || !EMAIL_PATTERN.test(email))
  ) {
    return null;
  }

  if (
    logText !== undefined &&
    logText !== null &&
    logText !== '' &&
    (typeof logText !== 'string' || utf8ByteLength(logText) > MAX_LOG_TEXT_BYTES)
  ) {
    return null;
  }

  if (typeof environment !== 'string' || !ALLOWED_ENVIRONMENTS.has(environment)) {
    return null;
  }

  return {
    reportId,
    category,
    bugCode,
    summary: summary.trim(),
    description: description.trim(),
    appVersion,
    profile,
    technicalSummary: technicalSummary ?? null,
    email: email || null,
    logText: logText || null,
    environment,
  };
}
