/* Pure presentation projection for the isolated STP V2 daily timeline. */

const DAY_US = 86_400_000_000n;
const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const DATETIME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))?$/;
const PROJECT_KEY = /^[0-9a-f]{64}$/;
const MAX_PREFERENCE_BYTES = 16 * 1024;
const projectionContexts = new WeakMap();

const VISUAL_WARNING = 'Preferencia visual inválida; se usarán los valores predeterminados.';
const PROJECT_WARNING = 'Clave de proyecto inválida; se usarán las preferencias visuales predeterminadas.';

function fail(message) { throw new Error(message); }
function own(value, key) { return Object.prototype.hasOwnProperty.call(value, key); }
function completed(fragment) { return fragment.lockedCompleted === true || fragment.locked_completed === true; }

function daysInMonth(year, month) {
  if (month === 2) return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28;
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

function parseDate(value) {
  if (typeof value !== 'string') fail('fecha civil inválida');
  const match = DATE.exec(value);
  if (!match) fail(`fecha civil inválida: ${value}`);
  const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
  if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month)) {
    fail(`fecha civil inválida: ${value}`);
  }
  return { year, month, day };
}

function parseDateTime(value) {
  if (typeof value !== 'string') fail('fecha civil inválida');
  const match = DATETIME.exec(value);
  if (!match) fail(`fecha civil inválida: ${value}`);
  const date = parseDate(`${match[1]}-${match[2]}-${match[3]}`);
  const hour = Number(match[4]), minute = Number(match[5]), second = Number(match[6]);
  if (hour > 23 || minute > 59 || second > 59) fail(`fecha civil inválida: ${value}`);
  const micros = Number(((match[7] || '') + '000000').slice(0, 6));
  return { ...date, hour, minute, second, micros };
}

function dayNumber(year, month, day) {
  const adjustedYear = year - (month <= 2 ? 1 : 0);
  const era = Math.floor(adjustedYear / 400);
  const yearOfEra = adjustedYear - era * 400;
  const adjustedMonth = month + (month > 2 ? -3 : 9);
  return era * 146097
    + Math.floor((yearOfEra * 365 + Math.floor(yearOfEra / 4) - Math.floor(yearOfEra / 100)
      + Math.floor((153 * adjustedMonth + 2) / 5) + day - 1));
}

function dateFromDayNumber(number) {
  const era = Math.floor(number / 146097);
  const dayOfEra = number - era * 146097;
  const yearOfEra = Math.floor((dayOfEra - Math.floor(dayOfEra / 1460)
    + Math.floor(dayOfEra / 36524) - Math.floor(dayOfEra / 146096)) / 365);
  const year = yearOfEra + era * 400;
  const dayOfYear = dayOfEra - (365 * yearOfEra + Math.floor(yearOfEra / 4) - Math.floor(yearOfEra / 100));
  const adjustedMonth = Math.floor((5 * dayOfYear + 2) / 153);
  const day = dayOfYear - Math.floor((153 * adjustedMonth + 2) / 5) + 1;
  return { year: year + (adjustedMonth >= 10 ? 1 : 0), month: adjustedMonth + (adjustedMonth < 10 ? 3 : -9), day };
}

function dateText(parts) {
  const pad = (number, width = 2) => String(number).padStart(width, '0');
  return `${pad(parts.year, 4)}-${pad(parts.month)}-${pad(parts.day)}`;
}

function addDateDays(value, days) {
  const parts = parseDate(value);
  const shifted = dateFromDayNumber(dayNumber(parts.year, parts.month, parts.day) + days);
  if (shifted.year < 1 || shifted.year > 9999) fail('fecha civil fuera del rango compatible');
  return dateText(shifted);
}

function addTimestampDays(value, days) {
  parseDateTime(value);
  return `${addDateDays(value.slice(0, 10), days)}${value.slice(10)}`;
}

function scalar(value) {
  const part = parseDateTime(value);
  const withinDay = (part.hour * 3600 + part.minute * 60 + part.second) * 1_000_000 + part.micros;
  return BigInt(dayNumber(part.year, part.month, part.day)) * DAY_US + BigInt(withinDay);
}

function midnight(value) {
  const part = parseDateTime(value);
  if (part.hour || part.minute || part.second || part.micros) fail('horizonte civil inválido');
  return `${value.slice(0, 10)}T00:00:00`;
}

function utf8Bytes(value) {
  if (typeof TextEncoder === 'function') return new TextEncoder().encode(value).byteLength;
  return unescape(encodeURIComponent(value)).length;
}

function visualDefaults(warning = '') {
  return {
    showCompleted: true, showPrecedences: true, visibleAttributes: [],
    sortByStart: false, sortByType: false, sortByAttribute: false,
    sortAttribute: '', sortDirection: 'asc', warning,
  };
}

function compareAttributeValues(leftValue, rightValue, direction) {
  const left = typeof leftValue === 'string' ? leftValue.trim() : '';
  const right = typeof rightValue === 'string' ? rightValue.trim() : '';
  if (!left && !right) return 0;
  if (!left) return 1;
  if (!right) return -1;
  const leftNumber = Number(left), rightNumber = Number(right);
  const comparison = Number.isFinite(leftNumber) && Number.isFinite(rightNumber)
    ? (leftNumber < rightNumber ? -1 : leftNumber > rightNumber ? 1 : 0)
    : left.localeCompare(right, 'es', { numeric: true, sensitivity: 'base' });
  return direction === 'desc' ? -comparison : comparison;
}

function validAvailableAttributes(value) {
  return Array.isArray(value)
    && value.every(item => typeof item === 'string' && item.length > 0)
    && new Set(value).size === value.length;
}

