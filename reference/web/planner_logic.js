/* Pure civil-time planning rules for the isolated STP V2 browser module. */

const DAY_US = 86_400_000_000n;
const CIVIL = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,6}))?)?)?$/;
const HORIZON_DIRECTION_GUIDANCE = 'Extienda el rango para habilitar esta dirección.';
const CIVIL_RANGE_DIRECTION_GUIDANCE = 'Movimiento no disponible: esta dirección excede el rango civil compatible (años 0001 a 9999).';
const UNAVAILABLE_DIRECTION_GUIDANCE = 'Movimiento no disponible en esta dirección.';
const MAX_HORIZON_PREFERENCE_BYTES = 1024;

function fail(message, code = '') { const error = new Error(message); if (code) error.code = code; throw error; }

function civilParts(value) {
  if (typeof value !== 'string') fail('fecha civil inválida');
  const match = CIVIL.exec(value);
  if (!match) fail(`fecha civil inválida: ${value}`);
  const [, yy, mm, dd, hh = '00', mi = '00', ss = '00', fraction = ''] = match;
  const year = Number(yy), month = Number(mm), day = Number(dd);
  const hour = Number(hh), minute = Number(mi), second = Number(ss);
  if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) fail(`fecha civil inválida: ${value}`);
  return { year, month, day, hour, minute, second, micros: Number((fraction + '000000').slice(0, 6)), hasTime: match[4] !== undefined };
}

function daysInMonth(year, month) { return month === 2 ? (year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28) : ([4, 6, 9, 11].includes(month) ? 30 : 31); }
function dayNumber(year, month, day) {
  const adjustedYear = year - (month <= 2 ? 1 : 0);
  const era = Math.floor(adjustedYear / 400);
  const yoe = adjustedYear - era * 400;
  const mp = month + (month > 2 ? -3 : 9);
  return era * 146097 + Math.floor((yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + Math.floor((153 * mp + 2) / 5) + day - 1));
}
function dateFromDayNumber(number) {
  const era = Math.floor(number / 146097), doe = number - era * 146097;
  const yoe = Math.floor((doe - Math.floor(doe / 1460) + Math.floor(doe / 36524) - Math.floor(doe / 146096)) / 365);
  const y = yoe + era * 400, doy = doe - (365 * yoe + Math.floor(yoe / 4) - Math.floor(yoe / 100));
  const mp = Math.floor((5 * doy + 2) / 153), d = doy - Math.floor((153 * mp + 2) / 5) + 1;
  return { year: y + (mp >= 10 ? 1 : 0), month: mp + (mp < 10 ? 3 : -9), day: d };
}
function civilScalar(value) { const part = civilParts(value); return BigInt(dayNumber(part.year, part.month, part.day)) * DAY_US + BigInt((part.hour * 3600 + part.minute * 60 + part.second) * 1_000_000 + part.micros); }
function civilDate(value) {
  const part = civilParts(value), pad = (number, width = 2) => String(number).padStart(width, '0');
  return `${pad(part.year, 4)}-${pad(part.month)}-${pad(part.day)}`;
}
function addCivilDays(value, days) {
  const part = civilParts(value), date = dateFromDayNumber(dayNumber(part.year, part.month, part.day) + days);
  const pad = (number, width = 2) => String(number).padStart(width, '0');
  if (date.year < 1 || date.year > 9999) fail('fecha civil fuera del rango compatible (años 0001 a 9999)', 'CIVIL_RANGE');
  const dateText = `${pad(date.year, 4)}-${pad(date.month)}-${pad(date.day)}`;
  if (!part.hasTime) return dateText;
  const fraction = part.micros ? `.${pad(part.micros, 6).replace(/0+$/, '')}` : '';
  return `${dateText}T${pad(part.hour)}:${pad(part.minute)}:${pad(part.second)}${fraction}`;
}
function midnightText(value) {
  const part = civilParts(value);
  if (!part.hasTime || part.hour !== 0 || part.minute !== 0 || part.second !== 0 || part.micros !== 0) fail('horizonte civil inválido');
  return `${String(part.year).padStart(4, '0')}-${String(part.month).padStart(2, '0')}-${String(part.day).padStart(2, '0')}T00:00:00`;
}
function durationDays(value) {
  if (!Number.isInteger(value) || value < 1 || value > 366) fail('duración de horizonte inválida');
  return value;
}
function horizonDayCount(start, finish) {
  const first = civilParts(start), last = civilParts(finish);
  return dayNumber(last.year, last.month, last.day) - dayNumber(first.year, first.month, first.day);
}
function horizonStorageKey(projectKey) {
  if (typeof projectKey !== 'string' || !projectKey || projectKey.trim() !== projectKey || projectKey.length > 256 || /[\u0000-\u001f\u007f]/.test(projectKey)) fail('clave de proyecto inválida');
  return `vgs-stp-v2:horizon:${projectKey}`;
}
function horizonPreferenceFallback(warning = '') { return { version: 3, startDay: null, durationDays: 28, migrated: false, warning }; }
function preferenceBytes(value) { return new TextEncoder().encode(value).length; }
function parsePreferenceStartDay(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) fail('preferencia inválida');
  const normalized = midnightText(`${value}T00:00:00`);
  return normalized.slice(0, 10);
}
function normalizeHorizonPreference(horizon) {
  const keys = horizon && typeof horizon === 'object' ? Object.keys(horizon).sort().join(',') : '';
  if (keys !== 'durationDays,finish,start') fail('preferencia de horizonte inválida');
  const start = midnightText(horizon.start), finish = midnightText(horizon.finish), duration = durationDays(horizon.durationDays);
  if (horizonDayCount(start, finish) !== duration) fail('preferencia de horizonte inválida');
  return { startDay: start.slice(0, 10), durationDays: duration };
}

export function deriveCivilHorizon(model, durationValue = 28) {
  const duration = durationDays(durationValue);
  const normalized = normalizeModel(model);
  const pending = (normalized.fragments || []).filter(fragment => !lockedFragment(fragment));
  if (!pending.length) fail('no hay trabajo pendiente para planificar');
  let earliest = pending[0].originalStart;
  for (const fragment of pending.slice(1)) if (civilScalar(fragment.originalStart) < civilScalar(earliest)) earliest = fragment.originalStart;
  const part = civilParts(earliest);
  const horizonStart = `${String(part.year).padStart(4, '0')}-${String(part.month).padStart(2, '0')}-${String(part.day).padStart(2, '0')}T00:00:00`;
  return { horizonStart, horizonFinish: addCivilDays(horizonStart, duration), durationDays: duration };
}

export function validateCivilHorizon(model, horizon) {
  const keys = horizon && typeof horizon === 'object' ? Object.keys(horizon).sort() : [];
  const keyShape = keys.join(',');
  if (keyShape !== 'horizonFinish,horizonStart' && keyShape !== 'durationDays,horizonFinish,horizonStart') fail('horizonte civil inválido');
  let horizonStart, horizonFinish;
  try { horizonStart = midnightText(horizon.horizonStart); horizonFinish = midnightText(horizon.horizonFinish); }
  catch (_) { fail('horizonte civil inválido'); }
  const duration = horizonDayCount(horizonStart, horizonFinish);
  if (duration < 1 || duration > 366) fail('horizonte civil inválido');
  const normalized = normalizeModel(model);
  const lower = civilScalar(horizonStart), upper = civilScalar(horizonFinish);
  if (!(normalized.fragments || []).some(fragment => {
    if (lockedFragment(fragment)) return false;
    const start = fragment.start ?? fragment.originalStart;
    const finish = fragment.finish ?? fragment.originalFinish;
    return civilScalar(finish) > lower && civilScalar(start) < upper;
  })) fail('horizonte sin trabajo pendiente');
  return { horizonStart, horizonFinish, durationDays: duration };
}

