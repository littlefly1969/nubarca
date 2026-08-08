const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');

const DEFAULT_CONTRACT_PATH = resolve(__dirname, '..', 'release-contract.json');
const SHA256_HEX = /^[0-9a-f]{64}$/;
const SAFE_SEGMENT = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const CONTRACT_FIELDS = [
  'applicationName', 'package', 'version', 'versionCode', 'runtimeVersion',
  'channel', 'updatePath', 'apkSignerSha256',
];

function requireString(value, name) {
  if (typeof value !== 'string' || value.trim() !== value || value.length === 0) {
    throw new Error(`TV release contract ${name} must be a non-empty trimmed string`);
  }
  return value;
}

function readReleaseContract(file = DEFAULT_CONTRACT_PATH) {
  let value;
  try {
    value = JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    throw new Error(`Unable to read TV release contract: ${error.message}`);
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('TV release contract must be a JSON object');
  }
  const fields = Object.keys(value).sort();
  if (fields.join('\n') !== [...CONTRACT_FIELDS].sort().join('\n')) {
    throw new Error(`TV release contract must contain exactly: ${CONTRACT_FIELDS.join(', ')}`);
  }

  const contract = {
    applicationName: requireString(value.applicationName, 'applicationName'),
    package: requireString(value.package, 'package'),
    version: requireString(value.version, 'version'),
    versionCode: value.versionCode,
    runtimeVersion: requireString(value.runtimeVersion, 'runtimeVersion'),
    channel: requireString(value.channel, 'channel'),
    updatePath: requireString(value.updatePath, 'updatePath'),
    apkSignerSha256: requireString(value.apkSignerSha256, 'apkSignerSha256'),
  };
  if (!/^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$/.test(contract.package)) {
    throw new Error('TV release contract package is not a valid Android applicationId');
  }
  if (!/^\d+\.\d+\.\d+$/.test(contract.version)) {
    throw new Error('TV release contract version must use major.minor.patch');
  }
  if (!Number.isInteger(contract.versionCode) || contract.versionCode < 1) {
    throw new Error('TV release contract versionCode must be a positive integer');
  }
  if (!SAFE_SEGMENT.test(contract.runtimeVersion) || !SAFE_SEGMENT.test(contract.channel)) {
    throw new Error('TV release contract runtimeVersion and channel must be safe path segments');
  }
  if (!/^\/[A-Za-z0-9._~!$&'()*+,;=:@%/-]+$/.test(contract.updatePath)
      || contract.updatePath.includes('//') || contract.updatePath.endsWith('/')) {
    throw new Error('TV release contract updatePath must be one absolute URL path without a trailing slash');
  }
  try {
    const segments = contract.updatePath.slice(1).split('/').map(decodeURIComponent);
    if (segments.some((segment) => !segment || segment === '.' || segment === '..'
        || segment.includes('/') || segment.includes('\\'))) {
      throw new Error('unsafe segment');
    }
  } catch {
    throw new Error('TV release contract updatePath contains an unsafe or invalid encoded segment');
  }
  if (!SHA256_HEX.test(contract.apkSignerSha256)) {
    throw new Error('TV release contract apkSignerSha256 must be 64 lowercase hexadecimal characters');
  }
  return Object.freeze(contract);
}

function normalizePublicOrigin(value) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error('NUBARCA_PUBLIC_ORIGIN is required');
  }
  let parsed;
  try {
    parsed = new URL(value.trim());
  } catch {
    throw new Error('NUBARCA_PUBLIC_ORIGIN must be an absolute https:// origin');
  }
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.search || parsed.hash
      || (parsed.pathname !== '/' && parsed.pathname !== '')) {
    throw new Error('NUBARCA_PUBLIC_ORIGIN must be an https:// origin without path, credentials, query, or fragment');
  }
  return parsed.origin;
}

module.exports = {
  DEFAULT_CONTRACT_PATH,
  normalizePublicOrigin,
  readReleaseContract,
};
