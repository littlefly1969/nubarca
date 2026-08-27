const fs = require('node:fs');
const path = require('node:path');

const CONTRACT_PATH = path.resolve(__dirname, '..', 'release-contract.json');

function readReleaseContract(contractPath = CONTRACT_PATH) {
  const value = JSON.parse(fs.readFileSync(contractPath, 'utf8'));
  const requiredStrings = ['applicationName', 'package', 'version', 'uploadSignerSha256'];
  for (const key of requiredStrings) {
    if (typeof value[key] !== 'string' || value[key].trim() === '') {
      throw new Error(`mobile release contract ${key} must be a non-empty string`);
    }
  }
  if (!/^\d+\.\d+\.\d+$/.test(value.version)) {
    throw new Error('mobile release contract version must be X.Y.Z');
  }
  if (!/^[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)+$/.test(value.package)) {
    throw new Error('mobile release contract package is not a valid Android applicationId');
  }
  for (const key of ['versionCode', 'minSdk', 'targetSdk']) {
    if (!Number.isSafeInteger(value[key]) || value[key] <= 0) {
      throw new Error(`mobile release contract ${key} must be a positive integer`);
    }
  }
  if (!/^[0-9a-f]{64}$/.test(value.uploadSignerSha256)) {
    throw new Error('mobile release contract uploadSignerSha256 must be lowercase SHA-256');
  }
  return Object.freeze(value);
}

function normalizePublicOrigin(raw) {
  if (typeof raw !== 'string' || raw.trim() === '') {
    throw new Error('NUBARCA_PUBLIC_ORIGIN is required for a production mobile build.');
  }
  let parsed;
  try {
    parsed = new URL(raw.trim());
  } catch {
    throw new Error('NUBARCA_PUBLIC_ORIGIN must be a valid https:// origin.');
  }
  if (
    parsed.protocol !== 'https:' ||
    parsed.username ||
    parsed.password ||
    parsed.pathname !== '/' ||
    parsed.search ||
    parsed.hash
  ) {
    throw new Error('NUBARCA_PUBLIC_ORIGIN must be an https:// origin without credentials, path, query, or fragment.');
  }
  return parsed.origin;
}

module.exports = { CONTRACT_PATH, normalizePublicOrigin, readReleaseContract };