export function loadHorizonPreference(storage, projectKey) {
  let key;
  try { key = horizonStorageKey(projectKey); }
  catch (_) { return horizonPreferenceFallback('No se pudo leer la preferencia de horizonte.'); }
  let raw;
  try { raw = storage.getItem(key); }
  catch (_) { return horizonPreferenceFallback('No se pudo leer la preferencia de horizonte.'); }
  if (raw === null) return horizonPreferenceFallback();
  try {
    if (typeof raw !== 'string' || preferenceBytes(raw) > MAX_HORIZON_PREFERENCE_BYTES) throw new Error('invalid');
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) || !Number.isInteger(parsed.duration_days) || parsed.duration_days < 1 || parsed.duration_days > 366) throw new Error('invalid');
    const keys = Object.keys(parsed).sort().join(',');
    if (parsed.version === 2 && keys === 'duration_days,version') return { version: 3, startDay: null, durationDays: parsed.duration_days, migrated: true, warning: '' };
    if (parsed.version === 3 && keys === 'duration_days,start_day,version') return { version: 3, startDay: parsePreferenceStartDay(parsed.start_day), durationDays: parsed.duration_days, migrated: false, warning: '' };
    throw new Error('invalid');
  } catch (_) { return horizonPreferenceFallback('Preferencia de horizonte inválida; se usarán 28 días.'); }
}

export function saveHorizonPreference(storage, projectKey, horizon) {
  const preference = normalizeHorizonPreference(horizon);
  let key;
  try { key = horizonStorageKey(projectKey); }
  catch (_) { return { persisted: false, ...preference, warning: 'No se pudo guardar la preferencia de horizonte.' }; }
  try {
    storage.setItem(key, JSON.stringify({ version: 3, start_day: preference.startDay, duration_days: preference.durationDays }));
    return { persisted: true, ...preference, warning: '' };
  } catch (_) { return { persisted: false, ...preference, warning: 'No se pudo guardar la preferencia de horizonte.' }; }
}

export function acquireHorizonStorage(readStorage) {
  try {
    const storage = typeof readStorage === 'function' ? readStorage() : readStorage;
    if (!storage || typeof storage.getItem !== 'function' || typeof storage.setItem !== 'function') throw new Error('storage unavailable');
    return storage;
  } catch (_) {
    return { getItem() { throw new Error('storage unavailable'); }, setItem() { throw new Error('storage unavailable'); } };
  }
}

export function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

export function buildPlannerModel(snapshot) {
  if (!snapshot || !Array.isArray(snapshot.activities) || !Array.isArray(snapshot.precedences)) {
    fail('instantánea V2 inválida', 'SNAPSHOT_INVALID');
  }
  return {
    activities: snapshot.activities.map(activity => ({
      id: activity.id,
      nativeBars: activity.nativeBars.map(bar => ({ ...bar })),
    })),
    precedences: snapshot.precedences.map(link => ({ ...link })),
    fragments: snapshot.activities.flatMap(activity => activity.fragments.map(fragment => {
      const before = activity.nativeAnchors.find(anchor =>
        anchor.rightBarKey === fragment.nativeBarKey && anchor.rightAt === fragment.start);
      const after = activity.nativeAnchors.find(anchor =>
        anchor.leftBarKey === fragment.nativeBarKey && anchor.at === fragment.finish);
      return {
        ...fragment,
        activityId: activity.id,
        provenance: '',
        anchorBefore: before ? before.rightAt : '',
        anchorAfter: after ? after.at : '',
      };
    })),
  };
}

export function validatePlannerModel(model) {
  return clone(preflight(model, true));
}

export function preexistingPrecedenceConflicts(model) {
  const normalized = preflight(model, true);
  const baseline = originalSchedule(normalized);
  return (baseline.precedences || []).flatMap(link => requiredShift(baseline, link) > 0n ? [{
    key: String(linkValue(link, 'key') ?? ''),
    predecessorId: String(linkValue(link, 'predecessorId', 'predecessor_id')),
    successorId: String(linkValue(link, 'successorId', 'successor_id')),
  }] : []);
}

export function assessPrecedenceConflicts(model, policy = 'block-inherited') {
  const inherited = preexistingPrecedenceConflicts(model);
  return {
    inherited,
    blocksOperations: inherited.length > 0 && policy !== 'allow-inherited',
  };
}

function outcomeCode(error) {
  if (error?.code === 'CIVIL_RANGE') return 'civil-range';
  if (error?.code === 'HORIZON_RANGE') return 'horizon-range';
  const message = String(error?.message || '');
  if (/completad[oa]|avance completado/i.test(message)) return 'completed-work';
  if (/precedencia|predecesor/i.test(message)) return 'incoming-precedence';
  if (/ancla|límite nativo/i.test(message)) return 'native-anchor';
  return 'invalid-move';
}

