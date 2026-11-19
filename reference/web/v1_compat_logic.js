/* Pure V1-style presentation compatibility helpers for the STP V2 snapshot. */

const TYPE_KEYS = new Set(['activity type']);
const RESOURCE_KEYS = new Set(['resource', 'resources', 'recurso', 'equipo']);
const SENSITIVE_KEY = /(?:pass(?:word)?|secret|token|api[ _-]?key|credential|authorization|cookie|session)/i;
const COLOR = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;
const CALCULATIONS = new Set(['sum', 'average', 'weightedAverage', 'maximum', 'minimum']);
const GENERAL_COLOR_KEYS = ['exempt', 'precedence', 'capacity', 'resourceOverload', 'completed'];
const GENERAL_COLOR_DEFAULTS = {
  exempt: '#68bd79', precedence: '#d28a00', capacity: '#d05750', resourceOverload: '#d05750', completed: '#d8dde2',
};
const PRINT_DEFAULTS = {
  title: 'Short Term Planner UG', subtitle: '', author: '', mineUnit: '', company: '', logoDataUrl: '', logoName: '',
  includeDailyProgressCharts: false, includeGeneralProgressCharts: false,
};
const PRINT_TEXT_KEYS = ['title', 'subtitle', 'author', 'mineUnit', 'company', 'logoDataUrl', 'logoName'];
const ACTIVE_TABS = new Set(['type', 'attributes', 'report', 'general', 'print']);
const MANUAL_RESOURCE_ID = /^manual:[1-9]\d*$/;

function normalizedKey(value) { return String(value ?? '').trim().replace(/\s+/g, ' ').toLocaleLowerCase('es-MX'); }
function text(value) { return typeof value === 'string' ? value.trim() : ''; }

function compareAttributeValues(leftValue, rightValue, direction) {
  const left = text(leftValue), right = text(rightValue);
  if (!left && !right) return 0;
  if (!left) return 1;
  if (!right) return -1;
  const leftNumber = Number(left), rightNumber = Number(right);
  const comparison = Number.isFinite(leftNumber) && Number.isFinite(rightNumber)
    ? (leftNumber < rightNumber ? -1 : leftNumber > rightNumber ? 1 : 0)
    : left.localeCompare(right, 'es-MX', { numeric: true, sensitivity: 'base' });
  return direction === 'desc' ? -comparison : comparison;
}
function safeText(value, maximum = 160) { const result = text(value); return result && result.length <= maximum ? result : ''; }
function finite(value) { return typeof value === 'number' && Number.isFinite(value) ? value : Number.NaN; }
function sortedUnique(values) { return [...new Set(values)]; }

function catalogLists(catalog) {
  return {
    attributes: new Set(Array.isArray(catalog?.attributes) ? catalog.attributes : []),
    types: new Set(Array.isArray(catalog?.types) ? catalog.types : []),
    resources: new Set(Array.isArray(catalog?.resources) ? catalog.resources : []),
  };
}

function resourceNameKey(value) { return text(value).toLocaleLowerCase('es-MX'); }

function vgsResourceOptions(catalog) {
  const descriptors = Array.isArray(catalog?.resourceDescriptors) ? catalog.resourceDescriptors : [];
  if (descriptors.length) return descriptors.map(item => ({ ...item }));
  return (Array.isArray(catalog?.resources) ? catalog.resources : []).map(name => ({
    id: `vgs:${name}`, name, source: 'vgs', editable: false,
  }));
}

function normalizeManualResources(raw, catalog) {
  const candidates = Array.isArray(raw) ? raw : [];
  const usedIds = new Set();
  const usedNames = new Set(vgsResourceOptions(catalog).map(item => resourceNameKey(item.name)));
  const result = [];
  for (const candidate of candidates) {
    if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) continue;
    const id = safeText(candidate.id, 80);
    const name = safeText(candidate.name, 160);
    const key = resourceNameKey(name);
    if (!MANUAL_RESOURCE_ID.test(id) || !name || usedIds.has(id) || usedNames.has(key)) continue;
    usedIds.add(id);
    usedNames.add(key);
    result.push({ id, name });
  }
  return result;
}

function combinedResourceOptions(catalog, manualResources) {
  return [
    ...vgsResourceOptions(catalog),
    ...manualResources.map(item => ({ id: item.id, name: item.name, source: 'manual', editable: true })),
  ];
}

function normalizedResourceOverrides(raw, validIds) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  return Object.fromEntries(Object.entries(raw).filter(([key, value]) =>
    safeText(key, 240) && typeof value === 'string' && validIds.has(value)
  ));
}

function normalizedTrueOverrides(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  return Object.fromEntries(Object.entries(raw).filter(([key, value]) =>
    safeText(key, 240) && value === true
  ));
}

function valuesForKnownKeys(raw, allowed, predicate) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result = {};
  for (const [key, value] of Object.entries(raw)) if (allowed.has(key) && predicate(value)) result[key] = value;
  return result;
}

function normalReportDefinition(raw, lists, index) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const reportAttributes = new Set(['__scheduled_advance__', ...lists.attributes]);
  const attribute = reportAttributes.has(raw.attribute) ? raw.attribute : '';
  if (!attribute || !CALCULATIONS.has(raw.calculation)) return null;
  const id = safeText(raw.id, 80) || `report-${index + 1}`;
  const title = safeText(raw.title, 160) || 'Reporte';
  const unit = safeText(raw.unit, 80);
  const weightedBy = reportAttributes.has(raw.weightedBy) ? raw.weightedBy
    : reportAttributes.has(raw.weightAttribute) ? raw.weightAttribute : '__scheduled_advance__';
  const activityType = raw.activityType === '' || lists.types.has(raw.activityType) ? raw.activityType : '';
  const resource = raw.resource === '' || lists.resources.has(raw.resource) ? raw.resource : '';
  const filterAttribute = lists.attributes.has(raw.filterAttribute) ? raw.filterAttribute : '';
  const filterValue = filterAttribute ? safeText(raw.filterValue, 160) : '';
  const decimals = Number.isInteger(raw.decimals) && raw.decimals >= 0 && raw.decimals <= 6 ? raw.decimals : 2;
  return { id, title, attribute, unit, calculation: raw.calculation, weightedBy, activityType, resource, filterAttribute, filterValue, decimals };
}

