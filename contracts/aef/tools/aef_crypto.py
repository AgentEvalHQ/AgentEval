#!/usr/bin/env python3
"""Signatures for the AEF reference tools: DSSE v1 envelopes signed with ECDSA P-256 / SHA-256 or with Ed25519.

It generates the conformance corpus's signature vectors (deterministically: nothing here uses randomness) and checks
signatures in the reference verifier. Standard library only.

Not for production key handling. Nothing is constant-time, secrets are plain Python integers and bytes, and the
code is written to be read and checked, not to be fast.

It follows:
- DSSE v1 (github.com/secure-systems-lab/dsse, protocol.md and envelope.md): PAE and the JSON envelope;
- ECDSA (FIPS 186-4, SEC 1) over NIST P-256 (secp256r1) with SHA-256, curve parameters from RFC 5903 section 3.1;
- RFC 6979 section 3.2 for the nonce (HMAC-SHA256). A signature keeps the s the nonce gives, above n/2 about half
  the time: ECDSA accepts both s and n - s, and so does verification here (see ecdsa_verify_digest);
- X.690 DER for the ECDSA signature, SEQUENCE { INTEGER r, INTEGER s }, as DSSE, in-toto and Sigstore use it,
  parsed strictly: minimal lengths and integers, nothing after it, r and s in [1, n-1];
- RFC 8032 section 5.1 for Ed25519 (pure Ed25519, no context): S < L, canonical point encodings only;
- RFC 5480 and RFC 8410 for the public keys' SubjectPublicKeyInfo (P-256 keys as uncompressed points only);
- a key id is "sha256:" + the lowercase hex SHA-256 of the SubjectPublicKeyInfo DER.

`python aef_crypto.py --self-test` checks the RFC test vectors and the round trips and exits 1 on any failure.
"""
import base64
import hashlib
import hmac
import re
import sys
from dataclasses import dataclass
from typing import NamedTuple

ECDSA_P256 = "ecdsa-sha2-nistp256"  # algorithm names as verify_envelope reports them (the TUF / in-toto scheme names)
ED25519 = "ed25519"

# NIST P-256: y^2 = x^3 - 3x + b over GF(p); G has prime order n; cofactor 1 (RFC 5903 section 3.1).
P256_P = 0xFFFFFFFF_00000001_00000000_00000000_00000000_FFFFFFFF_FFFFFFFF_FFFFFFFF
P256_B = 0x5AC635D8_AA3A93E7_B3EBBD55_769886BC_651D06B0_CC53B0F6_3BCE3C3E_27D2604B
P256_N = 0xFFFFFFFF_00000000_FFFFFFFF_FFFFFFFF_BCE6FAAD_A7179E84_F3B9CAC2_FC632551
P256_G = (0x6B17D1F2_E12C4247_F8BCE6E5_63A440F2_77037D81_2DEB33A0_F4A13945_D898C296,
          0x4FE342E2_FE1A7F9B_8EE7EB4A_7C0F9E16_2BCE3357_6B315ECE_CBB64068_37BF51F5)