export function createCivilHorizonController(modelFactory, projectKey, storage, initialHorizon = null) {
  if (typeof modelFactory !== 'function') fail('fábrica de modelo inválida');
  const makeState = (seedHorizon = null) => {
    const model = validatePlannerModel(modelFactory());
    const preference = loadHorizonPreference(storage, projectKey);
    const pending = (model.fragments || []).some(fragment => !lockedFragment(fragment));
    let horizon = null;
    if (pending && seedHorizon) {
      horizon = validateCivilHorizon(model, seedHorizon);
    } else if (pending && preference.startDay) {
      try {
        const start = `${preference.startDay}T00:00:00`;
        horizon = validateCivilHorizon(model, { horizonStart: start, horizonFinish: addCivilDays(start, preference.durationDays) });
      } catch (_) { horizon = null; }
    }
    if (pending && !horizon) horizon = deriveCivilHorizon(model, preference.durationDays);
    return { model, horizon, horizonLocked: false, hasLocalMovements: false, warning: seedHorizon ? '' : preference.warning };
  };
  let current = makeState(initialHorizon);
  const result = (ok, message, code = '') => ({ ok, message, code });
  const dateStart = value => `${value}T00:00:00`;
  const hasPending = () => (current.model.fragments || []).some(fragment => !lockedFragment(fragment));
  const modelHasLocalMovements = model => (model.fragments || []).some(fragment =>
    ['direct', 'cascade'].includes(fragment.provenance)
      && (fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish));
  const horizonDisplay = () => current.horizon && {
    start: current.horizon.horizonStart.slice(0, 10),
    finish: addCivilDays(current.horizon.horizonFinish, -1).slice(0, 10),
    durationDays: current.horizon.durationDays
  };
  const commit = candidateFactory => {
    if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
    if (modelHasLocalMovements(current.model)) return result(false, 'El rango no se puede cambiar mientras hay movimientos locales.', 'local-changes');
    try {
      const horizon = validateCivilHorizon(current.model, candidateFactory());
      current = { ...current, horizon, warning: '' };
      return result(true, 'Horizonte civil actualizado.');
    } catch (error) { return result(false, error.message, outcomeCode(error)); }
  };
  const directionFits = (activityIdValue, days) => {
    try {
      moveActivity(current.model, activityIdValue, days, current.horizon);
      return { enabled: true, reason: '', guidance: '' };
    } catch (error) {
      if (error && error.code === 'CIVIL_RANGE') return { enabled: false, reason: 'civil-range', guidance: CIVIL_RANGE_DIRECTION_GUIDANCE };
      if (error && error.code === 'HORIZON_RANGE') return { enabled: false, reason: 'horizon', guidance: HORIZON_DIRECTION_GUIDANCE };
      return { enabled: false, reason: 'unavailable', guidance: UNAVAILABLE_DIRECTION_GUIDANCE };
    }
  };
  const performMove = (activityIdValue, days) => {
    if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
    try {
      const model = moveActivity(current.model, activityIdValue, days, current.horizon);
      const dirty = modelHasLocalMovements(model);
      current = { ...current, model, horizonLocked: dirty, hasLocalMovements: dirty, warning: '' };
      return result(true, 'Movimiento validado.');
    } catch (error) { return result(false, error.message, outcomeCode(error)); }
  };
  const performDailyTailMove = (activityIdValue, fragmentIdValue, targetDate) => {
    if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
    try {
      const target = civilScalar(`${targetDate}T00:00:00`);
      const lower = civilScalar(current.horizon.horizonStart), upper = civilScalar(current.horizon.horizonFinish);
      if (target < lower || target >= upper) fail('fecha destino fuera del horizonte visible');
      const model = moveDailyTail(current.model, {
        activityId: activityIdValue, fragmentId: fragmentIdValue, targetDate,
      });
      const dirty = modelHasLocalMovements(model);
      current = { ...current, model, horizonLocked: dirty, hasLocalMovements: dirty, warning: '' };
      return result(true, 'Movimiento validado.');
    } catch (error) { return result(false, error.message, outcomeCode(error)); }
  };
  const deferDailyTailToNextPeriod = (activityIdValue, fragmentIdValue) => {
    if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
    try {
      const targetDate = current.horizon.horizonFinish.slice(0, 10);
      const model = moveDailyTail(current.model, {
        activityId: activityIdValue, fragmentId: fragmentIdValue, targetDate,
      });
      const dirty = modelHasLocalMovements(model);
      current = { ...current, model, horizonLocked: dirty, hasLocalMovements: dirty, warning: '' };
      return result(
        true,
        `La cola diaria se movió al siguiente periodo, a partir del ${targetDate.slice(8, 10)}/${targetDate.slice(5, 7)}.`,
      );
    } catch (error) { return result(false, error.message, outcomeCode(error)); }
  };
  const performDailyTailStagger = (activityIdValue, fragmentIdValue, divisions, gapDays) => {
    if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
    try {
      const model = staggerDailyTail(current.model, {
        activityId: activityIdValue, fragmentId: fragmentIdValue, divisions, gapDays,
      }, current.horizon);
      const dirty = modelHasLocalMovements(model);
      current = { ...current, model, horizonLocked: dirty, hasLocalMovements: dirty, warning: '' };
      return result(
        true,
        `Se insertaron ${divisions} desfases de ${gapDays} ${gapDays === 1 ? 'día' : 'días'}.`,
      );
    } catch (error) { return result(false, error.message, outcomeCode(error)); }
  };
  return {
    state() {
      const noPending = !hasPending();
      const dirty = modelHasLocalMovements(current.model);
      return {
        ...current,
        horizonLocked: dirty,
        hasLocalMovements: dirty,
        displayHorizon: horizonDisplay(),
        hasPending: !noPending,
        horizonControlsDisabled: !current.horizon,
        canDownload: Boolean(current.horizon && dirty),
        status: noPending ? 'No hay trabajo pendiente para planificar.' : current.warning
      };
    },
    editStart(value) {
      return commit(() => {
        const start = dateStart(value);
        return { horizonStart: start, horizonFinish: addCivilDays(start, current.horizon.durationDays) };
      });
    },
    editFinish(value) {
      return commit(() => ({ horizonStart: current.horizon.horizonStart, horizonFinish: addCivilDays(dateStart(value), 1) }));
    },
    editDuration(value) {
      return commit(() => ({ horizonStart: current.horizon.horizonStart, horizonFinish: addCivilDays(current.horizon.horizonStart, Number(value)) }));
    },
    savePreference() {
      if (!current.horizon) return result(false, 'No hay trabajo pendiente para planificar.', 'invalid-move');
      const saved = saveHorizonPreference(storage, projectKey, {
        start: current.horizon.horizonStart, finish: current.horizon.horizonFinish, durationDays: current.horizon.durationDays
      });
      return result(saved.persisted, saved.warning || 'Configuración de horizonte guardada.', saved.persisted ? '' : 'invalid-move');
    },
    save() { return this.savePreference(); },
    moveActivity: performMove,
    move: performMove,
    moveDailyTail: performDailyTailMove,
    deferDailyTailToNextPeriod,
    staggerDailyTail: performDailyTailStagger,
    reset() {
      current = makeState();
      return result(true, current.horizon ? (current.warning || 'Se restableció la instantánea, sin cambios pendientes.') : 'No hay trabajo pendiente para planificar.');
    },
    activityEligibility(activityIdValue) {
      const pending = current.model.fragments.filter(fragment => activityId(fragment) === String(activityIdValue) && !lockedFragment(fragment));
      const withinOriginalRange = current.horizon && pending.every(fragment => fragment.originalStart >= current.horizon.horizonStart && fragment.originalFinish <= current.horizon.horizonFinish);
      const showControls = Boolean(pending.length && withinOriginalRange);
      if (!showControls) return { showControls, backwardEnabled: false, forwardEnabled: false, guidance: 'Extienda el rango para incluir toda la actividad pendiente.' };
      const backward = directionFits(activityIdValue, -1), forward = directionFits(activityIdValue, 1);
      const blocked = [backward, forward].filter(item => !item.enabled);
      const guidance = blocked.find(item => item.reason === 'civil-range')?.guidance || blocked[0]?.guidance || '';
      return { showControls, backwardEnabled: backward.enabled, forwardEnabled: forward.enabled, guidance };
    },
    fragmentEligibility(fragmentOrId) {
      const fragmentIdValue = typeof fragmentOrId === 'string' ? fragmentOrId : fragmentOrId?.id;
      const fragment = current.model.fragments.find(item => item.id === fragmentIdValue);
      if (!fragment) fail(`fragmento desconocido: ${fragmentIdValue ?? ''}`);
      const unit = logicalUnits(current.model, activityId(fragment))
        .find(item => item.fragments.some(part => part.id === fragment.id));
      if (!unit) fail(`unidad diaria desconocida: ${fragment.id}`);
      if (unit.fragments.some(lockedFragment)) {
        return { eligible: false, reason: 'completed', guidance: 'El trabajo completado no se puede mover.' };
      }
      const lower = current.horizon && civilScalar(current.horizon.horizonStart);
      const upper = current.horizon && civilScalar(current.horizon.horizonFinish);
      const visible = Boolean(current.horizon && unit.fragments.some(part =>
        civilScalar(part.finish) > lower && civilScalar(part.start) < upper));
      if (!visible) {
        return { eligible: false, reason: 'out-of-range', guidance: 'La unidad est\u00e1 fuera del horizonte visible.' };
      }
      return { eligible: true, reason: '', guidance: '' };
    },
    fragmentState(fragment) {
      if (lockedFragment(fragment)) return 'completed';
      return current.horizon && fragment.start >= current.horizon.horizonStart && fragment.finish <= current.horizon.horizonFinish ? 'in-range' : 'out-of-range';
    }
  };
}
function clone(model) { return { ...model, activities: (model.activities || []).map(item => ({ ...item, nativeBars: item.nativeBars ? item.nativeBars.map(bar => ({ ...bar })) : item.nativeBars })), precedences: (model.precedences || []).map(item => ({ ...item })), fragments: (model.fragments || []).map(item => ({ ...item })) }; }
function hasOwn(item, key) { return Object.prototype.hasOwnProperty.call(item, key); }
function normalizeAlias(item, canonical, aliases, label, valid = () => true) {
  const present = aliases.filter(key => hasOwn(item, key));
  if (!present.length) return undefined;
  const value = item[present[0]];
  if (!valid(value)) fail(`${label} alias inválido`);
  if (present.some(key => item[key] !== value)) fail(`${label} alias conflictivo`);
  item[canonical] = value;
  for (const key of aliases) if (key !== canonical) delete item[key];
  return value;
}
function normalizeLock(fragment) {
  const aliases = ['lockedCompleted', 'locked_completed'], present = aliases.filter(key => hasOwn(fragment, key));
  if (!present.length) { fragment.lockedCompleted = false; return; }
  if (present.some(key => typeof fragment[key] !== 'boolean')) fail('completado debe ser booleano');
  const value = fragment[present[0]];
  if (present.some(key => fragment[key] !== value)) fail('completado alias conflictivo');
  fragment.lockedCompleted = value;
  delete fragment.locked_completed;
}
function normalizeLag(link) {
  const supplied = [];
  for (const key of ['lagMicroseconds', 'lag_microseconds']) if (hasOwn(link, key)) {
    const value = link[key];
    if (typeof value !== 'string' || !/^-?\d+$/.test(value)) fail('lag de precedencia inválido');
    supplied.push(BigInt(value));
  }
  for (const key of ['lagSeconds', 'lag_seconds', 'lag']) if (hasOwn(link, key)) {
    const value = link[key];
    if (typeof value !== 'number' || !Number.isSafeInteger(value)) fail('lag de precedencia inválido');
    supplied.push(BigInt(value) * 1_000_000n);
  }
  if (supplied.length && supplied.some(value => value !== supplied[0])) fail('lag de precedencia conflictivo');
  link.lagMicroseconds = (supplied[0] ?? 0n).toString();
  delete link.lag_microseconds;
  delete link.lagSeconds;
  delete link.lag_seconds;
  delete link.lag;
}
function normalizeModel(model) {
  const normalized = clone(model || {});
  for (const activity of normalized.activities || []) normalizeAlias(activity, 'id', ['id', 'activityId', 'activity_id'], 'actividad', value => typeof value === 'string');
  for (const fragment of normalized.fragments || []) {
    normalizeAlias(fragment, 'activityId', ['activityId', 'activity_id'], 'actividad', value => typeof value === 'string');
    normalizeAlias(fragment, 'nativeBarKey', ['nativeBarKey', 'native_bar_key'], 'barra nativa', value => typeof value === 'string');
    normalizeAlias(fragment, 'nativeBarXmlKey', ['nativeBarXmlKey', 'native_bar_xml_key'], 'barra nativa XML', value => typeof value === 'string');
    const logicalUnitDate = normalizeAlias(fragment, 'logicalUnitDate', ['logicalUnitDate', 'logical_unit_date'], 'unidad lógica', value => typeof value === 'string');
    if (logicalUnitDate !== undefined && civilDate(logicalUnitDate) !== logicalUnitDate) fail('unidad lógica inválida');
    normalizeAlias(fragment, 'displaySuppressed', ['displaySuppressed', 'display_suppressed'], 'visibilidad', value => typeof value === 'boolean');
    normalizeLock(fragment);
  }
  for (const link of normalized.precedences || []) {
    normalizeAlias(link, 'predecessorId', ['predecessorId', 'predecessor_id'], 'predecesor', value => typeof value === 'string');
    normalizeAlias(link, 'successorId', ['successorId', 'successor_id'], 'sucesor', value => typeof value === 'string');
    normalizeAlias(link, 'predecessorBar', ['predecessorBar', 'predecessor_bar'], 'barra predecesora', value => typeof value === 'string');
    normalizeAlias(link, 'successorBar', ['successorBar', 'successor_bar'], 'barra sucesora', value => typeof value === 'string');
    normalizeAlias(link, 'type', ['type', 'precedenceType', 'precedence_type'], 'tipo de precedencia', value => typeof value === 'string');
    normalizeLag(link);
  }
  return normalized;
}
function activityId(item) { return String(item.activityId ?? ''); }
function linkValue(link, ...names) { for (const name of names) if (link[name] !== undefined) return link[name]; return undefined; }
function normalType(value) {
  const compact = String(value ?? '').replace(/[-_\s]/g, '').toLowerCase();
  const found = { finishstart: 'FinishStart', fs: 'FinishStart', startstart: 'StartStart', ss: 'StartStart', finishfinish: 'FinishFinish', ff: 'FinishFinish', startfinish: 'StartFinish', sf: 'StartFinish' }[compact];
  if (!found) fail(`tipo de precedencia no evaluable: ${value}`);
  return found;
}
function lagUs(link) {
  if (typeof link.lagMicroseconds !== 'string' || !/^-?\d+$/.test(link.lagMicroseconds)) fail('lag de precedencia no evaluable');
  return BigInt(link.lagMicroseconds);
}
function fragmentsFor(model, id, barKey) {
  const found = model.fragments.filter(fragment => activityId(fragment) === id && String(fragment.nativeBarKey ?? '') === String(barKey));
  if (!found.length) fail(`barra de precedencia no evaluable para actividad ${id}`);
  return found;
}
function span(model, id, barKey) {
  const fragments = fragmentsFor(model, id, barKey);
  let start = civilScalar(fragments[0].start), finish = civilScalar(fragments[0].finish);
  for (const fragment of fragments.slice(1)) { start = start < civilScalar(fragment.start) ? start : civilScalar(fragment.start); finish = finish > civilScalar(fragment.finish) ? finish : civilScalar(fragment.finish); }
  return { start, finish };
}
function requiredShift(model, link) {
  const predecessorId = String(linkValue(link, 'predecessorId', 'predecessor_id'));
  const successorId = String(linkValue(link, 'successorId', 'successor_id'));
  const predecessor = span(model, predecessorId, linkValue(link, 'predecessorBar', 'predecessor_bar'));
  const successor = span(model, successorId, linkValue(link, 'successorBar', 'successor_bar'));
  const lag = lagUs(link);
  let amount;
  switch (normalType(linkValue(link, 'type', 'precedenceType', 'precedence_type'))) {
    case 'FinishStart': amount = predecessor.finish + lag - successor.start; break;
    case 'StartStart': amount = predecessor.start + lag - successor.start; break;
    case 'FinishFinish': amount = predecessor.finish + lag - successor.finish; break;
    case 'StartFinish': amount = predecessor.start + lag - successor.finish; break;
  }
  return amount > 0n ? amount : 0n;
}
function ceilDays(value) { return Number((value + DAY_US - 1n) / DAY_US); }
function validateShiftBounds(fragments, days) {
  return fragments.map(fragment => ({ fragment, start: addCivilDays(fragment.start, days), finish: addCivilDays(fragment.finish, days) }));
}
function applyShift(proposed, provenance) {
  for (const item of proposed) { item.fragment.start = item.start; item.fragment.finish = item.finish; item.fragment.provenance = provenance; }
}
function shiftFragments(model, id, days, provenance) {
  const relevant = model.fragments.filter(fragment => activityId(fragment) === id).slice().sort((a, b) => civilScalar(a.start) < civilScalar(b.start) ? -1 : 1);
  if (!relevant.length) fail(`actividad desconocida: ${id}`);
  let sawPending = false;
  const pending = [];
  for (const fragment of relevant) {
    if (lockedFragment(fragment)) {
      if (sawPending) fail(`prefijo completado ambiguo: ${id}`);
      continue;
    }
    sawPending = true;
    pending.push(fragment);
  }
  if (!pending.length) fail(`actividad completada: ${id}`);
  const movable = pending.filter(fragment => fragment.provenance !== 'direct');
  if (!movable.length) fail(`sin tramo pendiente para cascada: ${id}`);
  applyShift(validateShiftBounds(movable, days), provenance);
}
function lockedFragment(fragment) { return fragment.lockedCompleted === true; }
function pendingFragments(model, idValue) {
  const id = String(idValue);
  const relevant = (model.fragments || []).filter(fragment => activityId(fragment) === id).slice().sort((a, b) => civilScalar(a.originalStart) < civilScalar(b.originalStart) ? -1 : 1);
  if (!relevant.length) fail(`actividad desconocida: ${id}`);
  let sawPending = false;
  const pending = [];
  for (const fragment of relevant) {
    if (lockedFragment(fragment)) {
      if (sawPending) fail(`prefijo completado ambiguo: ${id}`);
      continue;
    }
    sawPending = true;
    pending.push(fragment);
  }
  if (!pending.length) fail(`actividad completada: ${id}`);
  return pending;
}
function shiftDays(fragment) {
  const start = civilScalar(fragment.start) - civilScalar(fragment.originalStart);
  const finish = civilScalar(fragment.finish) - civilScalar(fragment.originalFinish);
  if (start !== finish || start % DAY_US !== 0n) fail(`movimiento civil inválido: ${fragment.id}`);
  const days = start / DAY_US;
  if (days > BigInt(Number.MAX_SAFE_INTEGER) || days < BigInt(Number.MIN_SAFE_INTEGER)) fail(`movimiento civil inválido: ${fragment.id}`);
  return Number(days);
}
function logicalUnits(model, idValue) {
  const id = String(idValue), grouped = new Map();
  for (const fragment of model.fragments || []) {
    if (activityId(fragment) !== id) continue;
    const key = fragment.logicalUnitDate || civilDate(fragment.originalStart);
    if (!grouped.has(key)) grouped.set(key, []);
    grouped.get(key).push(fragment);
  }
  return [...grouped.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([originalDate, fragments]) => {
    const originalOrder = fragments.slice().sort((left, right) => {
      const start = civilScalar(left.originalStart) - civilScalar(right.originalStart);
      if (start !== 0n) return start < 0n ? -1 : 1;
      const finish = civilScalar(left.originalFinish) - civilScalar(right.originalFinish);
      return finish === 0n ? left.id.localeCompare(right.id) : (finish < 0n ? -1 : 1);
    });
    const shifts = originalOrder.map(fragment => {
      const start = civilScalar(fragment.start) - civilScalar(fragment.originalStart);
      const finish = civilScalar(fragment.finish) - civilScalar(fragment.originalFinish);
      if (start !== finish) fail(`split nativo deformado en actividad ${id}`);
      return start;
    });
    if (shifts.some(amount => amount !== shifts[0])) fail(`split nativo separado en actividad ${id}`);
    const currentOrder = originalOrder.slice().sort((left, right) => {
      const start = civilScalar(left.start) - civilScalar(right.start);
      if (start !== 0n) return start < 0n ? -1 : 1;
      const finish = civilScalar(left.finish) - civilScalar(right.finish);
      return finish === 0n ? left.id.localeCompare(right.id) : (finish < 0n ? -1 : 1);
    });
    if (currentOrder.some((fragment, index) => fragment.id !== originalOrder[index].id)) fail(`orden de split nativo alterado en actividad ${id}`);
    const first = originalOrder[0];
    for (const fragment of originalOrder) {
      if (civilScalar(fragment.start) - civilScalar(first.start) !== civilScalar(fragment.originalStart) - civilScalar(first.originalStart)
          || civilScalar(fragment.finish) - civilScalar(first.finish) !== civilScalar(fragment.originalFinish) - civilScalar(first.originalFinish)) {
        fail(`unión de split nativo alterada en actividad ${id}`);
      }
    }
    return {
      id: originalDate,
      originalDate,
      fragments: originalOrder,
      currentDate: first.logicalUnitDate
        ? addCivilDays(originalDate, shiftDays(first))
        : civilDate(first.start),
    };
  });
}
function shiftLogicalUnit(unit, targetDate, provenance) {
  const source = civilParts(unit.currentDate), target = civilParts(targetDate);
  const days = dayNumber(target.year, target.month, target.day) - dayNumber(source.year, source.month, source.day);
  applyShift(validateShiftBounds(unit.fragments, days), provenance);
  unit.currentDate = targetDate;
}
function shiftLogicalUnitByDays(unit, days, provenance) {
  if (!Number.isSafeInteger(days) || days === 0) fail('movimiento civil inválido');
  const proposed = unit.fragments.map(fragment => ({
    fragment,
    start: addCivilDays(fragment.start, days),
    finish: addCivilDays(fragment.finish, days),
  }));
  applyShift(proposed, provenance);
  unit.currentDate = addCivilDays(unit.currentDate, days);
}
function validateDailyTailMoveRequest(request) {
  if (!request || typeof request !== 'object' || Array.isArray(request)
      || Object.keys(request).sort().join(',') !== 'activityId,fragmentId,targetDate'
      || typeof request.activityId !== 'string' || !request.activityId
      || typeof request.fragmentId !== 'string' || !request.fragmentId
      || typeof request.targetDate !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(request.targetDate)) {
    fail('solicitud de inserción diaria inválida');
  }
  civilParts(request.targetDate);
  return request;
}
function validateDailyStaggerRequest(request) {
  if (!request || typeof request !== 'object' || Array.isArray(request)
      || Object.keys(request).sort().join(',') !== 'activityId,divisions,fragmentId,gapDays'
      || typeof request.activityId !== 'string' || !request.activityId
      || typeof request.fragmentId !== 'string' || !request.fragmentId
      || !Number.isInteger(request.divisions) || request.divisions < 1
      || !Number.isInteger(request.gapDays) || request.gapDays < 1) {
    fail('solicitud de dividir y desfasar inválida');
  }
  return request;
}
function validateNativeTopology(reference, candidate) {
  if ((reference.fragments || []).length !== (candidate.fragments || []).length) fail('topología nativa alterada');
  const byId = new Map((candidate.fragments || []).map(fragment => [fragment.id, fragment]));
  for (const fragment of reference.fragments || []) {
    const current = byId.get(fragment.id);
    if (!current || current.activityId !== fragment.activityId || current.nativeBarKey !== fragment.nativeBarKey
        || current.nativeBarXmlKey !== fragment.nativeBarXmlKey || current.originalStart !== fragment.originalStart
        || current.originalFinish !== fragment.originalFinish || current.quantityPercentage !== fragment.quantityPercentage
        || (current.anchorBefore || '') !== (fragment.anchorBefore || '')
        || (current.anchorAfter || '') !== (fragment.anchorAfter || '')
        || (current.splitOrder ?? null) !== (fragment.splitOrder ?? null)) {
      fail(`topología nativa alterada: ${fragment.id}`);
    }
  }
  for (const activity of candidate.activities || []) logicalUnits(candidate, activity.id);
}
function existingDirectShifts(model) {
  const directIds = new Set();
  for (const fragment of model.fragments || []) {
    if (fragment.provenance !== 'direct') continue;
    if (lockedFragment(fragment)) fail(`fragmento completado directo: ${fragment.id}`);
    directIds.add(activityId(fragment));
  }
  const totals = new Map();
  for (const id of [...directIds].sort()) {
    const pending = pendingFragments(model, id);
    if (pending.some(fragment => fragment.provenance !== 'direct')) fail(`cobertura directa incompleta: ${id}`);
    const shifts = pending.map(shiftDays);
    if (!shifts.length || shifts[0] === 0 || shifts.some(days => days !== shifts[0])) fail(`movimiento directo no uniforme: ${id}`);
    totals.set(id, shifts[0]);
  }
  return totals;
}
function directActivityIds(model) {
  const ids = new Set();
  for (const fragment of model.fragments || []) {
    if (fragment.provenance !== 'direct') continue;
    if (lockedFragment(fragment)) fail(`fragmento completado directo: ${fragment.id}`);
    ids.add(activityId(fragment));
  }
  for (const id of ids) {
    for (const unit of logicalUnits(model, id)) {
      const direct = unit.fragments.filter(fragment => fragment.provenance === 'direct');
      if (direct.length && direct.length !== unit.fragments.length) fail(`cobertura directa incompleta de unidad: ${id}`);
    }
  }
  return ids;
}
function restoreCurrentFragments(rebuilt, source, include) {
  const byId = new Map(rebuilt.fragments.map(fragment => [fragment.id, fragment]));
  for (const fragment of source.fragments || []) {
    if (!include(fragment)) continue;
    const target = byId.get(fragment.id);
    target.start = fragment.start;
    target.finish = fragment.finish;
    target.provenance = fragment.provenance;
  }
}
function rebuildDirectPreview(model) {
  const direct = directActivityIds(model), rebuilt = resetPendingToOriginal(model);
  restoreCurrentFragments(rebuilt, model, fragment => fragment.provenance === 'direct');
  checkOverlaps(rebuilt);
  // A re-edited cascaded tail may leave earlier units of that successor
  // temporarily at their original dates.  Recompute their permitted cascade
  // first; validating incoming precedence before that recomputation rejects a
  // valid direct tail solely because the preview is incomplete.
  const cascaded = cascadeForward(rebuilt, [...direct]);
  incomingSafe(cascaded, direct);
  return cascaded;
}
function hasNonUniformDirectState(model) {
  for (const id of directActivityIds(model)) {
    const pending = pendingFragments(model, id), direct = pending.filter(fragment => fragment.provenance === 'direct');
    if (direct.length !== pending.length) return true;
    const shifts = pending.map(shiftDays);
    if (shifts.some(days => days !== shifts[0])) return true;
  }
  return false;
}
function resetPendingToOriginal(model) {
  const rebuilt = clone(model);
  for (const fragment of rebuilt.fragments) {
    if (lockedFragment(fragment)) continue;
    fragment.start = fragment.originalStart;
    fragment.finish = fragment.originalFinish;
    fragment.provenance = '';
  }
  return rebuilt;
}
function requireDirectContainment(pending, horizon, days) {
  const lower = civilScalar(horizon.horizonStart), upper = civilScalar(horizon.horizonFinish);
  for (const fragment of pending) {
    const originalStart = civilScalar(fragment.originalStart), originalFinish = civilScalar(fragment.originalFinish);
    const start = civilScalar(addCivilDays(fragment.originalStart, days)), finish = civilScalar(addCivilDays(fragment.originalFinish, days));
    if (originalStart < lower || originalFinish > upper || start < lower || finish > upper) fail(`movimiento directo fuera del horizonte: ${fragment.id}`, 'HORIZON_RANGE');
  }
}
function requireCurrentContainment(fragments, horizon) {
  const lower = civilScalar(horizon.horizonStart), upper = civilScalar(horizon.horizonFinish);
  for (const fragment of fragments) {
    if (civilScalar(fragment.start) < lower || civilScalar(fragment.finish) > upper) fail(`movimiento directo fuera del horizonte: ${fragment.id}`, 'HORIZON_RANGE');
  }
}
function checkOverlaps(model) {
  for (const activity of model.activities || []) {
    const id = String(activity.id ?? '');
    const ordered = model.fragments.filter(fragment => activityId(fragment) === id).slice().sort((a, b) => civilScalar(a.start) < civilScalar(b.start) ? -1 : 1);
    for (let index = 1; index < ordered.length; index += 1) if (civilScalar(ordered[index - 1].finish) > civilScalar(ordered[index].start)) fail(`solapamiento de fragmentos en actividad ${id}`);
    const occupied = new Set();
    for (const unit of logicalUnits(model, id)) {
      if (occupied.has(unit.currentDate)) fail(`ocupación civil duplicada en actividad ${id}: dos unidades en el mismo día`);
      occupied.add(unit.currentDate);
    }
  }
}
function incomingSafe(model, directIds) {
  const baselineDeficits = baselinePrecedenceDeficits(model);
  for (const [index, link] of (model.precedences || []).entries()) {
    const successor = String(linkValue(link, 'successorId', 'successor_id'));
    const successorBar = linkValue(link, 'successorBar', 'successor_bar');
    const endpointHasDirectMovement = fragmentsFor(model, successor, successorBar)
      .some(fragment => fragment.provenance === 'direct');
    if (directIds.has(successor) && endpointHasDirectMovement
        && requiredShift(model, link) > baselineDeficits[index]) {
      fail(`precedencia entrante agravada para actividad ${successor}`);
    }
  }
}