function calendarParts(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '');
  if (!match) return null;
  const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
  if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month)) return null;
  return { year, month, day };
}

function daysInMonth(year, month) {
  if (month === 2) return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28;
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

function dayNumber(year, month, day) {
  const adjustedYear = year - (month <= 2 ? 1 : 0);
  const era = Math.floor(adjustedYear / 400);
  const yearOfEra = adjustedYear - era * 400;
  const adjustedMonth = month + (month > 2 ? -3 : 9);
  return era * 146097 + Math.floor((yearOfEra * 365 + Math.floor(yearOfEra / 4) - Math.floor(yearOfEra / 100)
    + Math.floor((153 * adjustedMonth + 2) / 5) + day - 1));
}

function dateFromDayNumber(number) {
  const era = Math.floor(number / 146097);
  const dayOfEra = number - era * 146097;
  const yearOfEra = Math.floor((dayOfEra - Math.floor(dayOfEra / 1460) + Math.floor(dayOfEra / 36524)
    - Math.floor(dayOfEra / 146096)) / 365);
  const year = yearOfEra + era * 400;
  const dayOfYear = dayOfEra - (365 * yearOfEra + Math.floor(yearOfEra / 4) - Math.floor(yearOfEra / 100));
  const adjustedMonth = Math.floor((5 * dayOfYear + 2) / 153);
  return { year: year + (adjustedMonth >= 10 ? 1 : 0), month: adjustedMonth + (adjustedMonth < 10 ? 3 : -9), day: dayOfYear - Math.floor((153 * adjustedMonth + 2) / 5) + 1 };
}

function dateText(parts) { return `${String(parts.year).padStart(4, '0')}-${String(parts.month).padStart(2, '0')}-${String(parts.day).padStart(2, '0')}`; }
function addDays(value, amount) { const parts = calendarParts(value); return parts ? dateText(dateFromDayNumber(dayNumber(parts.year, parts.month, parts.day) + amount)) : ''; }
function between(value, start, finish) { return value >= start && value <= finish; }

export function deriveCompatibilityCatalog(snapshot) {
  const activities = Array.isArray(snapshot?.activities) ? snapshot.activities : [];
  const typeById = new Map();
  const vgsActivityTypeById = new Map();
  const resourceById = new Map();
  const originalOrder = [];
  const attributes = [];
  const seenAttributes = new Set();
  const types = [];
  const resources = [];

  for (const activity of activities) {
    const id = String(activity?.id ?? '');
    originalOrder.push(id);
    const grid = activity?.grid && typeof activity.grid === 'object' && !Array.isArray(activity.grid) ? activity.grid : {};
    let type = '';
    let resource = '';
    for (const [key, value] of Object.entries(grid)) {
      const normalized = normalizedKey(key);
      if (TYPE_KEYS.has(normalized) && !type) type = text(value);
      if (RESOURCE_KEYS.has(normalized) && !resource) resource = text(value);
      if (!TYPE_KEYS.has(normalized) && !RESOURCE_KEYS.has(normalized) && !SENSITIVE_KEY.test(key) && !seenAttributes.has(key)) {
        seenAttributes.add(key);
        attributes.push(key);
      }
    }
    vgsActivityTypeById.set(id, Boolean(type));
    type = type || 'Sin tipo';
    resource = resource || 'Sin recurso';
    typeById.set(id, type);
    resourceById.set(id, resource);
    types.push(type);
    resources.push(resource);
  }

  return {
    attributes,
    types: sortedUnique(types),
    resources: sortedUnique(resources),
    resourceDescriptors: sortedUnique(resources).map(name => ({
      id: `vgs:${name}`, name, source: 'vgs', editable: false,
    })),
    originalOrder,
    typeFor(id) { return typeById.get(String(id)) || 'Sin tipo'; },
    hasVgsActivityTypeFor(id) { return vgsActivityTypeById.get(String(id)) === true; },
    resourceFor(id) { return resourceById.get(String(id)) || 'Sin recurso'; },
  };
}

export function normalizeCompatibilityPreferences(raw, catalog) {
  const value = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {};
  const baseLists = catalogLists(catalog);
  const manualResources = normalizeManualResources(value.manualResources, catalog);
  const resources = new Set([
    ...baseLists.resources,
    ...manualResources.map(item => item.name),
  ]);
  const lists = { ...baseLists, resources };
  const availableResourceIds = new Set(combinedResourceOptions(catalog, manualResources).map(item => item.id));
  const visibleAttributes = Array.isArray(value.visibleAttributes)
    ? sortedUnique(value.visibleAttributes.filter(attribute => lists.attributes.has(attribute))) : [];
  const typeColors = valuesForKnownKeys(value.typeColors, lists.types, color => typeof color === 'string' && COLOR.test(color));
  const resourceColors = valuesForKnownKeys(value.resourceColors, lists.resources, color => typeof color === 'string' && COLOR.test(color));
  const typeObjectives = valuesForKnownKeys(value.typeObjectives, lists.types, objective => Number.isFinite(finite(objective)) && objective >= 0);
  const resourceCapacities = valuesForKnownKeys(value.resourceCapacities ?? value.resourceFronts, lists.resources, capacity => [1, 2, 3, 4].includes(capacity));
  const reportDefinitions = Array.isArray(value.reportDefinitions)
    ? value.reportDefinitions.map((definition, index) => normalReportDefinition(definition, lists, index)).filter(Boolean) : [];
  const generalColors = { ...GENERAL_COLOR_DEFAULTS };
  if (value.generalColors && typeof value.generalColors === 'object' && !Array.isArray(value.generalColors)) {
    for (const key of GENERAL_COLOR_KEYS) if (typeof value.generalColors[key] === 'string' && COLOR.test(value.generalColors[key])) generalColors[key] = value.generalColors[key];
  }
  const printConfig = { ...PRINT_DEFAULTS };
  if (value.printConfig && typeof value.printConfig === 'object' && !Array.isArray(value.printConfig)) {
    for (const key of PRINT_TEXT_KEYS) {
      const candidate = safeText(value.printConfig[key], key === 'logoDataUrl' ? 1_000_000 : 160);
      if (candidate) printConfig[key] = candidate;
    }
    printConfig.includeDailyProgressCharts = value.printConfig.includeDailyProgressCharts === true;
    printConfig.includeGeneralProgressCharts = value.printConfig.includeGeneralProgressCharts === true;
  }
  const progressDate = typeof value.progressDate === 'string' && calendarParts(value.progressDate) ? value.progressDate : '';
  const activeTab = ACTIVE_TABS.has(value.activeTab) ? value.activeTab : 'type';

  return {
    showCompleted: typeof value.showCompleted === 'boolean' ? value.showCompleted : true,
    showPrecedences: typeof value.showPrecedences === 'boolean' ? value.showPrecedences : true,
    sortByStart: typeof value.sortByStart === 'boolean' ? value.sortByStart : false,
    sortByType: typeof value.sortByType === 'boolean' ? value.sortByType : false,
    sortByAttribute: value.sortByAttribute === true && baseLists.attributes.has(value.sortAttribute),
    sortAttribute: baseLists.attributes.has(value.sortAttribute) ? value.sortAttribute : '',
    sortDirection: value.sortDirection === 'desc' ? 'desc' : 'asc',
    showDailyProgressCharts: value.showDailyProgressCharts === true,
    extraDays: Number.isInteger(value.extraDays) && value.extraDays >= 0 && value.extraDays <= 31 ? value.extraDays : 0,
    visibleAttributes,
    showResources: value.showResources === true,
    colorMode: value.colorMode === 'resource' ? 'resource' : 'type',
    typeColors,
    resourceColors,
    typeObjectives,
    dailyFronts: [1, 2, 3, 4].includes(value.dailyFronts) ? value.dailyFronts : 1,
    resourceCapacities,
    manualResources,
    activityResourceOverrides: normalizedResourceOverrides(
      value.activityResourceOverrides, availableResourceIds,
    ),
    fragmentResourceOverrides: normalizedResourceOverrides(
      value.fragmentResourceOverrides, availableResourceIds,
    ),
    fragmentCapacityExemptions: normalizedTrueOverrides(value.fragmentCapacityExemptions),
    reportDefinitions,
    generalColors,
    printConfig,
    progressDate,
    activeTab,
  };
}

export function resourceOptions(catalog, preferences = {}) {
  return combinedResourceOptions(
    catalog,
    normalizeManualResources(preferences?.manualResources, catalog),
  );
}

export function effectiveResourceFor(activityId, logicalUnitId, catalog, preferences = {}) {
  const options = resourceOptions(catalog, preferences);
  const byId = new Map(options.map(item => [item.id, item]));
  const activity = String(activityId ?? '');
  const logicalUnit = String(logicalUnitId ?? '');
  const selected = preferences?.fragmentResourceOverrides?.[logicalUnit]
    ?? preferences?.activityResourceOverrides?.[activity]
    ?? `vgs:${catalog?.resourceFor?.(activity) || 'Sin recurso'}`;
  return byId.get(selected)
    || byId.get(`vgs:${catalog?.resourceFor?.(activity) || 'Sin recurso'}`)
    || { id: 'vgs:Sin recurso', name: 'Sin recurso', source: 'vgs', editable: false };
}

export function migrateCompatibilityPreferences(raw, catalog) {
  const source = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {};
  return normalizeCompatibilityPreferences({
    showCompleted: source.showCompleted,
    showPrecedences: source.showPrecedences,
    sortByStart: source.sortByStart,
    sortByType: source.sortByType,
    sortByAttribute: source.sortByAttribute,
    sortAttribute: source.sortAttribute,
    sortDirection: source.sortDirection,
    showDailyProgressCharts: source.showDailyProgressCharts,
    visibleAttributes: source.visibleAttributes,
    fragmentCapacityExemptions: source.fragmentCapacityExemptions,
    progressDate: source.progressDate,
    activeTab: source.activeTab,
  }, catalog);
}

function visibleStart(row) {
  if (typeof row?.visibleStart === 'string') return row.visibleStart;
  if (typeof row?.firstVisibleStart === 'string') return row.firstVisibleStart;
  const segments = Array.isArray(row?.segments) ? row.segments : [];
  const starts = segments.map(segment => segment?.start ?? segment?.date).filter(value => typeof value === 'string').sort();
  return starts[0] || '';
}

function sourceIndex(row, fallback) {
  for (const key of ['sourceIndex', 'originalIndex', 'vgsOrder', 'index']) if (Number.isInteger(row?.[key]) && row[key] >= 0) return row[key];
  return fallback;
}

export function orderCompatibilityRows(rows, preferences) {
  if (!Array.isArray(rows)) return [];
  const sortByType = preferences?.sortByType === true;
  const sortByStart = preferences?.sortByStart === true;
  const sortByAttribute = preferences?.sortByAttribute === true;
  const sortAttribute = text(preferences?.sortAttribute);
  const sortDirection = preferences?.sortDirection === 'desc' ? 'desc' : 'asc';
  return rows.map((row, index) => ({ row, index })).sort((left, right) => {
    if (sortByType) {
      const typeOrder = String(left.row?.activityType ?? left.row?.type ?? 'Sin tipo').localeCompare(String(right.row?.activityType ?? right.row?.type ?? 'Sin tipo'), 'es-MX', { sensitivity: 'base' });
      if (typeOrder) return typeOrder;
    }
    if (sortByAttribute && sortAttribute) {
      const attributeOrder = compareAttributeValues(
        left.row?.safeGridAttributes?.[sortAttribute],
        right.row?.safeGridAttributes?.[sortAttribute],
        sortDirection,
      );
      if (attributeOrder) return attributeOrder;
    }
    if (sortByStart) {
      const startOrder = visibleStart(left.row).localeCompare(visibleStart(right.row));
      if (startOrder) return startOrder;
    }
    return sourceIndex(left.row, left.index) - sourceIndex(right.row, right.index) || left.index - right.index;
  }).map(item => item.row);
}

export function clipContextDays(days, displayHorizon, extraDays) {
  const input = Array.isArray(days) ? days : [];
  const start = displayHorizon?.start ?? displayHorizon?.horizonStart?.slice?.(0, 10);
  const finish = displayHorizon?.finish ?? displayHorizon?.horizonFinish?.slice?.(0, 10);
  if (!calendarParts(start) || !calendarParts(finish) || start > finish) return { days: [], visibleFragmentDays: [] };
  const requestedExtra = Number.isInteger(extraDays) && extraDays > 0 ? extraDays : 0;
  const horizonDays = Number.isInteger(displayHorizon?.days) && displayHorizon.days > 0
    ? displayHorizon.days : dayNumber(...Object.values(calendarParts(finish))) - dayNumber(...Object.values(calendarParts(start))) + 1;
  const byDate = new Map(input.filter(day => typeof day?.date === 'string').map(day => [day.date, day]));
  const result = [];
  const visibleFragmentDays = [];
  for (let offset = 0; offset < horizonDays + requestedExtra; offset += 1) {
    const date = addDays(start, offset);
    const operating = between(date, start, finish);
    const original = byDate.get(date);
    const copy = original && typeof original === 'object' ? { ...original, date } : { date };
    const segments = operating && Array.isArray(original?.segments) ? [...original.segments] : [];
    copy.segments = segments;
    if (!operating && Array.isArray(copy.fragments)) copy.fragments = [];
    if (operating && segments.length) visibleFragmentDays.push(date);
    result.push(copy);
  }
  return { days: result, visibleFragmentDays };
}

function numeric(value) {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string' || !value.trim()) return null;
  const parsed = Number(value.trim());
  return Number.isFinite(parsed) ? parsed : null;
}

function safeAttributeName(name) {
  const key = normalizedKey(name);
  return Boolean(key) && !TYPE_KEYS.has(key) && !RESOURCE_KEYS.has(key) && !SENSITIVE_KEY.test(String(name));
}

function rowAttributes(row, view) {
  const result = new Map();
  const add = (name, value) => {
    if (safeAttributeName(name) && !result.has(String(name))) result.set(String(name), value);
  };
  const addObject = candidate => {
    if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) return;
    for (const [name, value] of Object.entries(candidate)) add(name, value);
  };
  if (Array.isArray(row?.attributes)) {
    for (const attribute of row.attributes) add(attribute?.name ?? attribute?.key, attribute?.value);
  } else addObject(row?.attributes);
  addObject(row?.safeGridAttributes);
  addObject(row?.safeGrid);
  addObject(row?.grid);
  const byActivity = view?.safeGridByActivity ?? view?.attributesByActivity;
  const activityAttributes = byActivity && typeof byActivity === 'object' ? byActivity[String(row?.activityId ?? row?.id ?? '')] : null;
  addObject(activityAttributes);
  return result;
}

