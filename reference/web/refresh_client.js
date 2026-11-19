/* DOM-free authenticated snapshot refresh boundary for STP V2. */

const PROJECT_KEY = /^[0-9a-f]{64}$/;
const BRIDGE_ID = /^[0-9a-f]{32}$/;
const ENDPOINT = /^http:\/\/127\.0\.0\.1:(\d{1,5})$/;
const BRIDGE_FIELDS = ['mode', 'endpoint', 'token', 'projectKey', 'bridgeId', 'message'];
const PLANNER_POLICY_FIELD = 'precedenceConflictPolicy';
const PLANNER_POLICIES = new Set(['allow-inherited', 'block-inherited']);
const SNAPSHOT_FIELDS = [
  'schema', 'projectName', 'projectPath', 'projectKey', 'projectFingerprint',
  'precedenceFingerprint', 'exportedAt', 'dateFormat', 'activities', 'precedences',
  'snapshotComplete', 'applyAllowed',
];
const CONFIG_ERROR = 'Configuración de actualización en vivo inválida.';
const REQUEST_ERROR = 'No se pudo consultar la actualización en vivo.';
const RESPONSE_ERROR = 'El servicio de actualización en vivo no respondió correctamente.';
const JSON_ERROR = 'La actualización recibida no contiene datos válidos.';
const SNAPSHOT_ERROR = 'La actualización recibida no es válida para este proyecto.';
const PREPARATION_ERROR = 'La actualización recibida no pudo prepararse.';

function own(value, key) { return Object.prototype.hasOwnProperty.call(value, key); }

function plainObject(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  return Object.getPrototypeOf(value) === Object.prototype;
}

function exactKeys(value, keys) {
  if (!plainObject(value)) return false;
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  return actual.length === expected.length && actual.every((key, index) => key === expected[index]);
}

function captureExactDataObject(value, keys) {
  if (!plainObject(value)) return null;
  const descriptors = Object.getOwnPropertyDescriptors(value);
  const actual = Reflect.ownKeys(descriptors);
  if (actual.length !== keys.length || actual.some(key => typeof key !== 'string' || !keys.includes(key))) return null;
  const captured = {};
  for (const key of keys) {
    const descriptor = descriptors[key];
    if (!descriptor || descriptor.enumerable !== true || !own(descriptor, 'value')) return null;
    captured[key] = descriptor.value;
  }
  return Object.freeze(captured);
}

function captureBridgeConfig(value) {
  if (!plainObject(value)) return null;
  const descriptors = Object.getOwnPropertyDescriptors(value);
  const actual = Reflect.ownKeys(descriptors);
  const allowed = [...BRIDGE_FIELDS, PLANNER_POLICY_FIELD];
  if (actual.length < BRIDGE_FIELDS.length || actual.length > allowed.length
      || actual.some(key => typeof key !== 'string' || !allowed.includes(key))) return null;
  const captured = {};
  for (const key of BRIDGE_FIELDS) {
    const descriptor = descriptors[key];
    if (!descriptor || descriptor.enumerable !== true || !own(descriptor, 'value')) return null;
    captured[key] = descriptor.value;
  }
  const policy = descriptors[PLANNER_POLICY_FIELD];
  if (policy && (policy.enumerable !== true || !own(policy, 'value') || !PLANNER_POLICIES.has(policy.value))) return null;
  return Object.freeze(captured);
}

function validEndpoint(value) {
  if (typeof value !== 'string') return false;
  const match = ENDPOINT.exec(value);
  if (!match || match[0] !== value) return false;
  const port = Number(match[1]);
  return Number.isInteger(port) && port >= 1 && port <= 65535;
}

function validToken(value) {
  return typeof value === 'string' && value.length >= 16 && !/[\u0000-\u001f\u007f]/.test(value);
}

function validMessage(value) {
  return typeof value === 'string'
    && value.length >= 1
    && value.length <= 240
    && !/[<>&\u0000-\u001f\u007f]/.test(value);
}

function validFingerprint(value) { return typeof value === 'string' && PROJECT_KEY.test(value); }

function safely(validate, ...values) {
  try { return validate(...values); } catch (_) { return false; }
}

function validBridgeConfig(value) {
  return exactKeys(value, BRIDGE_FIELDS)
    && value.mode === 'live'
    && validEndpoint(value.endpoint)
    && validToken(value.token)
    && validFingerprint(value.projectKey)
    && typeof value.bridgeId === 'string' && BRIDGE_ID.test(value.bridgeId)
    && validMessage(value.message);
}