function originalSchedule(model) {
  const baseline = clone(model);
  for (const fragment of baseline.fragments || []) {
    fragment.start = fragment.originalStart;
    fragment.finish = fragment.originalFinish;
    fragment.provenance = '';
  }
  return baseline;
}

function baselinePrecedenceDeficits(model) {
  const baseline = originalSchedule(model);
  return (baseline.precedences || []).map(link => requiredShift(baseline, link));
}

function ensurePrecedencesNotWorsened(model, baselineDeficits) {
  for (const [index, link] of (model.precedences || []).entries()) {
    if (requiredShift(model, link) > baselineDeficits[index]) {
      fail(`precedencia preexistente agravada: ${linkValue(link, 'key') ?? ''}`);
    }
  }
}

function validatePlannerStructure(model, strictBrowser = false) {
  const activityBars = new Map();
  const nativeBarCatalogs = new Set();
  for (const activity of model.activities || []) {
    if (typeof activity.id !== 'string' || !activity.id) fail('actividad inválida');
    if (activityBars.has(activity.id)) fail(`actividad duplicada: ${activity.id}`);
    if ((strictBrowser && !Array.isArray(activity.nativeBars)) || (activity.nativeBars !== undefined && !Array.isArray(activity.nativeBars))) fail(`barras nativas inválidas para actividad ${activity.id}`);
    if (activity.nativeBars !== undefined) nativeBarCatalogs.add(activity.id);
    const keys = new Set();
    for (const bar of activity.nativeBars || []) {
      if (!bar || typeof bar.key !== 'string' || !bar.key) fail(`barra nativa inválida para actividad ${activity.id}`);
      if (keys.has(bar.key)) fail(`barra nativa duplicada para actividad ${activity.id}`);
      keys.add(bar.key);
    }
    activityBars.set(activity.id, keys);
  }
  const fragmentIds = new Set();
  for (const fragment of model.fragments || []) {
    if (typeof fragment.id !== 'string' || !fragment.id) fail('fragmento inválido');
    if (fragmentIds.has(fragment.id)) fail(`fragmento duplicado: ${fragment.id}`);
    fragmentIds.add(fragment.id);
    if (typeof fragment.activityId !== 'string' || !activityBars.has(fragment.activityId)) fail(`propiedad de fragmento inválida: ${fragment.id}`);
    if (typeof fragment.nativeBarKey !== 'string' || !fragment.nativeBarKey) fail(`barra nativa inválida: ${fragment.id}`);
    const bars = activityBars.get(fragment.activityId);
    if ((strictBrowser || nativeBarCatalogs.has(fragment.activityId)) && !bars.has(fragment.nativeBarKey)) fail(`barra nativa desconocida: ${fragment.id}`);
    let originalStart, originalFinish;
    if (strictBrowser) {
      try { originalStart = civilScalar(fragment.originalStart); originalFinish = civilScalar(fragment.originalFinish); }
      catch (_) { fail(`fragmento sin estado original verificable: ${fragment.id}`); }
    }
    for (const key of ['anchorBefore', 'anchorAfter']) {
      if (fragment[key] === undefined || fragment[key] === '') continue;
      if (typeof fragment[key] !== 'string') fail(`ancla nativa inválida: ${fragment.id}`);
      let anchor;
      try { anchor = civilScalar(fragment[key]); }
      catch (_) { fail(`ancla nativa inválida: ${fragment.id}`); }
      if (strictBrowser && ((key === 'anchorBefore' && anchor > originalStart) || (key === 'anchorAfter' && anchor < originalFinish))) fail(`ancla nativa cruzada: ${fragment.id}`);
    }
  }
}