function rowType(row) { return text(row?.activityType ?? row?.type) || 'Sin tipo'; }

function rowResources(row) {
  const values = Array.isArray(row?.resources) ? row.resources : [row?.resource ?? row?.resourceName ?? row?.resourceLabel];
  const result = sortedUnique(values.map(value => text(value)).filter(Boolean));
  return result.length ? result : ['Sin recurso'];
}

function segmentAmount(segment, attributes) {
  for (const key of ['scheduledAdvance', 'scheduled', 'quantity', 'advance']) {
    const value = numeric(segment?.[key]);
    if (value !== null && value >= 0) return value;
  }
  const width = numeric(segment?.widthRatio);
  const ratio = width !== null && width >= 0 ? width : 1;
  const rate = [...attributes.entries()].find(([name]) => normalizedKey(name) === 'rate')?.[1];
  const value = numeric(rate);
  return value !== null && value >= 0 ? value * ratio : ratio;
}

function visibleSegments(view) {
  const result = [];
  for (const row of Array.isArray(view?.rows) ? view.rows : []) {
    const activityId = String(row?.activityId ?? row?.id ?? '');
    if (!activityId) continue;
    const attributes = rowAttributes(row, view);
    for (const day of Array.isArray(row?.days) ? row.days : []) {
      const date = typeof day?.date === 'string' ? day.date : '';
      if (!date) continue;
      for (const segment of Array.isArray(day?.segments) ? day.segments : []) {
        if (!segment || typeof segment !== 'object') continue;
        result.push({
          activityId,
          type: rowType(row),
          resources: Array.isArray(segment.resources) && segment.resources.length
            ? segment.resources : text(segment.resource) ? [text(segment.resource)] : rowResources(row),
          attributes, date,
          segment, amount: segmentAmount(segment, attributes),
        });
      }
    }
  }
  return result;
}

