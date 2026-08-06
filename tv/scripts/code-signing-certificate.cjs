const { X509Certificate } = require('node:crypto');
const { readFileSync } = require('node:fs');

const CODE_SIGNING_OID = '1.3.6.1.5.5.7.3.3';
const KEY_USAGE_OID_DER = Buffer.from([0x06, 0x03, 0x55, 0x1d, 0x0f]);

function readDerValue(buffer, offset, expectedTag) {
  if (buffer[offset] !== expectedTag) return null;
  let cursor = offset + 1;
  let length = buffer[cursor++];
  if ((length & 0x80) !== 0) {
    const bytes = length & 0x7f;
    if (bytes === 0 || bytes > 4 || cursor + bytes > buffer.length) return null;
    length = 0;
    for (let index = 0; index < bytes; index += 1) length = (length * 256) + buffer[cursor++];
  }
  if (cursor + length > buffer.length) return null;
  return { value: buffer.subarray(cursor, cursor + length), next: cursor + length };
}

function hasDigitalSignatureKeyUsage(rawCertificate) {
  const oid = rawCertificate.indexOf(KEY_USAGE_OID_DER);
  if (oid < 0) return false;
  let cursor = oid + KEY_USAGE_OID_DER.length;
  const critical = readDerValue(rawCertificate, cursor, 0x01);
  if (critical) cursor = critical.next;
  const extension = readDerValue(rawCertificate, cursor, 0x04);
  if (!extension) return false;
  const bitString = readDerValue(extension.value, 0, 0x03);
  // First BIT STRING byte is the count of unused bits; Digital Signature is bit 0.
  return Boolean(bitString && bitString.value.length >= 2 && (bitString.value[1] & 0x80) !== 0);
}

function validateCodeSigningCertificate(certificatePath) {
  let certificate;
  try {
    const contents = readFileSync(certificatePath);
    if (/-----BEGIN (?:RSA |EC )?PRIVATE KEY-----/.test(contents.toString('utf8'))) {
      throw new Error('the public OTA certificate file must not contain a private key');
    }
    certificate = new X509Certificate(contents);
  } catch (error) {
    throw new Error(`Invalid OTA code-signing certificate: ${error.message}`);
  }

  const now = Date.now();
  if (now < Date.parse(certificate.validFrom) || now > Date.parse(certificate.validTo)) {
    throw new Error('OTA code-signing certificate is not currently valid');
  }
  if (certificate.publicKey.asymmetricKeyType !== 'rsa') {
    throw new Error('OTA code-signing certificate must use an RSA public key');
  }
  if (!certificate.keyUsage?.includes(CODE_SIGNING_OID)) {
    throw new Error('OTA certificate must have Extended Key Usage: Code Signing');
  }

  // Node exposes Extended Key Usage but not the X.509 Key Usage bitset, so read
  // the small DER KeyUsage extension directly. Android expo-updates requires both.
  if (!hasDigitalSignatureKeyUsage(certificate.raw)) {
    throw new Error('OTA certificate must have Key Usage: Digital Signature and Extended Key Usage: Code Signing');
  }
  if (certificate.subject !== certificate.issuer || !certificate.verify(certificate.publicKey)) {
    throw new Error('OTA code-signing certificate must be self-signed');
  }
  return certificate;
}

module.exports = { validateCodeSigningCertificate };