function preflight(model, strictBrowser = false) {
  const normalized = normalizeModel(model), baseline = clone(normalized);
  validatePlannerStructure(normalized, strictBrowser);
  for (const fragment of baseline.fragments) {
    if (typeof fragment.originalStart !== 'string' || typeof fragment.originalFinish !== 'string') fail(`fragmento sin estado original verificable: ${fragment.id}`);
    if (lockedFragment(fragment) && (fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish)) fail(`fragmento completado modificado: ${fragment.id}`);
    fragment.start = fragment.originalStart;
    fragment.finish = fragment.originalFinish;
  }
  for (const link of baseline.precedences || []) {
    normalType(linkValue(link, 'type', 'precedenceType', 'precedence_type'));
    lagUs(link);
  }
  return normalized;
}

export function cascadeForward(model, directActivityIds = []) {
  const normalized = preflight(model), result = clone(normalized), direct = new Set(directActivityIds.map(String)), queue = [...direct].sort();
  const baselineDeficits = baselinePrecedenceDeficits(normalized);
  let cursor = 0, moves = 0, limit = Math.max(1, result.fragments.length * Math.max(1, result.precedences.length));
  while (cursor < queue.length) {
    const current = queue[cursor++];
    for (const [index, link] of (result.precedences || []).entries()) {
      const predecessor = String(linkValue(link, 'predecessorId', 'predecessor_id'));
      const successor = String(linkValue(link, 'successorId', 'successor_id'));
      if (predecessor !== current) continue;
      const needed = requiredShift(result, link) - baselineDeficits[index];
      if (needed <= 0n) continue;
      const successorFragments = fragmentsFor(result, successor, linkValue(link, 'successorBar', 'successor_bar'));
      if (successorFragments.every(fragment => fragment.provenance === 'direct')) {
        fail(`conflicto entre movimiento directo y cascada para ${successor}`);
      }
      if (successorFragments.some(lockedFragment)) fail(`prefijo completado impide la cascada para ${successor}`);
      shiftFragments(result, successor, ceilDays(needed), 'cascade');
      queue.push(successor);
      moves += 1;
      if (moves > limit) fail('ciclo o no convergencia de precedencias');
    }
  }
  checkOverlaps(result);
  ensurePrecedencesNotWorsened(result, baselineDeficits);
  return result;
}