function visibleDates(view, segments) {
  return sortedUnique([
    ...(Array.isArray(view?.days) ? view.days.map(day => day?.date).filter(date => typeof date === 'string') : []),
    ...segments.map(item => item.date),
  ]).sort();
}

function objectiveFor(preferences, type) {
  const value = numeric(preferences?.typeObjectives?.[type]);
  return value !== null && value >= 0 ? value : 0;
}

function resourceCapacityFor(preferences, resource) {
  const configured = preferences?.resourceCapacities?.[resource] ?? preferences?.resourceFronts?.[resource];
  if ([1, 2, 3, 4].includes(configured)) return configured;
  return [1, 2, 3, 4].includes(preferences?.dailyFronts) ? preferences.dailyFronts : 1;
}

function capacityDay(date, objective) {
  return { date, scheduled: 0, capacity: 0, objective, overCapacity: false };
}

function resourceDay(date, capacity) {
  return { date, activityIds: [], fronts: 0, capacity, overCapacity: false };
}

export function buildCapacityRows(view, preferences = {}) {
  const segments = visibleSegments(view);
  const dates = visibleDates(view, segments);
  const byType = Object.create(null);
  const byResource = Object.create(null);
  const ensureType = type => {
    if (!byType[type]) {
      const objective = objectiveFor(preferences, type);
      byType[type] = { type, objective, days: Object.fromEntries(dates.map(date => [date, capacityDay(date, objective)])) };
    }
    return byType[type];
  };
  const ensureResource = resource => {
    if (!byResource[resource]) {
      const capacity = resourceCapacityFor(preferences, resource);
      byResource[resource] = { resource, capacity, days: Object.fromEntries(dates.map(date => [date, resourceDay(date, capacity)])) };
    }
    return byResource[resource];
  };

  for (const item of segments) {
    if (item.segment.status !== 'completed') {
      const typeDay = ensureType(item.type).days[item.date];
      typeDay.scheduled += item.amount;
      typeDay.capacity += item.segment.exemptFromCapacity === true ? 0 : item.amount;
      for (const resource of item.resources) {
        const day = ensureResource(resource).days[item.date];
        if (!day.activityIds.includes(item.activityId)) day.activityIds.push(item.activityId);
      }
    }
  }
  for (const row of Object.values(byType)) {
    for (const day of Object.values(row.days)) day.overCapacity = day.objective > 0 && day.capacity > day.objective;
  }
  for (const row of Object.values(byResource)) {
    for (const day of Object.values(row.days)) {
      day.activityIds.sort();
      day.fronts = day.activityIds.length;
      day.overCapacity = day.fronts > day.capacity;
    }
  }
  return { byType, byResource };
}