# Ed25519: -x^2 + y^2 = 1 + d x^2 y^2 over GF(p); B has prime order L (RFC 8032 section 5.1, table 1).
ED25519_P = 2**255 - 19
ED25519_L = 2**252 + 27742317777372353535851937790883648493
ED25519_D = -121665 * pow(121666, -1, ED25519_P) % ED25519_P
_SQRT_M1 = pow(2, (ED25519_P - 1) // 4, ED25519_P)  # a square root of -1 mod p
_ED_ZERO = (0, 1, 1, 0)  # the neutral point in extended coordinates (X, Y, Z, T)


# ---------------------------------------------------------------------------- P-256 arithmetic

def _p256_on_curve(x, y):
    return (isinstance(x, int) and isinstance(y, int) and 0 <= x < P256_P and 0 <= y < P256_P
            and (y * y - (x * x * x - 3 * x + P256_B)) % P256_P == 0)


def _p256_add(p1, p2):
    """Affine addition; None is the point at infinity."""
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    (x1, y1), (x2, y2) = p1, p2
    if x1 == x2:
        if (y1 + y2) % P256_P == 0:
            return None  # P + (-P)
        slope = (3 * x1 * x1 - 3) * pow(2 * y1, -1, P256_P)  # doubling: (3x^2 + a) / 2y, a = -3
    else:
        slope = (y2 - y1) * pow(x2 - x1, -1, P256_P)
    x3 = (slope * slope - x1 - x2) % P256_P
    return x3, (slope * (x1 - x3) - y1) % P256_P


def _p256_mul(k, point):
    """k * point, by double-and-add."""
    result = None
    while k:
        if k & 1:
            result = _p256_add(result, point)
        point = _p256_add(point, point)
        k >>= 1
    return result


# ---------------------------------------------------------------------------- ECDSA P-256 / SHA-256

@dataclass(frozen=True)
class P256PublicKey:
    """A P-256 public key: an affine point on the curve (the point at infinity has no affine coordinates)."""
    x: int
    y: int
    algorithm = ECDSA_P256

    def __post_init__(self):
        if not _p256_on_curve(self.x, self.y):
            raise ValueError("not a point on P-256")

    def verify(self, message: bytes, signature: bytes) -> bool:
        """True when signature is a DER ECDSA signature of SHA-256(message) by this key; False for anything else."""
        try:
            r, s = parse_der_signature(signature)
        except ValueError:
            return False
        return ecdsa_verify_digest(self, hashlib.sha256(message).digest(), r, s)


class P256PrivateKey:
    """A P-256 private key from its 32-byte big-endian scalar d, which must be in [1, n-1]."""
    algorithm = ECDSA_P256

    def __init__(self, scalar: bytes):
        if not isinstance(scalar, bytes) or len(scalar) != 32:
            raise ValueError("a P-256 private key is 32 bytes")
        self.d = int.from_bytes(scalar, "big")
        if not 1 <= self.d < P256_N:
            raise ValueError("a P-256 private key must be in [1, n-1]")
        self.public_key = P256PublicKey(*_p256_mul(self.d, P256_G))

    def sign(self, message: bytes) -> bytes:
        """The DER signature of SHA-256(message), with the RFC 6979 nonce."""
        return der_signature(*ecdsa_sign_digest(self.d, hashlib.sha256(message).digest()))


def _rfc6979_nonces(d, digest):
    """The candidate nonces of RFC 6979 section 3.2, in order, for P-256 with SHA-256 (qlen = hlen = 256)."""
    x = d.to_bytes(32, "big")  # int2octets(x)
    h = (int.from_bytes(digest, "big") % P256_N).to_bytes(32, "big")  # bits2octets(h1)
    mac = lambda key, data: hmac.new(key, data, hashlib.sha256).digest()
    K, V = b"\x00" * 32, b"\x01" * 32  # steps b and c
    K = mac(K, V + b"\x00" + x + h)  # d
    V = mac(K, V)  # e
    K = mac(K, V + b"\x01" + x + h)  # f
    V = mac(K, V)  # g
    while True:  # h
        V = mac(K, V)  # one HMAC output is already qlen bits
        k = int.from_bytes(V, "big")  # bits2int(T)
        if 1 <= k < P256_N:
            yield k
        K = mac(K, V + b"\x00")
        V = mac(K, V)


def _ecdsa_sign(d, digest):
    """(k, r, s): the nonce as well, which the RFC 6979 vectors list."""
    if len(digest) != 32:
        raise ValueError("a SHA-256 digest is 32 bytes")
    e = int.from_bytes(digest, "big")  # bits2int(H(m)): the whole digest, as qlen = hlen
    for k in _rfc6979_nonces(d, digest):
        r = _p256_mul(k, P256_G)[0] % P256_N
        s = pow(k, -1, P256_N) * (e + r * d) % P256_N
        if r and s:  # else the next nonce (RFC 6979 section 3.4)
            return k, r, s


def ecdsa_sign_digest(d: int, digest: bytes) -> tuple[int, int]:
    """(r, s) for a SHA-256 digest with the RFC 6979 nonce. s is not normalized to the low half."""
    return _ecdsa_sign(d, digest)[1:]


def ecdsa_verify_digest(public_key: P256PublicKey, digest: bytes, r: int, s: int) -> bool:
    """ECDSA verification (SEC 1 section 4.1.4).

    (r, s) and (r, n - s) both verify: kG and -kG have the same x-coordinate, and ECDSA accepts any s in [1, n-1].
    Low-S is a Bitcoin rule against transaction malleability (BIP 62, BIP 146), not part of ECDSA, DSSE, in-toto or
    Sigstore; OpenSSL, .NET and Go sign without it, so a verifier that required it would refuse valid signatures.
    """
    if not (1 <= r < P256_N and 1 <= s < P256_N) or len(digest) != 32:
        return False
    w = pow(s, -1, P256_N)
    e = int.from_bytes(digest, "big")
    point = _p256_add(_p256_mul(e * w % P256_N, P256_G), _p256_mul(r * w % P256_N, (public_key.x, public_key.y)))
    return point is not None and point[0] % P256_N == r


def der_signature(r: int, s: int) -> bytes:
    """DER SEQUENCE { INTEGER r, INTEGER s }."""
    return _der(0x30, _der_uint(r) + _der_uint(s))


def parse_der_signature(signature: bytes) -> tuple[int, int]:
    """(r, s) from a DER ECDSA signature; ValueError unless it is exactly SEQUENCE { INTEGER r, INTEGER s } in DER
    (minimal lengths, minimal non-negative integers, no trailing bytes) with r and s in [1, n-1]."""
    tag, body, end = _der_read(signature, 0)
    if tag != 0x30 or end != len(signature):
        raise ValueError("not a single DER SEQUENCE")
    r, pos = _read_uint(body, 0)
    s, pos = _read_uint(body, pos)
    if pos != len(body):
        raise ValueError("more than two INTEGERs in the SEQUENCE")
    if not (1 <= r < P256_N and 1 <= s < P256_N):
        raise ValueError("r or s is outside [1, n-1]")
    return r, s


# ---------------------------------------------------------------------------- Ed25519 (RFC 8032 section 5.1)

def _ed_add(p1, p2):
    """Section 5.1.4: complete addition in extended coordinates (X, Y, Z, T), x = X/Z, y = Y/Z, xy = T/Z."""
    X1, Y1, Z1, T1 = p1
    X2, Y2, Z2, T2 = p2
    A = (Y1 - X1) * (Y2 - X2) % ED25519_P
    B = (Y1 + X1) * (Y2 + X2) % ED25519_P
    C = T1 * 2 * ED25519_D * T2 % ED25519_P
    D = Z1 * 2 * Z2 % ED25519_P
    E, F, G, H = B - A, D - C, D + C, B + A
    return E * F % ED25519_P, G * H % ED25519_P, F * G % ED25519_P, E * H % ED25519_P


def _ed_mul(k, point):
    """k * point, by double-and-add."""
    result = _ED_ZERO
    while k:
        if k & 1:
            result = _ed_add(result, point)
        point = _ed_add(point, point)
        k >>= 1
    return result


def _ed_equal(p1, p2):
    return ((p1[0] * p2[2] - p2[0] * p1[2]) % ED25519_P == 0
            and (p1[1] * p2[2] - p2[1] * p1[2]) % ED25519_P == 0)


def _ed_point(y, sign):
    """The point with this y and this low bit of x (section 5.1.3, steps 2 to 4), or None when there is none."""
    if y >= ED25519_P:
        return None
    u, v = (y * y - 1) % ED25519_P, (ED25519_D * y * y + 1) % ED25519_P
    x = u * pow(v, 3, ED25519_P) * pow(u * pow(v, 7, ED25519_P), (ED25519_P - 5) // 8, ED25519_P) % ED25519_P
    if (v * x * x - u) % ED25519_P == 0:
        pass
    elif (v * x * x + u) % ED25519_P == 0:
        x = x * _SQRT_M1 % ED25519_P
    else:
        return None  # u/v is not a square
    if x == 0 and sign == 1:
        return None
    if (x & 1) != sign:
        x = ED25519_P - x
    return x, y, 1, x * y % ED25519_P


def _ed_decode(data):
    """Section 5.1.3: a 32-byte encoding to a point, or None when it does not decode (including y >= p)."""
    if len(data) != 32:
        return None
    y = int.from_bytes(data, "little")
    return _ed_point(y & ((1 << 255) - 1), y >> 255)


def _ed_encode(point):
    """Section 5.1.2: y little-endian, with the low bit of x in the top bit."""
    z = pow(point[2], -1, ED25519_P)
    x, y = point[0] * z % ED25519_P, point[1] * z % ED25519_P
    return (y | (x & 1) << 255).to_bytes(32, "little")


def _ed_hash(*parts):
    """SHA-512 of the concatenation as a little-endian integer, mod L."""
    return int.from_bytes(hashlib.sha512(b"".join(parts)).digest(), "little") % ED25519_L


def _ed_expand(seed):
    """Section 5.1.5: the secret scalar s (the pruned lower half of SHA-512(seed)) and the prefix (the upper half)."""
    h = hashlib.sha512(seed).digest()
    s = int.from_bytes(h[:32], "little") & ((1 << 254) - 8) | (1 << 254)
    return s, h[32:]


ED25519_B = _ed_point(4 * pow(5, -1, ED25519_P) % ED25519_P, 0)  # y = 4/5, x even


def ed25519_public(seed: bytes) -> bytes:
    """Section 5.1.5: the 32-byte public key of a 32-byte seed."""
    return _ed_encode(_ed_mul(_ed_expand(seed)[0], ED25519_B))


def ed25519_sign(seed: bytes, message: bytes) -> bytes:
    """Section 5.1.6: R || S, 64 bytes."""
    s, prefix = _ed_expand(seed)
    A = _ed_encode(_ed_mul(s, ED25519_B))
    r = _ed_hash(prefix, message)
    R = _ed_encode(_ed_mul(r, ED25519_B))
    S = (r + _ed_hash(R, A, message) * s) % ED25519_L
    return R + S.to_bytes(32, "little")


def ed25519_verify(public: bytes, message: bytes, signature: bytes) -> bool:
    """Section 5.1.7: False when A or R does not decode or S >= L, else whether [S]B = R + [k]A.

    This is the cofactorless equation the RFC allows in step 3 ('sufficient, but not required'), as in the RFC's
    section 6 reference code, OpenSSL and Go. The cofactored [8][S]B = [8]R + [8][k]A differs only for signatures
    built with small-order components, which no honest signer produces.
    """
    if len(public) != 32 or len(signature) != 64:
        return False
    A, R = _ed_decode(public), _ed_decode(signature[:32])
    S = int.from_bytes(signature[32:], "little")
    if A is None or R is None or S >= ED25519_L:
        return False
    k = _ed_hash(signature[:32], public, message)
    return _ed_equal(_ed_mul(S, ED25519_B), _ed_add(R, _ed_mul(k, A)))


@dataclass(frozen=True)
class Ed25519PublicKey:
    """An Ed25519 public key: its 32-byte encoding, which must decode to a curve point."""
    key: bytes
    algorithm = ED25519

    def __post_init__(self):
        if not isinstance(self.key, bytes) or _ed_decode(self.key) is None:
            raise ValueError("not an Ed25519 public key (32 bytes that decode to a curve point)")

    def verify(self, message: bytes, signature: bytes) -> bool:
        return ed25519_verify(self.key, message, signature)


class Ed25519PrivateKey:
    """An Ed25519 private key from its 32-byte seed (RFC 8032's 'secret key')."""
    algorithm = ED25519

    def __init__(self, seed: bytes):
        if not isinstance(seed, bytes) or len(seed) != 32:
            raise ValueError("an Ed25519 seed is 32 bytes")
        self.seed = seed
        self.public_key = Ed25519PublicKey(ed25519_public(seed))

    def sign(self, message: bytes) -> bytes:
        return ed25519_sign(self.seed, message)


PublicKey = P256PublicKey | Ed25519PublicKey
PrivateKey = P256PrivateKey | Ed25519PrivateKey


# ---------------------------------------------------------------------------- DER, SubjectPublicKeyInfo, PEM

def _der(tag, value):
    n = len(value)
    if n < 0x80:
        return bytes([tag, n]) + value
    size = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([tag, 0x80 | len(size)]) + size + value


def _der_read(data, pos):
    """(tag, value, position after it) of the DER element at pos; ValueError unless the length is definite, minimal
    and inside data."""
    if pos + 2 > len(data):
        raise ValueError("truncated DER")
    tag, first = data[pos], data[pos + 1]
    pos += 2
    if first < 0x80:
        length = first
    elif first == 0x80 or first > 0x84:
        raise ValueError("an indefinite or oversized DER length")
    else:
        size = data[pos:pos + (first & 0x7F)]
        if len(size) != first & 0x7F:
            raise ValueError("truncated DER")
        length = int.from_bytes(size, "big")
        if length < 0x80 or size[0] == 0:
            raise ValueError("a non-minimal DER length")
        pos += len(size)
    if pos + length > len(data):
        raise ValueError("truncated DER")
    return tag, data[pos:pos + length], pos + length


def _der_uint(v):
    """A non-negative INTEGER: big-endian, with a leading 0x00 only when the top bit would be set."""
    return _der(0x02, v.to_bytes(v.bit_length() // 8 + 1, "big"))


def _read_uint(data, pos):
    tag, value, pos = _der_read(data, pos)
    if tag != 0x02 or not value:
        raise ValueError("not a DER INTEGER")
    if value[0] & 0x80:
        raise ValueError("a negative INTEGER")
    if len(value) > 1 and value[0] == 0 and not value[1] & 0x80:
        raise ValueError("a non-minimal INTEGER")
    return int.from_bytes(value, "big"), pos


def _oid(dotted):
    """DER OBJECT IDENTIFIER from its dotted form."""
    arcs = [int(a) for a in dotted.split(".")]
    body = bytearray([40 * arcs[0] + arcs[1]])
    for arc in arcs[2:]:
        chunk = [arc & 0x7F]
        while arc > 0x7F:
            arc >>= 7
            chunk.append(0x80 | arc & 0x7F)
        body += bytes(reversed(chunk))
    return _der(0x06, bytes(body))


ID_EC_PUBLIC_KEY = _oid("1.2.840.10045.2.1")  # RFC 5480 section 2.1.1
PRIME256V1 = _oid("1.2.840.10045.3.1.7")  # secp256r1, RFC 5480 section 2.1.1.1
ID_ED25519 = _oid("1.3.101.112")  # RFC 8410 section 3; its parameters are absent
_PEM_BEGIN, _PEM_END = "-----BEGIN PUBLIC KEY-----", "-----END PUBLIC KEY-----"


def spki_der(public_key: PublicKey) -> bytes:
    """SubjectPublicKeyInfo DER: SEQUENCE { AlgorithmIdentifier, BIT STRING key }."""
    if isinstance(public_key, P256PublicKey):
        algorithm = _der(0x30, ID_EC_PUBLIC_KEY + PRIME256V1)
        key = b"\x04" + public_key.x.to_bytes(32, "big") + public_key.y.to_bytes(32, "big")
    elif isinstance(public_key, Ed25519PublicKey):
        algorithm, key = _der(0x30, ID_ED25519), public_key.key
    else:
        raise TypeError("not a P256PublicKey or an Ed25519PublicKey")
    return _der(0x30, algorithm + _der(0x03, b"\x00" + key))


def load_spki_der(der: bytes) -> PublicKey:
    """The public key of a SubjectPublicKeyInfo: P-256 (an uncompressed point on the curve) or Ed25519 (a point that
    decodes). ValueError for anything else, including a DER encoding other than the one spki_der writes."""
    tag, body, end = _der_read(der, 0)
    if tag != 0x30 or end != len(der):
        raise ValueError("not a SubjectPublicKeyInfo (one DER SEQUENCE)")
    tag, algorithm, pos = _der_read(body, 0)
    bits_tag, bits, end = _der_read(body, pos)
    if tag != 0x30 or bits_tag != 0x03 or end != len(body):
        raise ValueError("not a SubjectPublicKeyInfo (SEQUENCE { AlgorithmIdentifier, BIT STRING })")
    if not bits or bits[0] != 0:
        raise ValueError("the key BIT STRING has unused bits")
    point = bits[1:]
    if algorithm == ID_EC_PUBLIC_KEY + PRIME256V1:
        if len(point) != 65 or point[0] != 0x04:
            raise ValueError("a P-256 key must be an uncompressed point 0x04 || X || Y")
        key = P256PublicKey(int.from_bytes(point[1:33], "big"), int.from_bytes(point[33:], "big"))
    elif algorithm == ID_ED25519:
        key = Ed25519PublicKey(point)
    elif algorithm.startswith(ID_EC_PUBLIC_KEY):
        raise ValueError("an EC key on a curve other than P-256 (prime256v1)")
    else:
        raise ValueError("neither an id-ecPublicKey P-256 key nor an Ed25519 key")
    if spki_der(key) != der:
        raise ValueError("not the DER encoding of the key")
    return key


def spki_pem(public_key: PublicKey) -> str:
    """The SubjectPublicKeyInfo as a PEM PUBLIC KEY block (RFC 7468): 64 characters a line, LF line ends."""
    text = base64.b64encode(spki_der(public_key)).decode("ascii")
    return "\n".join([_PEM_BEGIN] + [text[i:i + 64] for i in range(0, len(text), 64)] + [_PEM_END]) + "\n"


def load_spki_pem(text: str) -> PublicKey:
    """The public key of a PEM PUBLIC KEY block. Spaces around lines and CR LF line ends are tolerated."""
    lines = [line.strip() for line in text.strip().splitlines()]
    if len(lines) < 3 or lines[0] != _PEM_BEGIN or lines[-1] != _PEM_END:
        raise ValueError("not a PEM PUBLIC KEY block")
    body = "".join(lines[1:-1])
    if not _B64.fullmatch(body) or b64encode(base64.b64decode(body)) != body:
        raise ValueError("the PEM body is not canonical standard base64")
    return load_spki_der(base64.b64decode(body))


def keyid(public_key: PublicKey) -> str:
    """'sha256:' + the lowercase hex SHA-256 of the SubjectPublicKeyInfo DER."""
    return "sha256:" + hashlib.sha256(spki_der(public_key)).hexdigest()


# ---------------------------------------------------------------------------- DSSE v1

_B64 = re.compile(r"(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?")
_B64URL = re.compile(r"(?:[A-Za-z0-9_-]{4})*(?:[A-Za-z0-9_-]{2}(?:==)?|[A-Za-z0-9_-]{3}=?)?")


class EnvelopeError(ValueError):
    """The envelope is not a DSSE JSON envelope."""


class Envelope(NamedTuple):
    payload_type: str
    payload: bytes
    signatures: list  # (keyid, signature bytes); keyid '' when unset


class SignatureResult(NamedTuple):
    keyid: str  # the key the signature was checked against (see verify_envelope)
    trusted: bool  # keyid names a trusted key
    valid: bool  # the signature verifies with that key
    algorithm: str | None  # the trusted key's algorithm; None when no trusted key was found


def b64encode(data: bytes) -> str:
    """Standard base64 with padding, as the envelope is written."""
    return base64.b64encode(data).decode("ascii")


def b64decode(text: str) -> bytes:
    """SIG-1: the standard or the URL-safe alphabet (DSSE accepts both), padded or not; padding, when present, is
    complete. Only the canonical encoding of its bytes: no whitespace, no set unused bits, one alphabet."""
    if not isinstance(text, str):
        raise ValueError("not a string")
    core = text.rstrip("=")
    if len(core) % 4 == 1 or (text != core and len(text) != len(core) + (-len(core) % 4)):
        raise ValueError("not base64 (a length no encoding has, or incomplete padding)")
    if re.fullmatch(r"[A-Za-z0-9+/]*", core):
        standard = core
    elif re.fullmatch(r"[A-Za-z0-9_-]*", core):
        standard = core.translate(str.maketrans("-_", "+/"))
    else:
        raise ValueError("not base64, or a mix of the two alphabets")
    data = base64.b64decode(standard + "=" * (-len(standard) % 4))
    if base64.b64encode(data).decode("ascii").rstrip("=") != standard:
        raise ValueError("not the canonical base64 of its bytes (unused bits are set)")
    return data


def pae(payload_type: str, payload: bytes) -> bytes:
    """DSSE's pre-authentication encoding: "DSSEv1" SP LEN(type) SP type SP LEN(body) SP body, where type is the
    UTF-8 of payload_type and LEN the decimal byte length, no leading zeros."""
    kind = payload_type.encode("utf-8")
    return b"DSSEv1 %d %b %d %b" % (len(kind), kind, len(payload), bytes(payload))


def build_envelope(payload_type: str, payload: bytes, signatures) -> dict:
    """The JSON envelope for (keyid, signature bytes) pairs; an empty or None keyid is left out."""
    return {"payload": b64encode(payload), "payloadType": payload_type,
            "signatures": [({"keyid": kid} if kid else {}) | {"sig": b64encode(sig)} for kid, sig in signatures]}


def sign_envelope(payload_type: str, payload: bytes, private_keys) -> dict:
    """The JSON envelope with one signature over the PAE per key, in order, each with keyid(its public key)."""
    message = pae(payload_type, payload)
    return build_envelope(payload_type, payload, [(keyid(k.public_key), k.sign(message)) for k in private_keys])


def _text(obj, name, where):
    if name not in obj:
        raise EnvelopeError(f"{where} has no {name}")
    if not isinstance(obj[name], str):
        raise EnvelopeError(f"{where}: {name} is not a string")
    return obj[name]


def _decoded(obj, name, where):
    text = _text(obj, name, where)
    try:
        return b64decode(text)
    except ValueError as error:
        raise EnvelopeError(f"{where}: {name} is {error}") from None


def parse_envelope(envelope: dict) -> Envelope:
    """The decoded envelope, or EnvelopeError. As DSSE's envelope.md says: payload, payloadType, signatures and sig
    are required (payload and sig possibly empty), an unset keyid is the same as an empty one, and unknown fields are
    ignored. AEF adds one rule (spec 04, SIG-1): an envelope with no signature signs nothing, so it is refused."""
    if not isinstance(envelope, dict):
        raise EnvelopeError("the envelope is not a JSON object")
    payload_type = _text(envelope, "payloadType", "the envelope")
    try:
        payload_type.encode("utf-8")
    except UnicodeEncodeError:
        raise EnvelopeError("the envelope: payloadType is not valid Unicode") from None
    payload = _decoded(envelope, "payload", "the envelope")
    if not isinstance(envelope.get("signatures"), list):
        raise EnvelopeError("the envelope: signatures is missing or not an array")
    if not envelope["signatures"]:
        raise EnvelopeError("the envelope has no signature")
    signatures = []
    for i, entry in enumerate(envelope["signatures"], start=1):
        where = f"signature {i}"
        if not isinstance(entry, dict):
            raise EnvelopeError(f"{where} is not a JSON object")
        kid = entry.get("keyid")
        if kid is not None and not isinstance(kid, str):
            raise EnvelopeError(f"{where}: keyid is not a string")
        signatures.append((kid or "", _decoded(entry, "sig", where)))
    return Envelope(payload_type, payload, signatures)


def payload_bytes(envelope: dict) -> bytes:
    """The payload, decoded exactly as verify_envelope decodes it. Use the same parsed dict for both: DSSE forbids
    re-parsing the envelope after verification to get the payload."""
    return parse_envelope(envelope).payload


def verify_envelope(envelope: dict, trusted_keys: dict[str, PublicKey]) -> list[SignatureResult]:
    """One result per signature, in order; EnvelopeError for a malformed envelope.

    A signature with a keyid is checked against the trusted key of that id only (trusted_keys maps ids to public
    keys). One without a keyid is checked against every trusted key in turn, and its result names the first that
    verifies it ('' when none does): DSSE's keyid is an optional hint, never a requirement.
    """
    parsed = parse_envelope(envelope)
    message = pae(parsed.payload_type, parsed.payload)
    results = []
    for kid, sig in parsed.signatures:
        if not kid:
            kid = next((k for k, key in trusted_keys.items() if key.verify(message, sig)), "")
        key = trusted_keys.get(kid) if kid else None
        results.append(SignatureResult(kid, key is not None, key is not None and key.verify(message, sig),
                                       key.algorithm if key is not None else None))
    return results


# ---------------------------------------------------------------------------- self-test

# RFC 6979 appendix A.2.5 (ECDSA, 256 bits, prime field): x, then U = xG, then per message with SHA-256: k, r, s.
_RFC6979_X = 0xC9AFA9D845BA75166B5C215767B1D6934E50C3DB36E89B127B8A622B120F6721
_RFC6979_U = (0x60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6,
              0x7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299)
_RFC6979_SHA256 = [
    (b"sample",
     0xA6E3C57DD01ABE90086538398355DD4C3B17AA873382B0F24D6129493D8AAD60,
     0xEFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716,
     0xF7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8),
    (b"test",
     0xD16B6AE827F17175E040871A1C7EC3500192C4C92677336EC2537ACAEE0008E0,
     0xF1ABB023518351CD71D881567B1EA663ED3EFCF6C5132B354F28D3B0B7D38367,
     0x019F4113742A2B14BD25926B49C649155F267E60D3814B4C0CC84250E46F0083),
]
# The SubjectPublicKeyInfo of that key as OpenSSL 3.5 writes it (`openssl ec -pubout` from the private key): an
# external check of the encoding (id-ecPublicKey, prime256v1, BIT STRING 0x04 || X || Y).
_RFC6979_PEM = """-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYP7UuiVanTHJYet0xjVtaMBJuJI7
Yfps5mliLmDyn7Z5A/4QCLi8maQa6elWKLxk8vGyDC1+n1F3o8KU1EYimQ==
-----END PUBLIC KEY-----
"""

# RFC 8032 section 7.1: name, secret key, public key, message, signature (hex, as the RFC prints them).
_RFC8032_ED25519 = [
    ("TEST 1",
     "9d61b19deffd5a60ba844af492ec2cc4 4449c5697b326919703bac031cae7f60",
     "d75a980182b10ab7d54bfed3c964073a 0ee172f3daa62325af021a68f707511a",
     "",
     "e5564300c360ac729086e2cc806e828a 84877f1eb8e5d974d873e06522490155"
     "5fb8821590a33bacc61e39701cf9b46b d25bf5f0595bbe24655141438e7a100b"),
    ("TEST 2",
     "4ccd089b28ff96da9db6c346ec114e0f 5b8a319f35aba624da8cf6ed4fb8a6fb",
     "3d4017c3e843895a92b70aa74d1b7ebc 9c982ccf2ec4968cc0cd55f12af4660c",
     "72",
     "92a009a9f0d4cab8720e820b5f642540 a2b27b5416503f8fb3762223ebdb69da"
     "085ac1e43e15996e458f3613d0f11d8c 387b2eaeb4302aeeb00d291612bb0c00"),
    ("TEST 3",
     "c5aa8df43f9f837bedb7442f31dcb7b1 66d38535076f094b85ce3a2e0b4458f7",
     "fc51cd8e6218a1a38da47ed00230f058 0816ed13ba3303ac5deb911548908025",
     "af82",
     "6291d657deec24024827e69c3abe01a3 0ce548a284743a445e3680d7db5ac3ac"
     "18ff9b538d16f290ae67f760984dc659 4a7c15e9716ed28dc027beceea1ec40a"),
    ("TEST SHA(abc)",
     "833fe62409237b9d62ec77587520911e 9a759cec1d19755b7da901b96dca3d42",
     "ec172b93ad5e563bf4932c70e1245034 c35467ef2efd4d64ebf819683467e2bf",
     "ddaf35a193617abacc417349ae204131 12e6fa4e89a97ea20a9eeee64b55d39a"
     "2192992a274fc1a836ba3c23a3feebbd 454d4423643ce80e2a9ac94fa54ca49f",
     "dc2a4459e7369633a52b1bf277839a00 201009a3efbf3ecb69bea2186c26b589"
     "09351fc9ac90b3ecfdfbc7c66431e030 3dca179c138ac17ad9bef1177331a704"),
]

# RFC 8032 table 1: d and B = (x, y) of edwards25519.
_RFC8032_D = 37095705934669439343138083508754565189542113879843219016388785533085940283555
_RFC8032_B = (15112221349535400772501151409588531511454012693041857206046113283949847762202,
              46316835694926478169428394003475163141307993866256225615783033603165251855960)

# RFC 8410 section 10.1 (an Ed25519 public key) and section 10.3 (the private key whose public key it is).
_RFC8410_PEM = """-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEAGb9ECWmEzf6FQbrBZ9w7lshQhqowtrbLDFw4rXAxZuE=
-----END PUBLIC KEY-----
"""
_RFC8410_SEED = "D4 EE 72 DB F9 13 58 4A D5 B6 D8 F1 F7 69 F8 AD 3A FE 7C 28 CB F1 D4 FB E0 97 A8 8F 44 75 58 42"

# DSSE protocol.md, 'Test Vectors': ECDSA P-256 / SHA-256 with deterministic-rfc6979, the signature as raw r || s.
_DSSE_TYPE, _DSSE_BODY = "http://example.com/HelloWorld", b"hello world"
_DSSE_PAE = b"DSSEv1 29 http://example.com/HelloWorld 11 hello world"
_DSSE_X = 46950820868899156662930047687818585632848591499744589407958293238635476079160
_DSSE_Y = 5640078356564379163099075877009565129882514886557779369047442380624545832820
_DSSE_D = 97358161215184420915383655311931858321456579547487070936769975997791359926199
_DSSE_SIG = "A3JqsQGtVsJ2O2xqrI5IcnXip5GToJ3F+FnZ+O88SjtR6rDAajabZKciJTfUiHqJPcIAriEGAHTVeCUjW2JIZA=="


def self_test():
    """Prints pass or FAIL per check; returns 1 when any check fails."""
    outcomes = []

    def check(name, test):
        try:
            ok = test() is True
        except Exception as error:  # a check that raises fails, and the others still run
            ok, name = False, f"{name} (raised {type(error).__name__}: {error})"
        outcomes.append(ok)
        print(f"{'pass' if ok else 'FAIL'}  {name}")

    # The curves.
    check("P-256: p = 2^256 - 2^224 + 2^192 + 2^96 - 1, G on the curve, n*G = infinity",
          lambda: P256_P == 2**256 - 2**224 + 2**192 + 2**96 - 1 and _p256_on_curve(*P256_G)
          and _p256_mul(P256_N, P256_G) is None)
    check("Ed25519: d and B as in RFC 8032 table 1, L*B = the neutral point",
          lambda: ED25519_D == _RFC8032_D
          and _ed_equal(ED25519_B, (*_RFC8032_B, 1, _RFC8032_B[0] * _RFC8032_B[1] % ED25519_P))
          and _ed_equal(_ed_mul(ED25519_L, ED25519_B), _ED_ZERO))

    # RFC 6979 A.2.5.
    rfc6979 = P256PrivateKey(_RFC6979_X.to_bytes(32, "big"))
    check("RFC 6979 A.2.5: public key U = xG",
          lambda: (rfc6979.public_key.x, rfc6979.public_key.y) == _RFC6979_U)
    for message, k, r, s in _RFC6979_SHA256:
        digest = hashlib.sha256(message).digest()
        check(f'RFC 6979 A.2.5: SHA-256, message "{message.decode()}": k, r and s',
              lambda: _ecdsa_sign(_RFC6979_X, digest) == (k, r, s))
        check(f'RFC 6979 A.2.5: SHA-256, message "{message.decode()}": the DER signature verifies',
              lambda: rfc6979.public_key.verify(message, rfc6979.sign(message)))
    check('RFC 6979 A.2.5: the "sample" s is above n/2, so signing does not normalize to low S',
          lambda: _RFC6979_SHA256[0][3] > P256_N // 2)
    # bits2octets reduces the digest mod n (RFC 6979 section 2.3.4), and ECDSA uses e mod n: a digest >= n (no
    # vector has one; about 2^-32 of SHA-256 outputs are) must sign exactly as the digest minus n.
    reduced = (2**256 - 1 - P256_N).to_bytes(32, "big")  # 0xFF..FF minus n
    check("RFC 6979 2.3.4: a digest >= n signs as the digest - n",
          lambda: _ecdsa_sign(_RFC6979_X, b"\xff" * 32) == _ecdsa_sign(_RFC6979_X, reduced))

    # RFC 8032 section 7.1.
    for name, secret, public, message, signature in _RFC8032_ED25519:
        secret, public, message, signature = (bytes.fromhex(v) for v in (secret, public, message, signature))
        check(f"RFC 8032 7.1 {name}: public key", lambda: ed25519_public(secret) == public)
        check(f"RFC 8032 7.1 {name}: signature", lambda: ed25519_sign(secret, message) == signature)
        check(f"RFC 8032 7.1 {name}: verifies", lambda: ed25519_verify(public, message, signature))

    # RFC 8410 section 10.
    check("RFC 8410 10.1: the example PEM loads as Ed25519 and is written back byte for byte",
          lambda: spki_pem(load_spki_pem(_RFC8410_PEM)) == _RFC8410_PEM)
    check("RFC 8410 10.3: the example private key's public key is the 10.1 key",
          lambda: Ed25519PrivateKey(bytes.fromhex(_RFC8410_SEED)).public_key == load_spki_pem(_RFC8410_PEM))

    # DSSE.
    check("DSSE protocol example: PAE", lambda: pae(_DSSE_TYPE, _DSSE_BODY) == _DSSE_PAE)
    # Hand-computed: 'ë' is U+00EB, UTF-8 C3 AB, so the type "tëst" is 4 characters and 5 bytes, and the body "ë" is
    # 2 bytes: "DSSEv1" SP "5" SP "t\xc3\xabst" SP "2" SP "\xc3\xab".
    check("DSSE PAE: lengths count UTF-8 bytes",
          lambda: pae("tëst", "ë".encode()) == b"DSSEv1 5 t\xc3\xabst 2 \xc3\xab")
    # Hand-computed: both lengths are 0 and both strings empty: "DSSEv1" SP "0" SP "" SP "0" SP "".
    check("DSSE PAE: empty type and body", lambda: pae("", b"") == b"DSSEv1 0  0 ")
    dsse = P256PrivateKey(_DSSE_D.to_bytes(32, "big"))
    check("DSSE protocol example: the public key (X, Y) of d",
          lambda: (dsse.public_key.x, dsse.public_key.y) == (_DSSE_X, _DSSE_Y))
    raw = base64.b64decode(_DSSE_SIG)
    published = (int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))
    check("DSSE protocol example: RFC 6979 over the PAE gives the published r || s",
          lambda: ecdsa_sign_digest(_DSSE_D, hashlib.sha256(_DSSE_PAE).digest()) == published)
    example = {"payload": "aGVsbG8gd29ybGQ=", "payloadType": _DSSE_TYPE,
               "signatures": [{"sig": b64encode(der_signature(*published))}]}
    check("DSSE protocol example, its signature as DER: verifies without a keyid against the trusted key",
          lambda: verify_envelope(example, {"dsse-example": dsse.public_key})
          == [SignatureResult("dsse-example", True, True, ECDSA_P256)])

    # Keys, SubjectPublicKeyInfo, key ids.
    ed = Ed25519PrivateKey(bytes.fromhex(_RFC8032_ED25519[0][1]))
    for key in (rfc6979.public_key, ed.public_key):
        check(f"{key.algorithm}: SPKI DER and PEM load back to the same key",
              lambda: load_spki_der(spki_der(key)) == key and load_spki_pem(spki_pem(key)) == key)
        check(f"{key.algorithm}: keyid is 'sha256:' + the hex SHA-256 of the SPKI DER",
              lambda: keyid(key) == "sha256:" + hashlib.sha256(spki_der(key)).hexdigest()
              and re.fullmatch(r"sha256:[0-9a-f]{64}", keyid(key)) is not None)
    check("P-256 SPKI of the RFC 6979 key: as OpenSSL writes it",
          lambda: spki_pem(rfc6979.public_key) == _RFC6979_PEM)
    p256_der = spki_der(rfc6979.public_key)
    x_bytes = rfc6979.public_key.x.to_bytes(32, "big")
    spki = lambda algorithm, key: _der(0x30, _der(0x30, algorithm) + _der(0x03, b"\x00" + key))
    refused = {  # name: (DER, a part of the expected error, so that each is refused for its own reason)
        "a point off the curve": (p256_der[:-1] + bytes([p256_der[-1] ^ 1]), "not a point on P-256"),
        "the point at infinity (SEC 1: the single byte 00)": (spki(ID_EC_PUBLIC_KEY + PRIME256V1, b"\x00"),
                                                              "uncompressed"),
        "a compressed point": (spki(ID_EC_PUBLIC_KEY + PRIME256V1, bytes([2 + rfc6979.public_key.y % 2]) + x_bytes),
                               "uncompressed"),
        "another curve (secp384r1)": (spki(ID_EC_PUBLIC_KEY + _oid("1.3.132.0.34"), p256_der[-65:]),
                                      "other than P-256"),
        "a trailing byte": (p256_der + b"\x00", "one DER SEQUENCE"),
        "Ed25519 with NULL parameters": (spki(ID_ED25519 + b"\x05\x00", ed.public_key.key), "nor an Ed25519 key"),
        "an Ed25519 key with y >= p": (spki(ID_ED25519, (ED25519_P + 1).to_bytes(32, "little")), "decode"),
    }
    for name, (der, reason) in refused.items():
        check(f"load_spki_der refuses {name}", lambda: _raises(ValueError, load_spki_der, der, reason=reason))

    # ECDSA round trips.
    p256, other_p256 = rfc6979.public_key, dsse
    message = b"AEF self-test message"
    good = rfc6979.sign(message)
    r, s = parse_der_signature(good)
    check("ECDSA: sign, then verify", lambda: p256.verify(message, good))
    check("ECDSA: a flipped message byte fails", lambda: not p256.verify(_flip(message), good))
    check("ECDSA: a signature by another key fails", lambda: not p256.verify(message, other_p256.sign(message)))
    # (r, s) and (r, n - s) are both valid, and exactly one of them has s above n/2 (n is odd). Both verify, because
    # ECDSA accepts any s in [1, n-1] (see ecdsa_verify_digest); RFC 6979's own "sample" signature is high-S.
    check("ECDSA: the high-S form (r, max(s, n - s)) of a signature verifies",
          lambda: p256.verify(message, der_signature(r, max(s, P256_N - s))))
    check("ECDSA: the low-S form (r, min(s, n - s)) of a signature verifies",
          lambda: p256.verify(message, der_signature(r, min(s, P256_N - s))))
    # Each malformed form below is RFC 6979's "sample" signature (valid; r and s both have the top bit set) encoded
    # wrongly, so a lax parser would accept most of them: only the strict parse refuses them.
    _, r, s = _ecdsa_sign(_RFC6979_X, hashlib.sha256(b"sample").digest())
    good = der_signature(r, s)
    malformed = {
        "empty": b"",
        "raw r || s, not DER": r.to_bytes(32, "big") + s.to_bytes(32, "big"),
        "a trailing byte": good + b"\x00",
        "truncated": good[:-1],
        "a SET, not a SEQUENCE": b"\x31" + good[1:],
        "a long-form length for a short SEQUENCE": b"\x30\x81" + good[1:],
        "a non-minimal INTEGER (00 00 ..)": _der(0x30, _der(0x02, b"\x00" + _der_uint(r)[2:]) + _der_uint(s)),
        "a negative INTEGER (r without its 00)": _der(0x30, _der(0x02, r.to_bytes(32, "big")) + _der_uint(s)),
        "a third INTEGER": _der(0x30, _der_uint(r) + _der_uint(s) + _der_uint(1)),
        "s + n (the same s mod n)": der_signature(r, s + P256_N),
        "r = 0": der_signature(0, s),
        "s = n": der_signature(r, P256_N),
    }
    check('ECDSA: the "sample" signature these are made from verifies', lambda: p256.verify(b"sample", good))
    for name, signature in malformed.items():
        check(f"ECDSA: malformed DER fails without an exception: {name}",
              lambda: p256.verify(b"sample", signature) is False)

    # Ed25519 round trips.
    other_ed = Ed25519PrivateKey(bytes.fromhex(_RFC8032_ED25519[1][1]))
    signed = ed.sign(message)
    big_s = int.from_bytes(signed[32:], "little") + ED25519_L
    check("Ed25519: sign, then verify", lambda: ed.public_key.verify(message, signed))
    check("Ed25519: a flipped message byte fails", lambda: not ed.public_key.verify(_flip(message), signed))
    check("Ed25519: a signature by another key fails",
          lambda: not ed.public_key.verify(message, other_ed.sign(message)))
    check("Ed25519: S + L (S >= L, same point equation) fails",
          lambda: big_s < 2**256 and not ed.public_key.verify(message, signed[:32] + big_s.to_bytes(32, "little")))
    check("Ed25519: an R that does not decode (y >= p) fails",
          lambda: not ed.public_key.verify(message, (ED25519_P + 1).to_bytes(32, "little") + signed[32:]))
    check("Ed25519: a public key that does not decode (y >= p) is refused",
          lambda: _raises(ValueError, Ed25519PublicKey, (ED25519_P + 1).to_bytes(32, "little")))
    # y = 1 gives x = 0 (the neutral point), whose sign bit must be 0: with it set, decoding fails (section 5.1.3).
    check("Ed25519: a public key with x = 0 and the sign bit set is refused",
          lambda: _raises(ValueError, Ed25519PublicKey, (1 | 1 << 255).to_bytes(32, "little")))

    # Envelopes.
    in_toto, p256_id, ed_id = "application/vnd.in-toto+json", keyid(p256), keyid(ed.public_key)
    trusted = {p256_id: p256, ed_id: ed.public_key}
    payload = b'{"_type":"https://in-toto.io/Statement/v1"}'
    envelope = sign_envelope(in_toto, payload, [rfc6979, ed])
    check("verify_envelope: both signatures, trusted and valid, in order",
          lambda: verify_envelope(envelope, trusted)
          == [SignatureResult(p256_id, True, True, ECDSA_P256), SignatureResult(ed_id, True, True, ED25519)])
    check("build_envelope: standard base64 with padding, read back by payload_bytes",
          lambda: envelope["payload"] == base64.b64encode(payload).decode() and payload_bytes(envelope) == payload)
    tampered = dict(envelope, payload=b64encode(_flip(payload)))
    check("verify_envelope: a flipped payload byte: trusted, not valid",
          lambda: [(x.trusted, x.valid) for x in verify_envelope(tampered, trusted)] == [(True, False)] * 2)
    check("verify_envelope: a key that is not trusted: not trusted, not valid, no algorithm",
          lambda: verify_envelope(envelope, {ed_id: ed.public_key})[0] == SignatureResult(p256_id, False, False, None))
    impostor = build_envelope(in_toto, payload, [(p256_id, other_p256.sign(pae(in_toto, payload)))])
    check("verify_envelope: another key's signature under a trusted keyid: trusted, not valid",
          lambda: verify_envelope(impostor, trusted) == [SignatureResult(p256_id, True, False, ECDSA_P256)])
    urlsafe = dict(envelope, payload=base64.urlsafe_b64encode(payload).decode().rstrip("="))
    check("parse_envelope: URL-safe base64 without padding is accepted (DSSE requires it)",
          lambda: payload_bytes(urlsafe) == payload)
    unpadded = dict(envelope, payload=base64.b64encode(payload).decode().rstrip("="))
    check("parse_envelope: standard base64 without padding is accepted (SIG-1)",
          lambda: payload_bytes(unpadded) == payload)
    check("b64decode: incomplete padding is refused (SIG-1)", lambda: _raises(ValueError, b64decode, "QQ="))
    check("b64decode: complete padding in either alphabet", lambda: b64decode("QQ==") == b64decode("QQ") == b"A")
    minimal = {"payload": "", "payloadType": "t", "signatures": [{"keyid": None, "sig": ""}], "extra": 1}
    check("parse_envelope: unknown fields are ignored and a null keyid is unset",
          lambda: parse_envelope(minimal) == Envelope("t", b"", [("", b"")]))
    broken = {
        "not a JSON object": [envelope],
        "no payloadType": {k: v for k, v in envelope.items() if k != "payloadType"},
        "a payloadType that is not a string": dict(envelope, payloadType=7),
        "a payloadType with a lone surrogate": dict(envelope, payloadType="\ud800"),
        "no payload": {k: v for k, v in envelope.items() if k != "payload"},
        "a payload that is not base64": dict(envelope, payload="aGVs bG8="),
        "a payload with set unused bits": dict(envelope, payload="aGVsbG9="),
        "no signatures": {k: v for k, v in envelope.items() if k != "signatures"},
        "signatures that is not an array": dict(envelope, signatures={}),
        "an empty signatures array": dict(envelope, signatures=[]),
        "a signature that is not an object": dict(envelope, signatures=["AAAA"]),
        "a signature without sig": dict(envelope, signatures=[{"keyid": "k"}]),
        "a keyid that is not a string": dict(envelope, signatures=[{"keyid": 1, "sig": "AAAA"}]),
        "a sig mixing the two base64 alphabets": dict(envelope, signatures=[{"sig": "ab+_"}]),
    }
    for name, value in broken.items():
        check(f"verify_envelope raises EnvelopeError: {name}",
              lambda: _raises(EnvelopeError, verify_envelope, value, trusted))

    print(f"{outcomes.count(True)} of {len(outcomes)} checks pass")
    return 0 if all(outcomes) else 1


def _flip(data):
    return bytes([data[0] ^ 0x01]) + data[1:]


def _raises(error, function, *args, reason=""):
    """True when function(*args) raises error with reason in its message; any other exception propagates (and fails
    the check)."""
    try:
        function(*args)
    except error as raised:
        return reason in str(raised)
    return False


if __name__ == "__main__":
    sys.exit(self_test() if "--self-test" in sys.argv else 0)