export function moveFragment(model, fragmentId, newStart) {
  const normalized = preflight(model), result = clone(normalized), target = result.fragments.find(fragment => fragment.id === fragmentId);
  if (!target) fail(`fragmento desconocido: ${fragmentId}`);
  if (lockedFragment(target)) fail('fragmento completado no se puede mover');
  const delta = civilScalar(newStart) - civilScalar(target.start);
  if (delta % DAY_US !== 0n) fail('el movimiento debe ser en días civiles enteros');
  const days = Number(delta / DAY_US), id = activityId(target), key = String(target.nativeBarKey ?? '');
  const candidates = result.fragments.filter(fragment => activityId(fragment) === id && String(fragment.nativeBarKey ?? '') === key && civilScalar(fragment.start) >= civilScalar(target.start)).sort((a, b) => civilScalar(a.start) < civilScalar(b.start) ? -1 : 1);
  if (!candidates.length || candidates.some(lockedFragment)) fail('avance completado impide el movimiento');
  applyShift(validateShiftBounds(candidates, days), 'direct');
  checkOverlaps(result);
  incomingSafe(result, new Set([id]));
  return days > 0 ? cascadeForward(result, [id]) : result;
}

export function moveDailyTail(model, requestValue) {
  const request = validateDailyTailMoveRequest(requestValue), normalized = preflight(model), result = clone(normalized);
  const target = result.fragments.find(fragment => fragment.id === request.fragmentId);
  if (!target) fail(`fragmento desconocido: ${request.fragmentId}`);
  if (activityId(target) !== request.activityId) fail(`fragmento ajeno a actividad ${request.activityId}`);
  checkOverlaps(result);
  const units = logicalUnits(result, request.activityId), selected = units.find(unit => unit.fragments.some(fragment => fragment.id === request.fragmentId));
  if (!selected) fail(`unidad diaria desconocida: ${request.fragmentId}`);
  if (selected.fragments.some(lockedFragment)) fail('fragmento completado no se puede mover');
  if (selected.currentDate === request.targetDate) fail('la unidad diaria ya ocupa la fecha destino');
  const source = civilParts(selected.currentDate), targetParts = civilParts(request.targetDate);
  const days = dayNumber(targetParts.year, targetParts.month, targetParts.day) - dayNumber(source.year, source.month, source.day);
  const tail = units.filter(unit => unit.currentDate >= selected.currentDate);
  if (tail.some(unit => unit.fragments.some(lockedFragment))) fail('avance completado impide desplazar la cola diaria');
  for (const unit of tail) shiftLogicalUnitByDays(unit, days, 'direct');
  checkOverlaps(result);
  const cascaded = rebuildDirectPreview(result);
  validateNativeTopology(normalized, cascaded);
  cascaded.precedences = (model.precedences || []).map(link => ({ ...link }));
  return cascaded;
}