function validateVisualValue(value, availableAttributes, schema) {
  if (!validAvailableAttributes(availableAttributes) || !value || typeof value !== 'object' || Array.isArray(value)) fail('preferencia visual inválida');
  const expected = schema === 1
    ? ['showCompleted', 'showPrecedences', 'version', 'visibleAttributes']
    : schema === 2
      ? ['showCompleted', 'showPrecedences', 'sortByStart', 'sortByType', 'version', 'visibleAttributes']
      : schema === 3
        ? ['showCompleted', 'showPrecedences', 'sortAttribute', 'sortByAttribute', 'sortByStart', 'sortByType', 'sortDirection', 'version', 'visibleAttributes']
        : ['showCompleted', 'showPrecedences', 'sortAttribute', 'sortByAttribute', 'sortByStart', 'sortByType', 'sortDirection', 'visibleAttributes'];
  if (Object.keys(value).sort().join(',') !== expected.join(',')) fail('preferencia visual inválida');
  if ((schema === 1 && value.version !== 1) || (schema === 2 && value.version !== 2) || (schema === 3 && value.version !== 3)) fail('preferencia visual inválida');
  if (typeof value.showCompleted !== 'boolean' || typeof value.showPrecedences !== 'boolean') fail('preferencia visual inválida');
  if (schema !== 1 && (typeof value.sortByStart !== 'boolean' || typeof value.sortByType !== 'boolean')) fail('preferencia visual inválida');
  if ((schema === 0 || schema === 3) && (
    typeof value.sortByAttribute !== 'boolean'
    || typeof value.sortAttribute !== 'string'
    || !['asc', 'desc'].includes(value.sortDirection)
    || (value.sortAttribute && !availableAttributes.includes(value.sortAttribute))
    || (value.sortByAttribute && !availableAttributes.includes(value.sortAttribute))
  )) fail('preferencia visual inválida');
  if (!Array.isArray(value.visibleAttributes)
    || value.visibleAttributes.some(item => typeof item !== 'string')
    || new Set(value.visibleAttributes).size !== value.visibleAttributes.length) fail('preferencia visual inválida');
  if (schema === 0 && value.visibleAttributes.some(item => !availableAttributes.includes(item))) fail('preferencia visual inválida');
  return {
    showCompleted: value.showCompleted,
    showPrecedences: value.showPrecedences,
    visibleAttributes: value.visibleAttributes.filter(item => availableAttributes.includes(item)),
    sortByStart: schema === 1 ? false : value.sortByStart,
    sortByType: schema === 1 ? false : value.sortByType,
    sortByAttribute: schema === 0 || schema === 3 ? value.sortByAttribute : false,
    sortAttribute: schema === 0 || schema === 3 ? value.sortAttribute : '',
    sortDirection: schema === 0 || schema === 3 ? value.sortDirection : 'asc',
  };
}

export function loadVisualPreferences(storage, projectKey, availableAttributes) {
  if (typeof projectKey !== 'string' || !PROJECT_KEY.test(projectKey)) return visualDefaults(PROJECT_WARNING);
  let raw;
  try { raw = storage.getItem(`vgs-stp-v2:visual:${projectKey}`); }
  catch (_) { return visualDefaults('No se pudo leer la preferencia visual; se usarán los valores predeterminados.'); }
  if (raw === null) return visualDefaults();
  try {
    if (typeof raw !== 'string' || utf8Bytes(raw) > MAX_PREFERENCE_BYTES) fail('preferencia visual inválida');
    const value = JSON.parse(raw);
    const schema = value?.version;
    if (schema !== 1 && schema !== 2 && schema !== 3) fail('preferencia visual inválida');
    return { ...validateVisualValue(value, availableAttributes, schema), warning: '' };
  } catch (_) { return visualDefaults(VISUAL_WARNING); }
}

export function saveVisualPreferences(storage, projectKey, preferences, availableAttributes) {
  if (typeof projectKey !== 'string' || !PROJECT_KEY.test(projectKey)) return { persisted: false, ...visualDefaults(PROJECT_WARNING) };
  let validated;
  try { validated = validateVisualValue(preferences, availableAttributes, 0); }
  catch (_) { return { persisted: false, ...visualDefaults(VISUAL_WARNING) }; }
  const payload = JSON.stringify({ version: 3, ...validated });
  if (utf8Bytes(payload) > MAX_PREFERENCE_BYTES) return { persisted: false, ...visualDefaults(VISUAL_WARNING) };
  try {
    storage.setItem(`vgs-stp-v2:visual:${projectKey}`, payload);
    return { persisted: true, ...validated, warning: '' };
  } catch (_) {
    return { persisted: false, ...visualDefaults('No se pudo guardar la preferencia visual; se usarán los valores predeterminados.') };
  }
}

function horizonInfo(plannerState) {
  if (!plannerState || typeof plannerState !== 'object' || !plannerState.model) fail('estado del planificador inválido');
  let horizon = plannerState.horizon;
  if (!horizon) {
    const fragments = plannerState.model.fragments;
    if (!Array.isArray(fragments) || !fragments.length || fragments.some(fragment => !completed(fragment))) fail('estado del planificador inválido');
    let earliest = fragments[0].start, latest = fragments[0].finish;
    for (const fragment of fragments.slice(1)) {
      if (scalar(fragment.start) < scalar(earliest)) earliest = fragment.start;
      if (scalar(fragment.finish) > scalar(latest)) latest = fragment.finish;
    }
    const latestParts = parseDateTime(latest);
    const finishDate = latestParts.hour || latestParts.minute || latestParts.second || latestParts.micros
      ? addDateDays(latest.slice(0, 10), 1) : latest.slice(0, 10);
    const startDate = earliest.slice(0, 10);
    const derivedDays = dayNumber(...Object.values(parseDate(finishDate))) - dayNumber(...Object.values(parseDate(startDate)));
    horizon = {
      horizonStart: `${startDate}T00:00:00`,
      horizonFinish: `${addDateDays(startDate, Math.min(366, Math.max(1, derivedDays)))}T00:00:00`,
    };
  }
  const start = midnight(horizon.horizonStart);
  const finish = midnight(horizon.horizonFinish);
  const startScalar = scalar(start), finishScalar = scalar(finish);
  const duration = Number((finishScalar - startScalar) / DAY_US);
  if (finishScalar <= startScalar || duration < 1 || duration > 366 || BigInt(duration) * DAY_US !== finishScalar - startScalar) fail('horizonte civil inválido');
  return { start, finish, startScalar, finishScalar, duration, startDay: dayNumber(...Object.values(parseDate(start.slice(0, 10)))) };
}