function validCurrentSnapshot(value, bridgeConfig) {
  return plainObject(value)
    && typeof value.projectPath === 'string' && value.projectPath.length > 0
    && validFingerprint(value.projectKey)
    && value.projectKey === bridgeConfig.projectKey
    && validFingerprint(value.projectFingerprint)
    && validFingerprint(value.precedenceFingerprint);
}

function validRefreshedSnapshot(value, current) {
  if (!exactKeys(value, SNAPSHOT_FIELDS)) return false;
  return value.schema === 'vgs-stp-snapshot/2'
    && typeof value.projectName === 'string'
    && typeof value.projectPath === 'string'
    && typeof value.exportedAt === 'string'
    && typeof value.dateFormat === 'string'
    && value.projectPath === current.projectPath
    && value.projectKey === current.projectKey
    && validFingerprint(value.projectFingerprint)
    && validFingerprint(value.precedenceFingerprint)
    && value.snapshotComplete === true
    && value.applyAllowed === false
    && Array.isArray(value.activities)
    && Array.isArray(value.precedences);
}

function cloneSnapshot(value) {
  try {
    if (typeof globalThis.structuredClone === 'function') return globalThis.structuredClone(value);
    return JSON.parse(JSON.stringify(value));
  } catch (_) {
    throw new Error(SNAPSHOT_ERROR);
  }
}

function staleSnapshotError() {
  const error = new Error(SNAPSHOT_ERROR);
  Object.defineProperty(error, 'status', { value: 409 });
  return error;
}

/**
 * True only when both snapshots name the exact same immutable VGS state.
 * This is deliberately DOM-free so the UI can stop package preparation before
 * it offers an out-of-date local plan to the live bridge.
 */
export function snapshotIdentityMatches(currentSnapshot, liveSnapshot) {
  return safely(
    (current, live) => validFingerprint(current?.projectKey)
      && validFingerprint(current?.projectFingerprint)
      && validFingerprint(current?.precedenceFingerprint)
      && validFingerprint(live?.projectKey)
      && validFingerprint(live?.projectFingerprint)
      && validFingerprint(live?.precedenceFingerprint)
      && current.projectKey === live.projectKey
      && current.projectFingerprint === live.projectFingerprint
      && current.precedenceFingerprint === live.precedenceFingerprint,
    currentSnapshot,
    liveSnapshot,
  );
}

export async function fetchRefreshedSnapshot(fetchImpl, bridgeConfig, currentSnapshot) {
  const bridge = safely(captureBridgeConfig, bridgeConfig);
  if (typeof fetchImpl !== 'function' || !bridge || !safely(validBridgeConfig, bridge)) throw new Error(CONFIG_ERROR);
  if (!safely(validCurrentSnapshot, currentSnapshot, bridge)) throw new Error(SNAPSHOT_ERROR);

  let response;
  try {
    response = await fetchImpl(`${bridge.endpoint}/snapshot`, {
      method: 'GET',
      cache: 'no-store',
      headers: { 'X-STP-V2-Token': bridge.token },
    });
  } catch (_) {
    throw new Error(REQUEST_ERROR);
  }
  let stale = false;
  let responseOk = false;
  try {
    stale = Boolean(response && response.status === 409);
    responseOk = Boolean(response && response.ok === true);
  } catch (_) {
    throw new Error(RESPONSE_ERROR);
  }
  if (!responseOk && !stale) throw new Error(RESPONSE_ERROR);
  if (stale) throw staleSnapshotError();

  let snapshot;
  try {
    if (typeof response.json !== 'function') throw new TypeError('missing json');
    snapshot = await response.json();
  } catch (_) {
    throw new Error(JSON_ERROR);
  }
  let candidate;
  try {
    candidate = cloneSnapshot(snapshot);
  } catch (_) {
    throw new Error(SNAPSHOT_ERROR);
  }
  if (!safely(validRefreshedSnapshot, candidate, currentSnapshot)) throw new Error(SNAPSHOT_ERROR);
  return candidate;
}

export async function prepareRefreshedSession(fetchImpl, bridgeConfig, currentSnapshot, prepareCandidate) {
  if (typeof prepareCandidate !== 'function') throw new Error('Preparación de actualización inválida.');
  const snapshot = await fetchRefreshedSnapshot(fetchImpl, bridgeConfig, currentSnapshot);
  let prepared;
  try {
    prepared = await prepareCandidate(snapshot);
  } catch (_) {
    throw new Error(PREPARATION_ERROR);
  }
  if (!prepared || typeof prepared !== 'object') throw new Error(PREPARATION_ERROR);
  return { snapshot, prepared };
}
