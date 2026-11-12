/**
 * Passkeys in the browser (WebAuthn): the server sends options with binary fields as base64url,
 * navigator.credentials turns them into a key pair (create) or a signature (get), and the result goes back as base64url.
 */

export function b64urlToBytes(s: string): Uint8Array {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(b64 + '='.repeat((4 - (b64.length % 4)) % 4));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

export function bytesToB64url(buf: ArrayBuffer | Uint8Array): string {
  const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export const passkeysSupported = () =>
  typeof window !== 'undefined' && typeof window.PublicKeyCredential === 'function' && !!navigator.credentials;

interface CredentialRef { type: 'public-key'; id: string }

export interface CreationOptionsJson {
  challenge: string;
  rp: { id: string; name: string };
  user: { id: string; name: string; displayName: string };
  pubKeyCredParams: { type: 'public-key'; alg: number }[];
  timeout?: number;
  attestation?: AttestationConveyancePreference;
  authenticatorSelection?: AuthenticatorSelectionCriteria;
  excludeCredentials?: CredentialRef[];
}

export interface RequestOptionsJson {
  challenge: string;
  rpId: string;
  timeout?: number;
  userVerification?: UserVerificationRequirement;
  allowCredentials?: CredentialRef[];
}

const refs = (list: CredentialRef[] | undefined) => (list ?? []).map((c) => ({ type: c.type, id: b64urlToBytes(c.id) }));

/** Makes a new passkey on this device (fingerprint, face or device PIN). */
export async function createPasskey(o: CreationOptionsJson) {
  const cred = (await navigator.credentials.create({
    publicKey: {
      ...o,
      challenge: b64urlToBytes(o.challenge),
      user: { ...o.user, id: b64urlToBytes(o.user.id) },
      excludeCredentials: refs(o.excludeCredentials),
    },
  })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Устройство не создало ключ доступа');
  const r = cred.response as AuthenticatorAttestationResponse;
  return {
    id: cred.id,
    rawId: bytesToB64url(cred.rawId),
    type: cred.type,
    response: { clientDataJSON: bytesToB64url(r.clientDataJSON), attestationObject: bytesToB64url(r.attestationObject) },
  };
}

/** Signs the login challenge with a passkey the device has for this site. */
export async function getPasskey(o: RequestOptionsJson) {
  const cred = (await navigator.credentials.get({
    publicKey: { ...o, challenge: b64urlToBytes(o.challenge), allowCredentials: refs(o.allowCredentials) },
  })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Вход отменён');
  const r = cred.response as AuthenticatorAssertionResponse;
  return {
    id: cred.id,
    rawId: bytesToB64url(cred.rawId),
    type: cred.type,
    response: {
      clientDataJSON: bytesToB64url(r.clientDataJSON),
      authenticatorData: bytesToB64url(r.authenticatorData),
      signature: bytesToB64url(r.signature),
      userHandle: r.userHandle ? bytesToB64url(r.userHandle) : null,
    },
  };
}

/** Browser errors in the owner's words. */
export function passkeyError(e: unknown): string {
  const name = (e as { name?: string })?.name;
  if (name === 'NotAllowedError') return 'Отменено или время вышло. Попробуйте ещё раз.';
  if (name === 'InvalidStateError') return 'На этом устройстве ключ доступа уже добавлен.';
  if (name === 'SecurityError') return 'Ключи доступа работают только по защищённому адресу сервиса (https).';
  if (name === 'NotSupportedError') return 'Это устройство не поддерживает вход по отпечатку или лицу.';
  return (e as Error)?.message ?? 'Не получилось';
}