export function staggerDailyTail(model, requestValue, horizonValue = null) {
  const request = validateDailyStaggerRequest(requestValue);
  const horizon = horizonValue ? validateCivilHorizon(model, horizonValue) : null;
  let candidate = preflight(model);
  let units = logicalUnits(candidate, request.activityId);
  const selectedIndex = units.findIndex(unit =>
    unit.fragments.some(fragment => fragment.id === request.fragmentId));
  if (selectedIndex < 0) fail(`unidad diaria desconocida: ${request.fragmentId}`);
  if (selectedIndex + request.divisions >= units.length) {
    fail('no hay suficientes unidades pendientes para dividir y desfasar');
  }
  for (let offset = 1; offset <= request.divisions; offset += 1) {
    units = logicalUnits(candidate, request.activityId);
    const next = units[selectedIndex + offset];
    if (!next || next.fragments.some(lockedFragment)) {
      fail('avance completado impide dividir y desfasar');
    }
    const targetDate = addCivilDays(`${next.currentDate}T00:00:00`, request.gapDays).slice(0, 10);
    if (horizon && (civilScalar(`${targetDate}T00:00:00`) < civilScalar(horizon.horizonStart)
        || civilScalar(`${targetDate}T00:00:00`) >= civilScalar(horizon.horizonFinish))) {
      fail('fecha destino fuera del horizonte visible', 'HORIZON_RANGE');
    }
    candidate = moveDailyTail(candidate, {
      activityId: request.activityId,
      fragmentId: next.fragments[0].id,
      targetDate,
    });
  }
  return candidate;
}

export function moveActivity(model, idValue, deltaDays, horizonValue) {
  const normalized = preflight(model);
  if (!Number.isInteger(deltaDays) || deltaDays === 0) fail('movimiento civil inválido');
  const horizon = validateCivilHorizon(normalized, horizonValue);
  if (hasNonUniformDirectState(normalized)) {
    const id = String(idValue), result = resetPendingToOriginal(normalized);
    restoreCurrentFragments(result, normalized, fragment => fragment.provenance === 'direct' || activityId(fragment) === id);
    const pending = pendingFragments(result, id);
    applyShift(validateShiftBounds(pending, deltaDays), 'direct');
    requireCurrentContainment(pending, horizon);
    checkOverlaps(result);
    const direct = directActivityIds(result);
    incomingSafe(result, direct);
    return cascadeForward(result, [...direct]);
  }
  const totals = existingDirectShifts(normalized);
  const id = String(idValue);
  const current = totals.get(id) || 0;
  totals.set(id, current + deltaDays);
  if (totals.get(id) === 0) totals.delete(id);
  const rebuilt = resetPendingToOriginal(normalized);
  for (const [directId, days] of [...totals].sort(([left], [right]) => left.localeCompare(right))) {
    const pending = pendingFragments(rebuilt, directId);
    requireDirectContainment(pending, horizon, days);
    applyShift(validateShiftBounds(pending, days), 'direct');
  }
  checkOverlaps(rebuilt);
  incomingSafe(rebuilt, new Set(totals.keys()));
  return cascadeForward(rebuilt, [...totals.keys()]);
}