function reportDefinitions(preferences) {
  return Array.isArray(preferences) ? preferences : Array.isArray(preferences?.reportDefinitions) ? preferences.reportDefinitions : [];
}

function matchesReport(item, definition) {
  if (text(definition?.activityType) && item.type !== definition.activityType) return false;
  if (text(definition?.resource) && !item.resources.includes(definition.resource)) return false;
  const attribute = text(definition?.filterAttribute);
  const expected = text(definition?.filterValue);
  return !attribute || !expected || text(item.attributes.get(attribute)) === expected;
}

function decimalPlaces(value) { return Number.isInteger(value) && value >= 0 && value <= 6 ? value : 2; }
function rounded(value, decimals) { return Number(value.toFixed(decimals)); }

export function buildReportRows(view, preferences = {}) {
  const segments = visibleSegments(view);
  const dates = visibleDates(view, segments);
  const scheduledByActivity = new Map();
  for (const item of segments) scheduledByActivity.set(item.activityId, (scheduledByActivity.get(item.activityId) || 0) + item.amount);

  return reportDefinitions(preferences).flatMap((definition, index) => {
    const calculation = definition?.calculation;
    const attribute = text(definition?.attribute);
    if (!attribute || !CALCULATIONS.has(calculation)) return [];
    const decimals = decimalPlaces(definition?.decimals);
    const days = {};
    for (const date of dates) {
      const matching = segments.filter(item => item.date === date && matchesReport(item, definition));
      const values = matching.map(item => {
        if (attribute === '__scheduled_advance__') return { item, value: item.amount };
        const total = numeric(item.attributes.get(attribute));
        const scheduled = scheduledByActivity.get(item.activityId) || 0;
        return total === null || scheduled <= 0 ? null : { item, value: total * item.amount / scheduled };
      }).filter(Boolean);
      let value = 0;
      if (values.length) {
        if (calculation === 'average') value = values.reduce((sum, entry) => sum + entry.value, 0) / values.length;
        else if (calculation === 'maximum') value = Math.max(...values.map(entry => entry.value));
        else if (calculation === 'minimum') value = Math.min(...values.map(entry => entry.value));
        else if (calculation === 'weightedAverage') {
          const weighted = values.map(entry => {
            const weightAttribute = text(definition?.weightedBy) || '__scheduled_advance__';
            const totalWeight = weightAttribute === '__scheduled_advance__' ? scheduledByActivity.get(entry.item.activityId) || 0 : numeric(entry.item.attributes.get(weightAttribute));
            const weight = totalWeight > 0 ? totalWeight * entry.item.amount / (scheduledByActivity.get(entry.item.activityId) || 1) : 0;
            const rawValue = attribute === '__scheduled_advance__' ? entry.item.amount : numeric(entry.item.attributes.get(attribute)) ?? 0;
            return { value: rawValue, weight };
          });
          const totalWeight = weighted.reduce((sum, entry) => sum + entry.weight, 0);
          value = totalWeight > 0 ? weighted.reduce((sum, entry) => sum + entry.value * entry.weight, 0) / totalWeight : 0;
        } else value = values.reduce((sum, entry) => sum + entry.value, 0);
      }
      days[date] = { value: rounded(value, decimals), decimals };
    }
    return [{
      id: safeText(definition?.id, 80) || `report-${index + 1}`,
      title: safeText(definition?.title, 160) || 'Reporte', unit: safeText(definition?.unit, 80),
      attribute, calculation, decimals, days,
    }];
  });
}