function headers(horizon) {
  const weekdayNames = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];
  const epoch = dayNumber(1970, 1, 1);
  const result = [];
  for (let index = 0; index < horizon.duration; index += 1) {
    const date = addDateDays(horizon.start.slice(0, 10), index);
    const absolute = horizon.startDay + index;
    const weekday = weekdayNames[((absolute - epoch + 4) % 7 + 7) % 7];
    result.push({ index, date, weekday, label: `${weekday} ${date.slice(8, 10)}/${date.slice(5, 7)}` });
  }
  return result;
}

function containsNativeBarKey(value, activity) {
  return (activity?.nativeBars || []).some(bar => {
    const key = typeof bar?.key === 'string' ? bar.key.trim() : '';
    if (!key) return false;
    const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`(?:^|[^\\p{L}\\p{N}])${escaped}(?=$|[^\\p{L}\\p{N}])`, 'iu').test(value);
  });
}

function activityIdentity(activity) {
  for (const key of ['Description', 'Name', 'Activity Name']) {
    const value = activity?.grid?.[key];
    if (typeof value === 'string' && value.trim()
      && !/\d{4}-\d{2}-\d{2}T/.test(value)
      && !containsNativeBarKey(value, activity)) {
      const humanName = value.trim();
      return { label: humanName, humanName };
    }
  }
  return { label: `Actividad ${String(activity?.id ?? '')}`, humanName: null };
}

