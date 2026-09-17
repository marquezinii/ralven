// Pure, dependency-free validation for one admin live-alert update. Mirrors
// bugReports/validateSubmission.js: the Worker never trusts the client
// alone, every field is re-checked here.
//
// `message` is optional so the dashboard's "Desativar" action can flip
// `active` off without resending the stored text.

export const MAX_LIVE_ALERT_MESSAGE_LENGTH = 300;
export const LIVE_ALERT_SEVERITIES = new Set(['info', 'important', 'critical']);

export function validateLiveAlertUpdate(payload) {
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) {
    return null;
  }

  const { message, active, severity } = payload;

  if (typeof active !== 'boolean') {
    return null;
  }

  if (severity !== undefined && !LIVE_ALERT_SEVERITIES.has(severity)) {
    return null;
  }

  if (message === undefined) {
    return severity === undefined ? { active } : { active, severity };
  }

  if (typeof message !== 'string') {
    return null;
  }

  const trimmed = message.trim();
  if (trimmed.length > MAX_LIVE_ALERT_MESSAGE_LENGTH) {
    return null;
  }
  if (active && trimmed.length === 0) {
    return null;
  }

  return severity === undefined ? { message: trimmed, active } : { message: trimmed, active, severity };
}