function actualProgressTypeDay(date) {
  return { date, scheduled: 0, actual: null };
}

function addProgressResource(total, resource, amount) {
  const name = text(resource) || 'Sin recurso';
  total[name] = (total[name] || 0) + amount;
}

function actualProgressResourceForDay(row, day) {
  const segments = Array.isArray(day?.segments) ? day.segments : [];
  const pending = segments.find(segment => segment?.status !== 'completed');
  if (text(pending?.resource)) return text(pending.resource);
  if (Array.isArray(pending?.resources) && text(pending.resources[0])) return text(pending.resources[0]);
  return rowResources(row)[0];
}

export function buildActualProgressByType(view, state = {}) {
  const segments = visibleSegments(view).filter(item => item.segment.status !== 'completed');
  const dates = visibleDates(view, segments);
  const byType = Object.create(null);
  const ensureType = type => {
    if (!byType[type]) {
      byType[type] = {
        type,
        days: Object.fromEntries(dates.map(date => [date, actualProgressTypeDay(date)])),
        plannedByResource: Object.create(null),
        actualByResource: Object.create(null),
        plannedByResourceByDate: Object.create(null),
        actualByResourceByDate: Object.create(null),
      };
    }
    return byType[type];
  };

  for (const item of segments) {
    const row = ensureType(item.type);
    row.days[item.date].scheduled += item.amount;
    addProgressResource(row.plannedByResource, item.resources[0], item.amount);
    row.plannedByResourceByDate[item.date] ??= Object.create(null);
    addProgressResource(row.plannedByResourceByDate[item.date], item.resources[0], item.amount);
  }

  for (const row of Array.isArray(view?.rows) ? view.rows : []) {
    const activityId = safeText(row?.activityId ?? row?.id, 240);
    if (!activityId) continue;
    const type = rowType(row);
    for (const day of Array.isArray(row?.days) ? row.days : []) {
      const date = typeof day?.date === 'string' && calendarParts(day.date) ? day.date : '';
      const hasPending = Array.isArray(day?.segments)
        && day.segments.some(segment => segment?.status !== 'completed');
      const actual = localActualProgress(state, activityId, date);
      if (!date || (!hasPending && !Number.isFinite(actual))) continue;
      if (!Number.isFinite(actual)) continue;
      const summary = ensureType(type);
      summary.days[date].actual = (summary.days[date].actual ?? 0) + actual;
      const resource = actualProgressResourceForDay(row, day);
      addProgressResource(summary.actualByResource, resource, actual);
      summary.actualByResourceByDate[date] ??= Object.create(null);
      addProgressResource(summary.actualByResourceByDate[date], resource, actual);
    }
  }
  return { dates, byType };
}

function copiedProgress(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? { ...value } : {};
}

function localActualKey(fragmentId, date) {
  const id = safeText(fragmentId, 240);
  return id && calendarParts(date) ? JSON.stringify([id, date]) : '';
}

export function localActualProgress(state, fragmentId, date) {
  const key = localActualKey(fragmentId, date);
  const value = key ? state?.actualProgressByDay?.[key] : undefined;
  return Number.isFinite(value) && value >= 0 ? value : undefined;
}

/**
 * Totals confined to the days already rendered for one activity. The caller
 * supplies a timeline row, so values elsewhere in VGS cannot affect the
 * visible-period summary.
 */
export function buildActivityPeriodTotals(row, state = {}, showActualProgress = false) {
  const activityId = safeText(row?.activityId ?? row?.id, 240);
  let scheduled = 0;
  let actual = 0;
  for (const day of Array.isArray(row?.days) ? row.days : []) {
    for (const segment of Array.isArray(day?.segments) ? day.segments : []) {
      const amount = Number(segment?.scheduledAmount);
      if (Number.isFinite(amount)) scheduled += amount;
    }
    if (!showActualProgress || !activityId || typeof day?.date !== 'string') continue;
    const value = localActualProgress(state, activityId, day.date);
    if (Number.isFinite(value)) actual += value;
  }
  return { scheduled, actual: showActualProgress ? actual : null };
}

function periodDateList(days) {
  return (Array.isArray(days) ? days : [])
    .map(day => typeof day === 'string' ? day : day?.date)
    .filter(date => typeof date === 'string' && calendarParts(date));
}

function sumPeriodFields(row, days, fields) {
  const result = Object.fromEntries(fields.map(field => [field, 0]));
  for (const date of periodDateList(days)) {
    const value = row?.days?.[date];
    for (const field of fields) {
      const amount = Number(value?.[field]);
      if (Number.isFinite(amount)) result[field] += amount;
    }
  }
  return result;
}

/** Totals for one capacity row, restricted to the supplied visible dates. */
export function buildCapacityPeriodTotals(row, days) {
  return sumPeriodFields(row, days, ['scheduled', 'objective']);
}