function recomputedPreview(model, horizon, allowHiddenTail = false) {
  const normalized = preflight(model);
  if (!(normalized.fragments || []).some(fragment => fragment.provenance === 'direct'
      && (fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish))) {
    return resetPendingToOriginal(normalized);
  }
  if (hasNonUniformDirectState(normalized)) return rebuildDirectPreview(normalized);
  const totals = existingDirectShifts(normalized), rebuilt = resetPendingToOriginal(normalized);
  for (const [directId, days] of [...totals].sort(([left], [right]) => left.localeCompare(right))) {
    const pending = pendingFragments(rebuilt, directId);
    if (!allowHiddenTail) requireDirectContainment(pending, horizon, days);
    applyShift(validateShiftBounds(pending, days), 'direct');
  }
  checkOverlaps(rebuilt);
  incomingSafe(rebuilt, new Set(totals.keys()));
  return cascadeForward(rebuilt, [...totals.keys()]);
}
function requirePreviewMatch(model, preview) {
  const actual = new Map();
  for (const fragment of model.fragments || []) {
    if (actual.has(String(fragment.id))) fail(`cobertura original ambigua: ${fragment.id}`);
    actual.set(String(fragment.id), fragment);
  }
  const expected = new Map();
  for (const fragment of preview.fragments || []) {
    if (expected.has(String(fragment.id))) fail(`vista previa ambigua: ${fragment.id}`);
    expected.set(String(fragment.id), fragment);
  }
  if (actual.size !== expected.size || [...expected.keys()].some(id => !actual.has(id))) fail('cobertura original incompleta');
  for (const [id, item] of expected) {
    const supplied = actual.get(id);
    if (supplied.start !== item.start || supplied.finish !== item.finish || (supplied.provenance || '') !== (item.provenance || '')) fail(`vista previa no coincide: ${id}`);
  }
}

export function buildScenario(model, source, horizonValue) {
  if (!source || typeof source.projectFingerprint !== 'string' || !source.projectFingerprint || typeof source.precedenceFingerprint !== 'string' || !source.precedenceFingerprint) fail('identidad o fingerprint del proyecto ausente');
  const horizon = validateCivilHorizon(model, horizonValue);
  if (!(model.fragments || []).some(fragment => fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish)) {
    fail('el escenario no contiene cambios directos o por cascada');
  }
  const preview = recomputedPreview(model, horizon);
  requirePreviewMatch(model, preview);
  const changed = new Map();
  for (const fragment of preview.fragments || []) {
    const changedTime = fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish;
    if (!changedTime || !['direct', 'cascade'].includes(fragment.provenance)) continue;
    const id = activityId(fragment);
    const previous = changed.get(id);
    if (previous && previous !== fragment.provenance) fail(`procedencia ambigua para actividad ${id}`);
    changed.set(id, fragment.provenance);
  }
  const activities = [...changed.keys()].sort().map(activityIdValue => ({
    activity_id: activityIdValue,
    provenance: changed.get(activityIdValue),
    fragments: (preview.fragments || []).filter(fragment => activityId(fragment) === activityIdValue).slice().sort((a, b) => a.originalStart === b.originalStart ? String(a.id).localeCompare(String(b.id)) : a.originalStart.localeCompare(b.originalStart)).map(fragment => ({
      id: String(fragment.id), native_bar_xml_key: String(fragment.nativeBarXmlKey ?? ''), native_bar_key: String(fragment.nativeBarKey ?? ''),
      start: fragment.start, finish: fragment.finish, original_start: fragment.originalStart, original_finish: fragment.originalFinish,
      quantity_percentage: String(fragment.quantityPercentage ?? fragment.quantity_percentage ?? ''), locked_completed: lockedFragment(fragment)
    }))
  }));
  if (!activities.length) fail('el escenario no contiene cambios directos o por cascada');
  if (activities.some(activity => activity.fragments.some(fragment => !fragment.native_bar_xml_key || !fragment.native_bar_key || !fragment.quantity_percentage))) fail('metadatos nativos o cantidades incompletos');
  return { schema: 'vgs-stp-splits/2', project_fingerprint: source.projectFingerprint, precedence_fingerprint: source.precedenceFingerprint, horizon_start: horizon.horizonStart, horizon_finish: horizon.horizonFinish, activities };
}

function serializeAllAffectedActivitiesByFragment(fragments) {
  const affected = new Set((fragments || []).filter(fragment =>
    (fragment.start !== fragment.originalStart || fragment.finish !== fragment.originalFinish)
    && ['direct', 'cascade'].includes(fragment.provenance)
  ).map(activityId));
  return [...affected].sort().map(id => {
    const activityFragments = (fragments || []).filter(fragment => activityId(fragment) === id)
      .slice().sort((left, right) => left.originalStart === right.originalStart
        ? String(left.id).localeCompare(String(right.id)) : left.originalStart.localeCompare(right.originalStart));
    const unitDates = new Map();
    for (const unit of logicalUnits({ fragments }, id)) for (const fragment of unit.fragments) unitDates.set(String(fragment.id), unit.originalDate);
    return {
      activity_id: id,
      fragments: activityFragments.map(fragment => ({
        id: String(fragment.id), native_bar_xml_key: String(fragment.nativeBarXmlKey ?? ''), native_bar_key: String(fragment.nativeBarKey ?? ''),
        logical_unit_id: `${id}:${unitDates.get(String(fragment.id)) ?? ''}`,
        logical_unit_date: unitDates.get(String(fragment.id)) ?? '',
        provenance: fragment.start === fragment.originalStart && fragment.finish === fragment.originalFinish
          ? 'unchanged' : String(fragment.provenance ?? ''),
        start: fragment.start, finish: fragment.finish, original_start: fragment.originalStart, original_finish: fragment.originalFinish,
        quantity_percentage: String(fragment.quantityPercentage ?? fragment.quantity_percentage ?? ''), locked_completed: lockedFragment(fragment)
      }))
    };
  });
}

export function buildVgsApplyPackage(model, source, horizonValue) {
  if (!source || typeof source.projectFingerprint !== 'string' || !source.projectFingerprint || typeof source.precedenceFingerprint !== 'string' || !source.precedenceFingerprint) fail('identidad o fingerprint del proyecto ausente');
  const horizon = validateCivilHorizon(model, horizonValue);
  const preview = recomputedPreview(model, horizon, true);
  requirePreviewMatch(model, preview);
  const activities = serializeAllAffectedActivitiesByFragment(preview.fragments);
  if (!activities.length) fail('el paquete de aplicación no contiene cambios directos o por cascada');
  if (activities.some(activity => activity.fragments.some(fragment => {
    const moved = fragment.start !== fragment.original_start || fragment.finish !== fragment.original_finish;
    const validProvenance = moved
      ? ['direct', 'cascade'].includes(fragment.provenance)
      : fragment.provenance === 'unchanged';
    return !fragment.native_bar_xml_key || !fragment.native_bar_key || !fragment.quantity_percentage || !validProvenance;
  }))) fail('metadatos nativos, cantidades o procedencia incompletos');
  return { schema: 'vgs-stp-apply/1', project_fingerprint: source.projectFingerprint, precedence_fingerprint: source.precedenceFingerprint, horizon_start: horizon.horizonStart, horizon_finish: horizon.horizonFinish, activities };
}