function gridText(activity, acceptedKeys) {
  const grid = activity?.grid;
  if (!grid || typeof grid !== 'object' || Array.isArray(grid)) return '';
  for (const [key, value] of Object.entries(grid)) {
    if (!acceptedKeys.has(String(key).trim().replace(/\s+/g, ' ').toLocaleLowerCase('es-MX'))) continue;
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
}

function activityType(activity) {
  return gridText(activity, new Set(['activity type'])) || 'Sin tipo';
}

function activityResource(activity) {
  return gridText(activity, new Set(['resource', 'resources', 'recurso', 'equipo'])) || 'Sin recurso';
}

function activityRate(activity) {
  const value = Number(gridText(activity, new Set(['rate'])));
  return Number.isFinite(value) && value >= 0 ? value : null;
}

function humanDateTime(value) {
  const part = parseDateTime(value);
  const match = DATETIME.exec(value);
  const fraction = match[7] ? `.${match[7]}` : '';
  const pad = number => String(number).padStart(2, '0');
  return `${pad(part.day)}/${pad(part.month)}/${String(part.year).padStart(4, '0')} ${pad(part.hour)}:${pad(part.minute)}:${pad(part.second)}${fraction}`;
}

function splitProtection(fragments) {
  const result = new Map(fragments.map(fragment => [fragment.id, { before: false, after: false }]));
  const byActivity = new Map();
  for (const fragment of fragments) {
    const id = String(fragment.activityId ?? fragment.activity_id ?? '');
    if (!byActivity.has(id)) byActivity.set(id, []);
    byActivity.get(id).push(fragment);
  }
  for (const items of byActivity.values()) {
    items.sort((left, right) => scalar(left.start) < scalar(right.start) ? -1 : scalar(left.start) > scalar(right.start) ? 1 : 0);
    for (let index = 1; index < items.length; index += 1) {
      const previous = items[index - 1], current = items[index];
      const sameNativeBar = String(previous.nativeBarKey ?? previous.native_bar_key ?? '') === String(current.nativeBarKey ?? current.native_bar_key ?? '');
      if (sameNativeBar && previous.finish === current.start && previous.originalFinish === current.originalStart) {
        result.get(previous.id).after = true;
        result.get(current.id).before = true;
      }
    }
  }
  return result;
}

function fragmentStatus(fragment) {
  if (completed(fragment)) return 'completed';
  if (fragment.provenance === 'direct') return 'direct';
  if (fragment.provenance === 'cascade') return 'cascade';
  return 'pending';
}

function segmentGroups(fragment, horizon, protection, label, rate = null, logicalUnit = null) {
  const fragmentStart = scalar(fragment.start), fragmentFinish = scalar(fragment.finish);
  if (fragmentFinish <= fragmentStart) fail(`fragmento inválido: ${fragment.id}`);
  const clippedStart = fragmentStart > horizon.startScalar ? fragmentStart : horizon.startScalar;
  const clippedFinish = fragmentFinish < horizon.finishScalar ? fragmentFinish : horizon.finishScalar;
  if (clippedFinish <= clippedStart) return [];
  const firstIndex = Number((clippedStart - horizon.startScalar) / DAY_US);
  const lastExclusive = Number((clippedFinish - horizon.startScalar + DAY_US - 1n) / DAY_US);
  const groups = [];
  for (let index = firstIndex; index < lastExclusive; index += 1) {
    const dayStart = horizon.startScalar + BigInt(index) * DAY_US;
    const dayFinish = dayStart + DAY_US;
    const segmentStart = fragmentStart > dayStart ? fragmentStart : dayStart;
    const segmentFinish = fragmentFinish < dayFinish ? fragmentFinish : dayFinish;
    if (segmentFinish <= segmentStart) continue;
    const date = addDateDays(horizon.start.slice(0, 10), index);
    const start = segmentStart === fragmentStart ? fragment.start : `${date}T00:00:00`;
    const nextDate = addDateDays(date, 1);
    const finish = segmentFinish === fragmentFinish ? fragment.finish : `${nextDate}T00:00:00`;
    const flags = protection.get(fragment.id) || { before: false, after: false };
    const widthRatio = Number(segmentFinish - segmentStart) / Number(DAY_US);
    groups.push({ date, segment: {
      fragmentId: fragment.id,
      nativeBarKey: String(fragment.nativeBarKey ?? fragment.native_bar_key ?? ''),
      ...(logicalUnit ? {
        logicalUnitId: logicalUnit.logicalUnitId,
        logicalUnitDate: logicalUnit.currentDate,
        eligibility: { ...logicalUnit.eligibility },
      } : {}),
      date, start, finish,
      leftRatio: Number(segmentStart - dayStart) / Number(DAY_US),
      widthRatio,
      ...(rate === null ? {} : { scheduledAmount: rate * widthRatio }),
      status: fragmentStatus(fragment),
      protectedSplitBefore: flags.before && segmentStart === fragmentStart,
      protectedSplitAfter: flags.after && segmentFinish === fragmentFinish,
      tooltip: `${label}: ${humanDateTime(start)} — ${humanDateTime(finish)}`,
    } });
  }
  return groups;
}

function fragmentsByActivity(fragments) {
  const result = new Map();
  for (const fragment of fragments) {
    const id = String(fragment.activityId ?? fragment.activity_id ?? '');
    if (!result.has(id)) result.set(id, []);
    result.get(id).push(fragment);
  }
  return result;
}

function normalizedFragmentEligibility(eligibilityFor, fragment) {
  if (completed(fragment)) {
    return { eligible: false, reason: 'completed', guidance: 'El trabajo completado no se puede mover.' };
  }
  let value;
  try { value = eligibilityFor(fragment); } catch (_) { value = null; }
  if (value && typeof value === 'object' && typeof value.eligible === 'boolean') {
    return {
      eligible: value.eligible,
      reason: typeof value.reason === 'string' ? value.reason : '',
      guidance: typeof value.guidance === 'string' ? value.guidance : '',
    };
  }
  return { eligible: true, reason: '', guidance: '' };
}

function logicalUnitProjection(fragments, eligibilityFor) {
  const grouped = new Map();
  for (const fragment of fragments) {
    const activityId = String(fragment.activityId ?? fragment.activity_id ?? '');
    const originalDate = String(
      fragment.logicalUnitDate ?? fragment.logical_unit_date
      ?? String(fragment.originalStart ?? fragment.original_start ?? '').slice(0, 10),
    );
    parseDate(originalDate);
    const logicalUnitId = `${activityId}:${originalDate}`;
    if (!grouped.has(logicalUnitId)) grouped.set(logicalUnitId, { activityId, logicalUnitId, originalDate, fragments: [] });
    grouped.get(logicalUnitId).fragments.push(fragment);
  }
  const byFragment = new Map();
  const units = [...grouped.values()];
  for (const unit of units) {
    unit.fragments.sort((left, right) => scalar(left.originalStart) < scalar(right.originalStart) ? -1 : 1);
    const first = unit.fragments[0];
    if (first.logicalUnitDate ?? first.logical_unit_date) {
      const original = parseDate(first.originalStart.slice(0, 10));
      const current = parseDate(first.start.slice(0, 10));
      unit.currentDate = addDateDays(
        unit.originalDate,
        dayNumber(current.year, current.month, current.day) - dayNumber(original.year, original.month, original.day),
      );
    } else {
      unit.currentDate = first.start.slice(0, 10);
    }
    const eligibility = unit.fragments.map(fragment => normalizedFragmentEligibility(eligibilityFor, fragment));
    const blocked = eligibility.find(item => !item.eligible);
    unit.eligibility = blocked ? { ...blocked } : { eligible: true, reason: '', guidance: '' };
    for (const fragment of unit.fragments) byFragment.set(fragment.id, unit);
  }
  units.sort((left, right) => left.originalDate.localeCompare(right.originalDate));
  return { units, byFragment };
}

function endpoint(activityId, nativeBarKey, edge, fragments) {
  const matches = fragments.filter(fragment => String(fragment.activityId ?? fragment.activity_id ?? '') === String(activityId)
    && String(fragment.nativeBarKey ?? fragment.native_bar_key ?? '') === String(nativeBarKey));
  if (!matches.length) return null;
  let selected = matches[0];
  for (const candidate of matches.slice(1)) {
    const candidateValue = scalar(edge === 'start' ? candidate.start : candidate.finish);
    const selectedValue = scalar(edge === 'start' ? selected.start : selected.finish);
    if ((edge === 'start' && candidateValue < selectedValue) || (edge === 'finish' && candidateValue > selectedValue)) selected = candidate;
  }
  const at = edge === 'start' ? selected.start : selected.finish;
  return {
    activityId: String(activityId), nativeBarKey: String(nativeBarKey), edge, at,
  };
}

function visibleEndpoint(activityId, nativeBarKey, edge, fragments, visibleRows) {
  const original = endpoint(activityId, nativeBarKey, edge, fragments);
  if (!original) return null;
  const row = visibleRows.get(original.activityId);
  if (!row) return null;
  for (const day of row.days) {
    for (const segment of day.segments) {
      if (segment.nativeBarKey !== original.nativeBarKey || segment[edge] !== original.at) continue;
      const ratio = edge === 'start' ? segment.leftRatio : segment.leftRatio + segment.widthRatio;
      if (!Number.isFinite(day.dayIndex) || !Number.isFinite(ratio)) return null;
      return { ...original, date: day.date, dayIndex: day.dayIndex, ratio };
    }
  }
  return null;
}

function relationshipId(kind, index) {
  return `${kind}-${index}`;
}

function precedenceLines(snapshot, fragments, enabled, visibleRows) {
  if (!enabled) return [];
  const endpoints = {
    FinishStart: ['finish', 'start'], StartStart: ['start', 'start'],
    FinishFinish: ['finish', 'finish'], StartFinish: ['start', 'finish'],
  };
  return (snapshot.precedences || []).flatMap((link, index) => {
    const edges = endpoints[link.type];
    if (!edges) fail(`tipo de precedencia inválido: ${link.type}`);
    const from = visibleEndpoint(link.predecessorId, link.predecessorBar, edges[0], fragments, visibleRows);
    const to = visibleEndpoint(link.successorId, link.successorBar, edges[1], fragments, visibleRows);
    if (!from || !to) return [];
    return [{
      relationId: relationshipId('precedence', index),
      id: String(link.id ?? link.key ?? ''), key: String(link.key ?? ''),
      predecessorId: String(link.predecessorId), successorId: String(link.successorId),
      type: link.type, status: 'precedence', kind: 'precedence', route: 'orthogonal', from, to,
    }];
  });
}

function continuityLines(fragments, visibleRows) {
  const result = [];
  let relationIndex = 0;
  for (const [activityId, items] of fragmentsByActivity(fragments)) {
    if (!visibleRows.has(activityId)) continue;
    const visibleItems = items
      .filter(item => item.displaySuppressed !== true && item.display_suppressed !== true)
      .sort((left, right) => scalar(left.start) < scalar(right.start) ? -1 : scalar(left.start) > scalar(right.start) ? 1 : 0);
    for (let index = 1; index < visibleItems.length; index += 1) {
      const previous = visibleItems[index - 1], current = visibleItems[index];
      const relationId = relationshipId('continuity', relationIndex);
      relationIndex += 1;
      const from = visibleEndpoint(activityId, previous.nativeBarKey ?? previous.native_bar_key, 'finish', [previous], visibleRows);
      const to = visibleEndpoint(activityId, current.nativeBarKey ?? current.native_bar_key, 'start', [current], visibleRows);
      if (!from || !to) continue;
      result.push({ relationId, activityId, status: 'continuity', kind: 'continuity', route: 'orthogonal', from, to });
    }
  }
  return result;
}

function relationshipDiagnostics(precedences) {
  return (precedences || []).map((link, index) => ({
    relationId: relationshipId('precedence', index),
    id: String(link.id ?? ''),
    key: String(link.key ?? ''),
    predecessorId: String(link.predecessorId ?? ''),
    successorId: String(link.successorId ?? ''),
    predecessorBar: String(link.predecessorBar ?? ''),
    successorBar: String(link.successorBar ?? ''),
    type: String(link.type ?? ''),
    lagMicroseconds: String(link.lagMicroseconds ?? ''),
    groupKey: String(link.groupKey ?? ''),
  }));
}

function diagnostics(fragments, horizon) {
  return fragments.map(fragment => {
    const start = scalar(fragment.start), finish = scalar(fragment.finish);
    const inRange = start >= horizon.startScalar && finish <= horizon.finishScalar;
    return {
      activityId: String(fragment.activityId ?? fragment.activity_id ?? ''),
      fragmentId: fragment.id,
      nativeBarKey: String(fragment.nativeBarKey ?? fragment.native_bar_key ?? ''),
      nativeBarXmlKey: String(fragment.nativeBarXmlKey ?? fragment.native_bar_xml_key ?? ''),
      originalStart: fragment.originalStart,
      originalFinish: fragment.originalFinish,
      proposedStart: fragment.start,
      proposedFinish: fragment.finish,
      lockedCompleted: completed(fragment),
      provenance: fragment.provenance || '',
      horizonState: inRange ? 'in-range' : 'out-of-range',
    };
  });
}

const LEGEND_LABELS = {
  completed: 'Avance completado y protegido', pending: 'Trabajo pendiente sin cambios',
  direct: 'Movimiento directo', cascade: 'Movimiento por cascada',
  precedence: 'Precedencia', continuity: 'Continuidad de actividad',
};

function legacyActivityEligibility(eligibilityFor, activityId, logicalUnits) {
  try {
    const value = eligibilityFor(activityId);
    if (value && typeof value === 'object' && typeof value.showControls === 'boolean') return { ...value };
  } catch (_) { /* A fragment-only callback intentionally rejects activity IDs. */ }
  const blocked = logicalUnits.units
    .filter(unit => unit.activityId === activityId)
    .map(unit => unit.eligibility)
    .find(item => !item.eligible);
  return {
    showControls: false,
    backwardEnabled: false,
    forwardEnabled: false,
    guidance: blocked?.guidance || '',
  };
}

export function buildTimelineView(snapshot, plannerState, eligibilityFor, visualPreferences) {
  if (!snapshot || !Array.isArray(snapshot.activities) || !Array.isArray(snapshot.precedences)) fail('instantánea inválida');
  if (typeof eligibilityFor !== 'function') fail('elegibilidad inválida');
  const horizon = horizonInfo(plannerState);
  const fragments = plannerState.model.fragments;
  if (!Array.isArray(fragments)) fail('modelo del planificador inválido');
  const preferences = {
    showCompleted: visualPreferences?.showCompleted !== false,
    showPrecedences: visualPreferences?.showPrecedences !== false,
    visibleAttributes: Array.isArray(visualPreferences?.visibleAttributes) ? [...visualPreferences.visibleAttributes] : [],
    sortByStart: visualPreferences?.sortByStart === true,
    sortByType: visualPreferences?.sortByType === true,
    sortByAttribute: visualPreferences?.sortByAttribute === true,
    sortAttribute: typeof visualPreferences?.sortAttribute === 'string' ? visualPreferences.sortAttribute : '',
    sortDirection: visualPreferences?.sortDirection === 'desc' ? 'desc' : 'asc',
  };
  const additionalVisibleActivityIds = new Set(
    (Array.isArray(visualPreferences?.additionalVisibleActivityIds)
      ? visualPreferences.additionalVisibleActivityIds : [])
      .map(value => String(value || '')).filter(Boolean),
  );
  const protection = splitProtection(fragments);
  const logicalUnits = logicalUnitProjection(fragments, eligibilityFor);
  const grouped = fragmentsByActivity(fragments);
  const rowStatuses = new Set();
  const rows = snapshot.activities.map((activity, sourceIndex) => {
    const activityId = String(activity.id);
    const { label, humanName } = activityIdentity(activity);
    const type = activityType(activity);
    const resource = activityResource(activity);
    const rate = activityRate(activity);
    const activityFragments = grouped.get(activityId) || [];
    const days = new Map();
    let visibleStart = null;
    for (const item of activityFragments) {
      if (completed(item) && !preferences.showCompleted) continue;
      if (item.displaySuppressed === true || item.display_suppressed === true) continue;
      for (const group of segmentGroups(item, horizon, protection, label, rate, logicalUnits.byFragment.get(item.id))) {
        if (!days.has(group.date)) days.set(group.date, { date: group.date, dayIndex: dayNumber(...Object.values(parseDate(group.date))) - horizon.startDay, segments: [] });
        days.get(group.date).segments.push(group.segment);
        const segmentStart = scalar(group.segment.start);
        if (visibleStart === null || segmentStart < visibleStart) visibleStart = segmentStart;
        rowStatuses.add(group.segment.status);
      }
    }
    if (!days.size && !additionalVisibleActivityIds.has(activityId)) return null;
    const attributes = preferences.visibleAttributes
      .filter(name => own(activity.grid || {}, name) && typeof activity.grid[name] === 'string' && activity.grid[name] !== '')
      .map(name => ({ name, value: activity.grid[name] }));
    const sortValue = preferences.sortByAttribute && typeof activity.grid?.[preferences.sortAttribute] === 'string'
      ? activity.grid[preferences.sortAttribute] : '';
    return {
      activityId, label, humanName, activityType: type, resource, attributes,
      days: [...days.values()].sort((left, right) => left.dayIndex - right.dayIndex),
      eligibility: legacyActivityEligibility(eligibilityFor, activityId, logicalUnits),
      sourceIndex,
      visibleStart,
      sortValue,
    };
  }).filter(Boolean);
  rows.sort((left, right) => {
    if (preferences.sortByType) {
      const typeOrder = left.activityType.localeCompare(right.activityType, 'es', { sensitivity: 'base' });
      if (typeOrder) return typeOrder;
    }
    if (preferences.sortByAttribute) {
      const attributeOrder = compareAttributeValues(left.sortValue, right.sortValue, preferences.sortDirection);
      if (attributeOrder) return attributeOrder;
    }
    if (preferences.sortByStart && left.visibleStart !== right.visibleStart) return left.visibleStart < right.visibleStart ? -1 : 1;
    return left.sourceIndex - right.sourceIndex;
  });
  for (const row of rows) {
    delete row.sourceIndex;
    delete row.visibleStart;
    delete row.sortValue;
  }
  const visibleRows = new Map(rows.map(row => [row.activityId, row]));
  const precedence = precedenceLines(snapshot, fragments, preferences.showPrecedences, visibleRows);
  const continuity = continuityLines(fragments, visibleRows);
  const overlayStatuses = new Set([
    ...(precedence.length ? ['precedence'] : []),
    ...(continuity.length ? ['continuity'] : []),
  ]);
  const activeStatuses = new Set([...rowStatuses, ...overlayStatuses]);
  const distinct = status => new Set(rows
    .filter(row => row.days.some(day => day.segments.some(segment => segment.status === status)))
    .map(row => row.activityId)).size;
  const view = {
    days: headers(horizon), rows,
    precedenceLines: precedence, continuityLines: continuity,
    metrics: {
      visibleActivities: rows.length,
      pendingActivities: new Set(rows
        .filter(row => row.days.some(day => day.segments.some(segment => segment.status !== 'completed')))
        .map(row => row.activityId)).size,
      directMovements: distinct('direct'), cascadeMovements: distinct('cascade'),
    },
    legend: Object.keys(LEGEND_LABELS).filter(status => activeStatuses.has(status)).map(status => ({ status, label: LEGEND_LABELS[status] })),
    diagnostics: diagnostics(fragments, horizon),
    relationships: relationshipDiagnostics(snapshot.precedences),
  };
  projectionContexts.set(view, {
    snapshot, plannerState, eligibilityFor, visualPreferences,
  });
  return view;
}

export function civilDragDelta(originDay, targetDay) {
  const origin = parseDate(originDay), target = parseDate(targetDay);
  const delta = dayNumber(target.year, target.month, target.day) - dayNumber(origin.year, origin.month, origin.day);
  if (!Number.isInteger(delta) || delta === 0) fail('las fechas civiles deben ser distintas');
  return delta;
}

export function previewWholePendingMove(timelineView, activityId, deltaDays) {
  if (!Number.isInteger(deltaDays)) fail('el desplazamiento debe ser un entero de días civiles');
  if (!timelineView || !Array.isArray(timelineView.rows)) fail('vista de cronograma inválida');
  const row = timelineView.rows.find(item => item.activityId === String(activityId));
  if (!row) fail(`actividad no encontrada: ${activityId}`);
  const segments = row.days.flatMap(day => day.segments)
    .filter(segment => segment.status !== 'completed')
    .map(segment => ({
      ...segment,
      date: addDateDays(segment.date, deltaDays),
      start: addTimestampDays(segment.start, deltaDays),
      finish: addTimestampDays(segment.finish, deltaDays),
      preview: true,
    }));
  if (!segments.length) fail(`actividad sin trabajo pendiente: ${activityId}`);
  return { activityId: String(activityId), deltaDays, segments };
}

function immutablePreviewCopy(value) {
  if (Array.isArray(value)) return Object.freeze(value.map(immutablePreviewCopy));
  if (!value || typeof value !== 'object') return value;
  const copy = {};
  for (const [key, item] of Object.entries(value)) copy[key] = immutablePreviewCopy(item);
  return Object.freeze(copy);
}

export function captureVisibleDailyTail(timelineView, request) {
  if (!timelineView || !Array.isArray(timelineView.days) || !Array.isArray(timelineView.rows)) fail('vista de cronograma inválida');
  if (!request || typeof request !== 'object' || Array.isArray(request)
      || typeof request.activityId !== 'string' || !request.activityId
      || typeof request.fragmentId !== 'string' || !request.fragmentId) fail('solicitud de vista previa inválida');
  const row = timelineView.rows.find(item => item.activityId === request.activityId);
  if (!row || !Array.isArray(row.days)) fail(`actividad no encontrada: ${request.activityId}`);
  const selected = row.days.flatMap(day => day.segments || [])
    .find(segment => segment.fragmentId === request.fragmentId && segment.status !== 'completed');
  if (!selected) fail(`fragmento desconocido o completado: ${request.fragmentId}`);
  if (typeof selected.logicalUnitId !== 'string' || typeof selected.logicalUnitDate !== 'string') fail('segmento sin unidad lógica');
  const dayDates = timelineView.days.map(day => day.date);
  const dayIndexByDate = Object.create(null);
  dayDates.forEach((date, index) => { dayIndexByDate[date] = index; });
  const originIndex = dayIndexByDate[selected.logicalUnitDate];
  if (!Number.isInteger(originIndex)) fail('fecha origen fuera del horizonte visible');
  const visibleUnits = new Map();
  for (const day of row.days) {
    for (const segment of day.segments || []) {
      if (typeof segment.logicalUnitId !== 'string'
          || typeof segment.logicalUnitDate !== 'string' || segment.logicalUnitDate < selected.logicalUnitDate) continue;
      if (!visibleUnits.has(segment.logicalUnitId)) {
        visibleUnits.set(segment.logicalUnitId, {
          activityId: request.activityId,
          logicalUnitId: segment.logicalUnitId,
          logicalUnitDate: segment.logicalUnitDate,
          segments: [],
          hasCompletedSegment: false,
        });
      }
      const unit = visibleUnits.get(segment.logicalUnitId);
      unit.segments.push(segment);
      unit.hasCompletedSegment ||= segment.status === 'completed';
    }
  }
  if (!visibleUnits.has(selected.logicalUnitId) || visibleUnits.get(selected.logicalUnitId).hasCompletedSegment) {
    fail(`unidad lógica completada o no elegible: ${request.fragmentId}`);
  }
  const movableUnits = [...visibleUnits.values()]
    .filter(unit => !unit.hasCompletedSegment)
    .map(unit => immutablePreviewCopy({
      activityId: unit.activityId,
      logicalUnitId: unit.logicalUnitId,
      logicalUnitDate: unit.logicalUnitDate,
      segments: unit.segments,
    }));
  return Object.freeze({
    kind: 'visible-daily-tail',
    activityId: request.activityId,
    fragmentId: request.fragmentId,
    logicalUnitId: selected.logicalUnitId,
    originIndex,
    dayDates: Object.freeze([...dayDates]),
    dayIndexByDate: Object.freeze(dayIndexByDate),
    movableUnits: Object.freeze(movableUnits),
    suppressedLogicalUnitIds: Object.freeze(movableUnits.map(unit => unit.logicalUnitId)),
  });
}

export function previewVisibleDailyTail(tailSnapshot, request) {
  if (!tailSnapshot || tailSnapshot.kind !== 'visible-daily-tail'
      || !Array.isArray(tailSnapshot.movableUnits)
      || !tailSnapshot.dayIndexByDate || !Number.isInteger(tailSnapshot.originIndex)) {
    fail('fotografía de cola visible inválida');
  }
  if (!request || typeof request !== 'object' || Array.isArray(request)
      || typeof request.targetDate !== 'string') fail('solicitud de vista previa inválida');
  const targetIndex = tailSnapshot.dayIndexByDate[request.targetDate];
  if (!Number.isInteger(targetIndex)) fail('fecha destino fuera del horizonte visible');
  const deltaDays = targetIndex - tailSnapshot.originIndex;
  const segments = [], hiddenLogicalUnits = [];
  for (const unit of tailSnapshot.movableUnits) {
    const logicalUnitDate = addDateDays(unit.logicalUnitDate, deltaDays);
    const previewClass = unit.logicalUnitId === tailSnapshot.logicalUnitId
      ? 'drag-preview-selected' : 'drag-preview-queued';
    if (!Number.isInteger(tailSnapshot.dayIndexByDate[logicalUnitDate])) {
      hiddenLogicalUnits.push({
        activityId: unit.activityId, logicalUnitId: unit.logicalUnitId, logicalUnitDate, previewClass,
      });
      continue;
    }
    for (const segment of unit.segments) {
      segments.push({
        ...segment,
        date: addDateDays(segment.date, deltaDays),
        start: addTimestampDays(segment.start, deltaDays),
        finish: addTimestampDays(segment.finish, deltaDays),
        logicalUnitDate,
        preview: true,
        previewClass,
      });
    }
  }
  return {
    activityId: tailSnapshot.activityId,
    fragmentId: tailSnapshot.fragmentId,
    logicalUnitId: tailSnapshot.logicalUnitId,
    targetDate: request.targetDate,
    segments,
    hiddenLogicalUnits,
    suppressedLogicalUnitIds: tailSnapshot.suppressedLogicalUnitIds,
    warnings: [],
    hasNonBlockingWarning: false,
  };
}

function previewExcesses(view, preferences) {
  const typeTotals = new Map(), resourceActivities = new Map();
  for (const row of view.rows || []) {
    for (const day of row.days || []) {
      const active = day.segments.filter(segment => segment.status !== 'completed');
      if (!active.length) continue;
      const typeKey = `${row.activityType}\u0000${day.date}`;
      const amount = active.reduce((total, segment) => total
        + (Number.isFinite(segment.scheduledAmount) ? segment.scheduledAmount : segment.widthRatio), 0);
      typeTotals.set(typeKey, (typeTotals.get(typeKey) || 0) + amount);
      const resourceKey = `${row.resource}\u0000${day.date}`;
      if (!resourceActivities.has(resourceKey)) resourceActivities.set(resourceKey, new Set());
      resourceActivities.get(resourceKey).add(row.activityId);
    }
  }
  const warnings = [];
  for (const [key, amount] of typeTotals) {
    const [label, date] = key.split('\u0000'), objective = Number(preferences?.typeObjectives?.[label]);
    if (Number.isFinite(objective) && objective >= 0 && amount > objective) {
      warnings.push({ kind: 'capacity', date, label, blocking: false, message: `Capacidad excedida para ${label}.` });
    }
  }
  for (const [key, activities] of resourceActivities) {
    const [label, date] = key.split('\u0000');
    const configured = preferences?.resourceCapacities?.[label] ?? preferences?.resourceFronts?.[label] ?? preferences?.dailyFronts;
    const capacity = Number(configured);
    if (Number.isFinite(capacity) && capacity >= 0 && activities.size > capacity) {
      warnings.push({ kind: 'fronts', date, label, blocking: false, message: `Frentes excedidos para ${label}.` });
    }
  }
  return warnings;
}

export function previewDailyTail(timelineView, candidatePlannerState, request, eligibilityFor, visualPreferences) {
  if (!timelineView || !Array.isArray(timelineView.rows)) fail('vista de cronograma inválida');
  const context = projectionContexts.get(timelineView);
  if (!context) fail('vista de cronograma sin contexto de proyección');
  if (!candidatePlannerState?.model || !Array.isArray(candidatePlannerState.model.fragments)) fail('estado candidato inválido');
  if (!request || typeof request !== 'object' || Array.isArray(request)
      || typeof request.activityId !== 'string' || !request.activityId
      || typeof request.fragmentId !== 'string' || !request.fragmentId
      || typeof request.targetDate !== 'string') fail('solicitud de vista previa inválida');
  parseDate(request.targetDate);
  if (!timelineView.days.some(day => day.date === request.targetDate)) fail('fecha destino fuera del horizonte visible');
  const projectEligibility = typeof eligibilityFor === 'function' ? eligibilityFor : context.eligibilityFor;
  const preferences = visualPreferences ?? context.visualPreferences;
  const currentFragments = context.plannerState.model.fragments;
  const candidateFragments = candidatePlannerState.model.fragments;
  const currentById = new Map(currentFragments.map(fragment => [fragment.id, fragment]));
  if (candidateFragments.length !== currentFragments.length
      || candidateFragments.some(fragment => !currentById.has(fragment.id))) fail('modelo candidato incompatible');
  const currentLogical = logicalUnitProjection(currentFragments, projectEligibility);
  const candidateLogical = logicalUnitProjection(candidateFragments, projectEligibility);
  const selected = currentLogical.byFragment.get(request.fragmentId);
  if (!selected) fail(`fragmento desconocido: ${request.fragmentId}`);
  if (selected.activityId !== request.activityId) fail(`fragmento ajeno a actividad ${request.activityId}`);
  const selectedCandidate = candidateLogical.byFragment.get(request.fragmentId);
  if (!selectedCandidate || selectedCandidate.currentDate !== request.targetDate) fail('el candidato no contiene la inserción solicitada');

  const changedFragmentIds = new Set(candidateFragments.filter(fragment => {
    const current = currentById.get(fragment.id);
    return current.start !== fragment.start || current.finish !== fragment.finish;
  }).map(fragment => fragment.id));
  if (!changedFragmentIds.has(request.fragmentId)) fail('el candidato no contiene movimientos');
  const affectedIds = new Set([...changedFragmentIds].map(id => candidateLogical.byFragment.get(id)?.logicalUnitId).filter(Boolean));
  const candidateView = buildTimelineView(
    context.snapshot, candidatePlannerState, projectEligibility, preferences,
  );
  const baselineWarnings = new Set(previewExcesses(timelineView, preferences)
    .map(item => `${item.kind}\u0000${item.label}\u0000${item.date}`));
  const warnings = previewExcesses(candidateView, preferences)
    .filter(item => !baselineWarnings.has(`${item.kind}\u0000${item.label}\u0000${item.date}`));
  const warningDates = new Set(warnings.map(item => item.date));
  const visibleSegments = [];
  for (const row of candidateView.rows) {
    for (const day of row.days) {
      for (const segment of day.segments) {
        if (!affectedIds.has(segment.logicalUnitId)) continue;
        const previewClass = segment.logicalUnitId === selected.logicalUnitId
          ? 'drag-preview-selected' : 'drag-preview-queued';
        Object.assign(segment, {
          activityId: row.activityId,
          preview: true,
          previewClass,
          previewWarning: warningDates.has(segment.date),
        });
        visibleSegments.push({ ...segment, eligibility: { ...segment.eligibility } });
      }
    }
  }
  const visibleUnitIds = new Set(visibleSegments.map(segment => segment.logicalUnitId));
  const hiddenLogicalUnits = [...affectedIds].filter(id => !visibleUnitIds.has(id)).map(id => {
    const unit = candidateLogical.units.find(item => item.logicalUnitId === id);
    return {
      activityId: unit.activityId,
      logicalUnitId: id,
      logicalUnitDate: unit.currentDate,
      previewClass: id === selected.logicalUnitId ? 'drag-preview-selected' : 'drag-preview-queued',
    };
  });
  const projectedLogicalUnitDates = Object.fromEntries(candidateFragments
    .filter(fragment => String(fragment.activityId ?? fragment.activity_id ?? '') === request.activityId)
    .map(fragment => [fragment.id, candidateLogical.byFragment.get(fragment.id)?.currentDate ?? fragment.start.slice(0, 10)]));
  return {
    activityId: request.activityId,
    fragmentId: request.fragmentId,
    logicalUnitId: selected.logicalUnitId,
    targetDate: request.targetDate,
    segments: visibleSegments,
    hiddenLogicalUnits,
    projectedLogicalUnitDates,
    suppressedLogicalUnitIds: [...affectedIds],
    warnings,
    hasNonBlockingWarning: warnings.length > 0,
    view: candidateView,
  };
}

export function refreshDecision(plannerState) {
  const fragments = plannerState?.model?.fragments;
  const dirty = Array.isArray(fragments) && fragments.some(fragment => fragment.provenance === 'direct' || fragment.provenance === 'cascade');
  return dirty
    ? { requiresConfirmation: true, message: 'Actualizar descartará los movimientos locales de STP. ¿Continuar?' }
    : { requiresConfirmation: false, message: '' };
}

export function humanPlannerOutcome(outcome) {
  const messages = {
    'horizon-range': 'El movimiento saldría del horizonte visible. Extienda el rango o reduzca el desplazamiento.',
    'civil-range': 'El movimiento excede el rango civil compatible (años 0001 a 9999).',
    'completed-work': 'El avance completado está protegido y no se puede mover.',
    'incoming-precedence': 'El movimiento viola una precedencia entrante. Revise las actividades predecesoras.',
    'native-anchor': 'El movimiento cruzaría una división nativa protegida.',
    'invalid-move': 'El movimiento no es válido. Revise el horizonte y la actividad seleccionada.',
  };
  if (outcome?.ok && typeof outcome.message === 'string' && outcome.message) return outcome.message;
  if (typeof outcome?.code === 'string' && messages[outcome.code]) return messages[outcome.code];
  if (typeof outcome?.message === 'string' && outcome.message) return outcome.message;
  return 'No se pudo completar la operación solicitada.';
}