/** Totals for one actual-progress row, restricted to the supplied visible dates. */
export function buildActualProgressPeriodTotals(row, days) {
  return sumPeriodFields(row, days, ['scheduled', 'actual']);
}

export function hasLocalActualProgressSlot(state, activityId, date) {
  const key = localActualKey(activityId, date);
  return Boolean(key && state?.actualProgressSlotsByDay?.[key] === true);
}

export function hasHiddenLocalActualProgressSlot(state, activityId, date) {
  const key = localActualKey(activityId, date);
  return Boolean(key && state?.hiddenActualProgressSlotsByDay?.[key] === true);
}

export function hasVisibleLocalActualProgressSlotInDays(state, activityId, dates) {
  return Array.isArray(dates) && dates.some(date =>
    hasLocalActualProgressSlot(state, activityId, date)
      && !hasHiddenLocalActualProgressSlot(state, activityId, date)
  );
}

export function visibleLocalActualProgressActivityIdsInDays(state, dates) {
  const visibleDates = new Set((Array.isArray(dates) ? dates : []).filter(calendarParts));
  const activityIds = new Set();
  for (const [key, present] of Object.entries(state?.actualProgressSlotsByDay || {})) {
    if (present !== true) continue;
    try {
      const [activityId, date] = JSON.parse(key);
      const id = safeText(activityId, 240);
      if (id && visibleDates.has(date) && !hasHiddenLocalActualProgressSlot(state, id, date)) {
        activityIds.add(id);
      }
    } catch (_) { /* Ignore malformed local-only keys. */ }
  }
  return [...activityIds];
}

export function setHiddenLocalActualProgressSlot(state, activityId, date, hidden) {
  const source = standaloneLocalState(state);
  const key = localActualKey(activityId, date);
  if (!key) return source;
  const hiddenActualProgressSlotsByDay = copiedProgress(source.hiddenActualProgressSlotsByDay);
  if (hidden === true) hiddenActualProgressSlotsByDay[key] = true;
  else delete hiddenActualProgressSlotsByDay[key];
  return { ...source, hiddenActualProgressSlotsByDay };
}

export function setLocalActualProgressSlot(state, activityId, date, present) {
  const source = standaloneLocalState(state);
  const key = localActualKey(activityId, date);
  if (!key) return source;
  const actualProgressSlotsByDay = copiedProgress(source.actualProgressSlotsByDay);
  if (present === true) actualProgressSlotsByDay[key] = true;
  else delete actualProgressSlotsByDay[key];
  return { ...source, actualProgressSlotsByDay };
}

export function clearLocalActualProgressSlot(state, activityId, date, scheduled) {
  const source = standaloneLocalState(state);
  const key = localActualKey(activityId, date);
  if (!key) return source;
  const actualProgressByDay = copiedProgress(source.actualProgressByDay);
  const actualProgressSlotsByDay = copiedProgress(source.actualProgressSlotsByDay);
  const hiddenActualProgressSlotsByDay = copiedProgress(source.hiddenActualProgressSlotsByDay);
  delete actualProgressByDay[key];
  delete actualProgressSlotsByDay[key];
  if (scheduled === true) hiddenActualProgressSlotsByDay[key] = true;
  else delete hiddenActualProgressSlotsByDay[key];
  return {
    ...source,
    actualProgressByDay,
    actualProgressSlotsByDay,
    hiddenActualProgressSlotsByDay,
  };
}

export function restoreLocalActualProgressSlot(state, activityId, date, scheduled) {
  const source = standaloneLocalState(state);
  const key = localActualKey(activityId, date);
  if (!key) return source;
  const actualProgressSlotsByDay = copiedProgress(source.actualProgressSlotsByDay);
  const hiddenActualProgressSlotsByDay = copiedProgress(source.hiddenActualProgressSlotsByDay);
  delete hiddenActualProgressSlotsByDay[key];
  if (scheduled !== true) actualProgressSlotsByDay[key] = true;
  return { ...source, actualProgressSlotsByDay, hiddenActualProgressSlotsByDay };
}

export function setLocalActualProgress(state, fragmentId, dateOrValue, maybeValue) {
  const source = state && typeof state === 'object' && !Array.isArray(state) ? state : {};
  if (maybeValue !== undefined) {
    const actualProgressByDay = copiedProgress(source.actualProgressByDay);
    const key = localActualKey(fragmentId, dateOrValue);
    if (key && Number.isFinite(maybeValue) && maybeValue >= 0) actualProgressByDay[key] = maybeValue;
    return { ...source, actualProgressByDay };
  }
  const actualProgressByFragment = copiedProgress(source.actualProgressByFragment);
  const id = safeText(fragmentId, 240);
  if (id && Number.isFinite(dateOrValue) && dateOrValue >= 0) actualProgressByFragment[id] = dateOrValue;
  return { ...source, actualProgressByFragment };
}

export function moveLocalActualProgress(state, activityId, originDate, targetDate) {
  const source = standaloneLocalState(state);
  const originKey = localActualKey(activityId, originDate);
  const targetKey = localActualKey(activityId, targetDate);
  const value = originKey ? source.actualProgressByDay[originKey] : undefined;
  if (!originKey || !targetKey || originKey === targetKey || !Number.isFinite(value) || value <= 0) {
    return source;
  }
  const actualProgressByDay = copiedProgress(source.actualProgressByDay);
  delete actualProgressByDay[originKey];
  actualProgressByDay[targetKey] = value;
  return { ...source, actualProgressByDay };
}

export function visibleLocalActualProgressCascadeMoves(state, activityId, originDate, targetDate, visibleDates) {
  const source = standaloneLocalState(state);
  const activity = safeText(activityId, 240);
  const dates = [...new Set((Array.isArray(visibleDates) ? visibleDates : [])
    .filter(date => typeof date === 'string' && calendarParts(date)))];
  const originIndex = dates.indexOf(originDate);
  const targetIndex = dates.indexOf(targetDate);
  const offset = targetIndex - originIndex;
  const origin = localActualProgress(source, activity, originDate);
  if (!activity || originIndex < 0 || targetIndex < 0 || offset === 0 || !Number.isFinite(origin) || origin <= 0) {
    return [];
  }
  return dates
    .filter((date, index) => (offset > 0 ? index >= originIndex : index <= originIndex))
    .map(date => ({ date, value: localActualProgress(source, activity, date) }))
    .filter(item => Number.isFinite(item.value) && item.value > 0)
    .map(item => ({
      originDate: item.date,
      targetDate: addDays(item.date, offset),
      value: item.value,
    }))
    .filter(item => Boolean(item.targetDate));
}

export function moveVisibleLocalActualProgressCascade(state, activityId, originDate, targetDate, visibleDates) {
  const source = standaloneLocalState(state);
  const moves = visibleLocalActualProgressCascadeMoves(source, activityId, originDate, targetDate, visibleDates);
  if (!moves.length) return source;
  const activity = safeText(activityId, 240);
  const actualProgressByDay = copiedProgress(source.actualProgressByDay);
  for (const move of moves) delete actualProgressByDay[localActualKey(activity, move.originDate)];
  for (const move of moves) actualProgressByDay[localActualKey(activity, move.targetDate)] = move.value;
  return { ...source, actualProgressByDay };
}

export function visibleLocalActualProgressSlotCascadeMoves(
  state, activityId, originDate, targetDate, visibleDates, visibleSlotDates,
) {
  const activity = safeText(activityId, 240);
  const dates = [...new Set((Array.isArray(visibleDates) ? visibleDates : [])
    .filter(date => typeof date === 'string' && calendarParts(date)))];
  const originIndex = dates.indexOf(originDate);
  const targetIndex = dates.indexOf(targetDate);
  const offset = targetIndex - originIndex;
  if (!activity || originIndex < 0 || targetIndex < 0 || offset === 0) return [];
  if (offset < 0) {
    return [{
      originDate,
      targetDate,
      value: localActualProgress(state, activity, originDate),
    }];
  }
  const slots = new Set(Array.isArray(visibleSlotDates) ? visibleSlotDates : []);
  return dates
    .filter((date, index) => slots.has(date) && index >= originIndex)
    .map(date => ({
      originDate: date,
      targetDate: addDays(date, offset),
      value: localActualProgress(state, activity, date),
    }));
}

export function moveVisibleLocalActualProgressSlotCascade(
  state, activityId, originDate, targetDate, visibleDates, visibleSlotDates,
) {
  const source = standaloneLocalState(state);
  const moves = visibleLocalActualProgressSlotCascadeMoves(
    source, activityId, originDate, targetDate, visibleDates, visibleSlotDates,
  );
  if (!moves.length) return source;
  const activity = safeText(activityId, 240);
  const actualProgressByDay = copiedProgress(source.actualProgressByDay);
  for (const move of moves) delete actualProgressByDay[localActualKey(activity, move.originDate)];
  for (const move of moves) {
    const key = localActualKey(activity, move.targetDate);
    if (Number.isFinite(move.value) && move.value >= 0) actualProgressByDay[key] = move.value;
    else delete actualProgressByDay[key];
  }
  return { ...source, actualProgressByDay };
}

export function migrateLocalActualProgress(state, visibleSegments) {
  const source = standaloneLocalState(state);
  const actualProgressByDay = {};
  const activityDayByFragmentDay = new Map();
  const firstActivityDayByFragment = new Map();
  const visibleActivityDays = new Set();
  for (const segment of Array.isArray(visibleSegments) ? visibleSegments : []) {
    const id = safeText(segment?.fragmentId, 240);
    const date = typeof segment?.date === 'string' && calendarParts(segment.date) ? segment.date : '';
    const activityId = safeText(segment?.activityId, 240) || id;
    if (!id || !date || !activityId) continue;
    const fragmentDay = localActualKey(id, date);
    const activityDay = localActualKey(activityId, date);
    activityDayByFragmentDay.set(fragmentDay, activityDay);
    visibleActivityDays.add(activityDay);
    if (!firstActivityDayByFragment.has(id)) firstActivityDayByFragment.set(id, activityDay);
  }
  const add = (key, value) => {
    if (!key || !Number.isFinite(value) || value < 0) return;
    actualProgressByDay[key] = (actualProgressByDay[key] || 0) + value;
  };
  for (const [key, value] of Object.entries(source.actualProgressByDay)) {
    const target = visibleActivityDays.has(key)
      ? key : (activityDayByFragmentDay.get(key) || key);
    add(target, value);
  }
  for (const [fragmentId, value] of Object.entries(source.actualProgressByFragment)) {
    add(firstActivityDayByFragment.get(fragmentId) || '', value);
  }
  return {
    actualProgressByDay,
    actualProgressSlotsByDay: copiedProgress(source.actualProgressSlotsByDay),
    hiddenActualProgressSlotsByDay: copiedProgress(source.hiddenActualProgressSlotsByDay),
  };
}

export function standaloneLocalState(state) {
  return {
    actualProgressByDay: copiedProgress(state?.actualProgressByDay),
    actualProgressByFragment: copiedProgress(state?.actualProgressByFragment),
    actualProgressSlotsByDay: copiedProgress(state?.actualProgressSlotsByDay),
    hiddenActualProgressSlotsByDay: copiedProgress(state?.hiddenActualProgressSlotsByDay),
  };
}
