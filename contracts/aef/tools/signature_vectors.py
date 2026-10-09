#!/usr/bin/env python3
"""Builds conformance/signature-vectors/: DSSE envelopes over AEF files, trust policies, and the result of each (spec 04,
§4.4).

The keys in signature-vectors/keys/ are TEST KEYS, derived from public strings: anyone can sign with them. Never put
them in a real trust policy.

This script implements only what it writes: key ids ([SIG-3]) and signing. Every expected result is written by hand.

Usage: python contracts/aef/tools/signature_vectors.py   (after build_conformance.py)
"""
import base64
import hashlib
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import aef_crypto as C  # noqa: E402
from build_conformance import COMPLETED_ID, overlay_batches, read_json, sealed_files  # noqa: E402
from build_conformance import seal as seal_run, small_run  # noqa: E402

CONF = Path(__file__).resolve().parents[1] / "1" / "conformance"
OUT = CONF / "signature-vectors"
INTOTO = "application/vnd.in-toto+json"
CHECKPOINT = "application/vnd.agenteval.aef.checkpoint+json"


def test_scalar(label):
    d = hashlib.sha256(f"AEF 1.0 TEST KEY {label}: never trust".encode()).digest()
    assert 1 <= int.from_bytes(d, "big") < C.P256_N
    return d


KA = C.P256PrivateKey(test_scalar("ecdsa-a"))
KB = C.P256PrivateKey(test_scalar("ecdsa-b"))
ED = C.Ed25519PrivateKey(test_scalar("ed25519-a"))
ID = {name: C.keyid(k.public_key) for name, k in (("ecdsa-a", KA), ("ecdsa-b", KB), ("ed25519-a", ED))}


def rsa_spki_pem():
    """An RSA public key (SubjectPublicKeyInfo, rsaEncryption): an algorithm AEF does not support. The modulus is
    arbitrary: nothing is ever verified with it."""
    def der(tag, body):
        n = len(body)
        size = bytes([n]) if n < 0x80 else (lambda b: bytes([0x80 | len(b)]) + b)(n.to_bytes((n.bit_length() + 7) // 8, "big"))
        return bytes([tag]) + size + body

    def uint(v):
        b = v.to_bytes((v.bit_length() + 8) // 8, "big")
        return der(0x02, b)

    modulus = int.from_bytes(hashlib.sha512(b"AEF 1.0 TEST RSA modulus").digest() * 4, "big") | (1 << 2047) | 1
    rsa_oid = bytes.fromhex("2a864886f70d010101")  # 1.2.840.113549.1.1.1
    alg = der(0x30, der(0x06, rsa_oid) + b"\x05\x00")
    key = der(0x30, uint(modulus) + uint(65537))
    spki = der(0x30, alg + der(0x03, b"\x00" + key))
    b64 = base64.b64encode(spki).decode()
    pem = "-----BEGIN PUBLIC KEY-----\n" + "\n".join(b64[i:i + 64] for i in range(0, len(b64), 64)) + "\n-----END PUBLIC KEY-----\n"
    return pem, "sha256:" + hashlib.sha256(spki).hexdigest()


RSA_PEM, RSA_ID = rsa_spki_pem()
PEM = {"ecdsa-a": C.spki_pem(KA.public_key), "ecdsa-b": C.spki_pem(KB.public_key), "ed25519-a": C.spki_pem(ED.public_key),
       "rsa": RSA_PEM}
WHO = {"ecdsa-a": "git:alice@example.com", "ecdsa-b": "git:bob@example.com", "ed25519-a": "spiffe://example.com/ci/sealer",
       "rsa": "git:rsa@example.com"}


def policy(*names, may=()):
    """A trust policy; the identities in `may` may also authorize redactions (OVL-10, SIG-4)."""
    return {"keys": [{"identity": WHO[n], "publicKey": PEM[n], **({"may": ["redact"]} if n in may else {})} for n in names]}


def policy_may(name, may):
    """A trust policy of one key whose identity may do what `may` lists (SIG-4)."""
    return {"keys": [{"identity": WHO[name], "publicKey": PEM[name], "may": may}]}


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def write_json(path, obj):
    write(path, (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def sig(payload_type, payload, key):
    return key.sign(C.pae(payload_type, payload))


def envelope(payload_type, payload, signatures):
    return {"payloadType": payload_type, "payload": C.b64encode(payload),
            "signatures": [{"keyid": k, "sig": C.b64encode(s)} if k is not None else {"sig": C.b64encode(s)} for k, s in signatures]}


def vector(name, env, file_name, file_bytes, payload_type, pol, envelope_result, signatures, verifies_for, rules):
    d = OUT / name
    write_json(d / "envelope.dsse.json", env)
    write(d / file_name, file_bytes)
    write_json(d / "policy.json", pol)
    write_json(d / "expected.json", {
        "kind": "signature", "envelope": "envelope.dsse.json", "file": file_name, "payloadType": payload_type,
        "policy": "policy.json", "envelopeResult": envelope_result, "signatures": signatures, "verifiesFor": verifies_for,
        "rules": rules + ["SIG-6"]})


def ok(name):
    return {"keyid": ID[name], "result": "verified", "identity": WHO[name]}


def res(keyid, result):
    return {"keyid": keyid, "result": result}


def pem_of(der):
    b64 = base64.b64encode(der).decode()
    return "-----BEGIN PUBLIC KEY-----\n" + "\n".join(b64[i:i + 64] for i in range(0, len(b64), 64)) + "\n-----END PUBLIC KEY-----\n"


def mixed_order_vectors(seal_bytes):
    """SIG-2: A' = A + T, with T the point of order 2, so [k]A' = [k]A + T when k is odd. k is SHA-512(R || A' || M)
    reduced mod L. Both vectors take a hash h with h // L odd, so the parity of h and of k = h mod L differ: an
    implementation that does not reduce k gets each of them wrong, and one that checks the cofactored equation gets the
    first wrong.
      odd k:  [S]B = R + [k]A' fails (cofactorless, k reduced): invalid.
      even k: it holds: verified."""
    s, prefix = C._ed_expand(test_scalar("ed25519-a"))
    T2 = (0, C.ED25519_P - 1, 1, 0)  # (x, y) = (0, -1): order 2
    A2 = C._ed_add(C._ed_mul(s, C.ED25519_B), T2)
    public = C._ed_encode(A2)
    key = C.Ed25519PublicKey(public)
    kid = C.keyid(key)
    identity = "spiffe://example.com/ci/mixed-order"
    pol = {"keys": [{"identity": identity, "publicKey": C.spki_pem(key)}]}
    message = C.pae(INTOTO, seal_bytes)
    for name, want_odd, result in (("ed25519-mixed-order-key-odd-k", True, "invalid"),
                                   ("ed25519-mixed-order-key-even-k", False, "verified")):
        counter = 0
        while True:
            r = C._ed_hash(prefix, message, name.encode(), counter.to_bytes(4, "big"))
            R = C._ed_encode(C._ed_mul(r, C.ED25519_B))
            h = int.from_bytes(hashlib.sha512(R + public + message).digest(), "little")
            k = h % C.ED25519_L
            if (h // C.ED25519_L) % 2 == 1 and (k % 2 == 1) == want_odd:
                break
            counter += 1
        signature = R + ((r + k * s) % C.ED25519_L).to_bytes(32, "little")
        assert C.ed25519_verify(public, message, signature) == (result == "verified")
        unreduced = C._ed_equal(C._ed_mul(int.from_bytes(signature[32:], "little"), C.ED25519_B),
                                C._ed_add(C._ed_decode(R), C._ed_mul(h, A2)))
        assert unreduced == (result != "verified")  # without the reduction, the opposite answer
        expected = {"keyid": kid, "result": "verified", "identity": identity} if result == "verified" else res(kid, "invalid")
        vector(name, envelope(INTOTO, seal_bytes, [(kid, signature)]), "seal.json", seal_bytes, INTOTO, pol, None,
               [expected], [identity] if result == "verified" else [], ["SIG-2"])


def unusable_key_vectors(seal_bytes):
    """SIG-2, SIG-3: a P-256 key that is compressed or off the curve cannot be used, so the policy is refused as a
    whole (exit status 2 in the command-line contract), even though another key of it would verify."""
    der = C.spki_der(KA.public_key)
    x = der[-64:-32]
    point = bytes([0x02 | (der[-1] & 1)]) + x
    bits = b"\x03" + bytes([len(point) + 1]) + b"\x00" + point
    algorithm = der[2:2 + 2 + der[3]]
    compressed = b"\x30" + bytes([len(algorithm) + len(bits)]) + algorithm + bits
    off_curve = der[:-1] + bytes([der[-1] ^ 0x01])
    for name, bad in (("ecdsa-compressed-key-in-policy", compressed), ("ecdsa-key-off-curve-in-policy", off_curve)):
        try:
            C.load_spki_der(bad)
            raise AssertionError(name + ": the key loaded")
        except ValueError:
            pass
        d = OUT / name
        write_json(d / "envelope.dsse.json", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]))
        write(d / "seal.json", seal_bytes)
        write_json(d / "policy.json", {"keys": [{"identity": WHO["ecdsa-a"], "publicKey": PEM["ecdsa-a"]},
                                                {"identity": "git:carol@example.com", "publicKey": pem_of(bad)}]})
        write_json(d / "expected.json", {"kind": "signature", "envelope": "envelope.dsse.json", "file": "seal.json",
                                         "payloadType": INTOTO, "policy": "policy.json", "policyRefused": True,
                                         "rules": ["SIG-2", "SIG-3", "SIG-6"]})


def refused(name, env, seal_bytes, pol, rules):
    d = OUT / name
    write_json(d / "envelope.dsse.json", env)
    write(d / "seal.json", seal_bytes)
    write_json(d / "policy.json", pol)
    write_json(d / "expected.json", {"kind": "signature", "envelope": "envelope.dsse.json", "file": "seal.json",
                                     "payloadType": INTOTO, "policy": "policy.json", "policyRefused": True,
                                     "rules": rules + ["SIG-6"]})


def policy_and_envelope_vectors(seal_bytes):
    """SIG-1, SIG-2, SIG-3, SIG-4, SIG-5 as pinned after the .NET implementation compared itself with the reference."""
    good_sig = sig(INTOTO, seal_bytes, KA)
    good = envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], good_sig)])
    alice = {"identity": WHO["ecdsa-a"], "publicKey": PEM["ecdsa-a"]}

    # SIG-2, SIG-3: Ed25519 keys that cannot be used refuse the policy.
    no_point = next(y for y in range(2, 100) if C._ed_point(y, 0) is None)
    for name, raw in (("ed25519-key-no-point-in-policy", no_point.to_bytes(32, "little")),
                      ("ed25519-small-order-key-in-policy", (1).to_bytes(32, "little"))):  # the neutral point
        der = C._der(0x30, C._der(0x30, C.ID_ED25519) + C._der(0x03, b"\x00" + raw))
        refused(name, good, seal_bytes, {"keys": [alice, {"identity": "git:carol@example.com", "publicKey": pem_of(der)}]},
                ["SIG-2", "SIG-3"])

    # SIG-2: an AlgorithmIdentifier other than the two exact ones is another algorithm.
    point = C.spki_der(KA.public_key)[-65:]
    for name, der in (("ec-key-without-named-curve", C._der(0x30, C._der(0x30, C.ID_EC_PUBLIC_KEY) + C._der(0x03, b"\x00" + point))),
                      ("ed25519-key-with-parameters", C._der(0x30, C._der(0x30, C.ID_ED25519 + b"\x05\x00")
                                                            + C._der(0x03, b"\x00" + ED.public_key.key)))):
        kid = "sha256:" + hashlib.sha256(der).hexdigest()
        pol = {"keys": [{"identity": "git:carol@example.com", "publicKey": pem_of(der)}, alice]}
        vector(name, envelope(INTOTO, seal_bytes, [(kid, good_sig)]), "seal.json", seal_bytes, INTOTO, pol, None,
               [res(kid, "unsupported-algorithm")], [], ["SIG-2"])

    # SIG-3: a SubjectPublicKeyInfo is DER whatever its algorithm.
    rsa = bytearray(base64.b64decode("".join(RSA_PEM.strip().splitlines()[1:-1])))
    assert rsa[0] == 0x30 and rsa[1] == 0x82 and rsa[4] == 0x30 and rsa[19] == 0x03 and rsa[20] == 0x82 and rsa[23] == 0
    unused = bytes(rsa[:23]) + b"\x01" + bytes(rsa[24:])
    outer = int.from_bytes(rsa[2:4], "big") + 1
    ber = b"\x30\x82" + outer.to_bytes(2, "big") + b"\x30\x81" + bytes([rsa[5]]) + bytes(rsa[6:])  # 13 in long form
    for name, der in (("rsa-key-unused-bits-in-policy", unused), ("rsa-key-ber-in-policy", ber)):
        refused(name, good, seal_bytes, {"keys": [alice, {"identity": WHO["rsa"], "publicKey": pem_of(der)}]}, ["SIG-3"])

    # SIG-3: RFC 7468's strict PEM.
    lines = PEM["ecdsa-a"].split("\n")
    indented = "\n".join(lines[:1] + ["  " + lines[1]] + lines[2:])
    refused("pem-indented-line-in-policy", good, seal_bytes, {"keys": [{"identity": WHO["ecdsa-a"], "publicKey": indented}]},
            ["SIG-3"])

    # SIG-3: lines of 64 characters but the last; the same key wrapped at 76 is refused.
    body = "".join(PEM["ecdsa-a"].strip().splitlines()[1:-1])
    long_lines = "-----BEGIN PUBLIC KEY-----\n" + "\n".join(body[i:i + 76] for i in range(0, len(body), 76)) + "\n-----END PUBLIC KEY-----\n"
    refused("pem-long-lines-in-policy", good, seal_bytes, {"keys": [{"identity": WHO["ecdsa-a"], "publicKey": long_lines}]},
            ["SIG-3"])

    # SIG-3: a key id listed twice.
    refused("key-listed-twice-in-policy", good, seal_bytes,
            {"keys": [alice, {"identity": WHO["ecdsa-b"], "publicKey": PEM["ecdsa-a"]}]}, ["SIG-3", "SIG-4"])

    # SIG-4 (R4-3): a policy valid against trust-policy.schema.json, closed for readers too, or refused as a whole.
    for name, entry in (("policy-may-as-string", dict(alice, may="never-redact")),
                        ("policy-may-twice", dict(alice, may=["redact", "redact"])),
                        ("policy-empty-identity", dict(alice, identity="")),
                        ("policy-unknown-member", dict(alice, notAfter="2026-01-01T00:00:00Z"))):
        refused(name, good, seal_bytes, {"keys": [entry]}, ["SIG-4"])
    refused("policy-unknown-top-member", good, seal_bytes, {"keys": [alice], "revoked": []}, ["SIG-4"])
    # SIG-4 (R5-6): a policy may say it is 1.0; one that declares a later version is refused, as a closed schema says.
    refused("policy-later-version", good, seal_bytes, {"schemaVersion": "1.1", "keys": [alice]}, ["SIG-4", "VER-1"])
    vector("policy-with-version", good, "seal.json", seal_bytes, INTOTO, {"schemaVersion": "1.0", "keys": [alice]}, None,
           [ok("ecdsa-a")], [WHO["ecdsa-a"]], ["SIG-4", "SIG-5"])

    # SIG-1: a null keyid reads as absent; members DSSE does not define are ignored; a duplicate member is not I-JSON.
    vector("envelope-keyid-null", dict(good, signatures=[{"keyid": None, "sig": C.b64encode(good_sig)}]), "seal.json",
           seal_bytes, INTOTO, policy("ecdsa-b", "ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], ["SIG-1", "SIG-5"])
    vector("envelope-unknown-member", dict(good, comment="DSSE does not define this member: ignored"), "seal.json",
           seal_bytes, INTOTO, policy("ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], ["SIG-1"])
    vector("envelope-duplicate-payload", good, "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), "malformed", [], [],
           ["SIG-1", "ENC-2"])
    dup = OUT / "envelope-duplicate-payload" / "envelope.dsse.json"
    raw = dup.read_bytes()
    assert raw.count(b'  "payload": ') == 1
    dup.write_bytes(raw.replace(b'  "payload": ', b'  "payload": "e30=",\n  "payload": ', 1))

    # SIG-1: either alphabet, padded or not; padding, when present, is complete.
    ed_sig = C.b64encode(sig(INTOTO, seal_bytes, ED))
    assert ed_sig.endswith("==")
    vector("sig-standard-alphabet-unpadded", dict(good, signatures=[{"keyid": ID["ed25519-a"], "sig": ed_sig.rstrip("=")}]),
           "seal.json", seal_bytes, INTOTO, policy("ed25519-a"), None, [ok("ed25519-a")], [WHO["ed25519-a"]], ["SIG-1"])
    vector("sig-incomplete-padding", dict(good, signatures=[{"keyid": ID["ed25519-a"], "sig": ed_sig[:-1]}]),
           "seal.json", seal_bytes, INTOTO, policy("ed25519-a"), "malformed", [], [], ["SIG-1", "SIG-5"])

    # SIG-5: a keyid-less signature is tried only against keys of P-256 or Ed25519.
    vector("no-keyid-only-another-algorithm", envelope(INTOTO, seal_bytes, [(None, good_sig)]), "seal.json", seal_bytes,
           INTOTO, policy("rsa"), None, [res("", "untrusted-key")], [], ["SIG-2", "SIG-5"])

    # SIG-4, SIG-5: one identity with two keys; identities in the order of the first key that verified for each.
    pol = {"keys": [alice | {"publicKey": PEM["ecdsa-b"]}, {"identity": WHO["ed25519-a"], "publicKey": PEM["ed25519-a"]}, alice]}
    two = envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], good_sig), (ID["ed25519-a"], sig(INTOTO, seal_bytes, ED))])
    vector("one-identity-two-keys", two, "seal.json", seal_bytes, INTOTO, pol, None, [ok("ecdsa-a"), ok("ed25519-a")],
           [WHO["ed25519-a"], WHO["ecdsa-a"]], ["SIG-4", "SIG-5"])


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    for name in ("ecdsa-a", "ecdsa-b", "ed25519-a", "rsa"):
        write(OUT / "keys" / f"{name}.pub.pem", PEM[name].encode())
    write(OUT / "keys" / "README.md", (
        "# Test keys: never trust them\n\n"
        "The private keys are derived from public strings (see `tools/signature_vectors.py`): anyone can sign with them.\n"
        "`rsa.pub.pem` is an RSA key AEF does not support; its modulus is arbitrary.\n\n"
        "| Key | Key id |\n|---|---|\n" + "".join(f"| `{n}` | `{ID.get(n, RSA_ID)}` |\n" for n in ("ecdsa-a", "ecdsa-b", "ed25519-a", "rsa"))
    ).encode())
    write_private_keys()

    seal_bytes = (CONF / "valid" / "completed-eval" / "run" / "seal.json").read_bytes()
    other_seal = (CONF / "valid" / "aborted-early" / "run" / "seal.json").read_bytes()
    batch_bytes = (CONF / "valid" / "completed-eval" / "run" / "overlays" / "seal-0001.json").read_bytes()
    cp_bytes = (CONF / "checkpoints" / "valid-decided" / "document.json").read_bytes()
    S = ["SIG-1", "SIG-2", "SIG-3", "SIG-4", "SIG-5"]

    vector("ecdsa-valid", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], S)
    vector("ed25519-valid", envelope(INTOTO, seal_bytes, [(ID["ed25519-a"], sig(INTOTO, seal_bytes, ED))]),
           "seal.json", seal_bytes, INTOTO, policy("ed25519-a"), None, [ok("ed25519-a")], [WHO["ed25519-a"]], S)
    vector("two-signatures-one-trusted",
           envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA)), (ID["ed25519-a"], sig(INTOTO, seal_bytes, ED))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None,
           [ok("ecdsa-a"), res(ID["ed25519-a"], "untrusted-key")], [WHO["ecdsa-a"]], S)
    vector("verifies-for-two",
           envelope(INTOTO, seal_bytes, [(ID["ed25519-a"], sig(INTOTO, seal_bytes, ED)), (ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a", "ed25519-a"), None,
           [ok("ed25519-a"), ok("ecdsa-a")], [WHO["ecdsa-a"], WHO["ed25519-a"]], S)
    vector("untrusted-key", envelope(INTOTO, seal_bytes, [(ID["ecdsa-b"], sig(INTOTO, seal_bytes, KB))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None, [res(ID["ecdsa-b"], "untrusted-key")], [], S)

    tampered = seal_bytes.replace(b'"sealedBy": "producer"', b'"sealedBy": "ingest"', 1)
    assert tampered != seal_bytes
    env = envelope(INTOTO, tampered, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))])  # signed the original
    vector("tampered-payload", env, "seal.json", tampered, INTOTO, policy("ecdsa-a"), None, [res(ID["ecdsa-a"], "invalid")], [], S)

    vector("payload-not-the-file", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]),
           "seal.json", other_seal, INTOTO, policy("ecdsa-a"), "payload-mismatch", [ok("ecdsa-a")], [], ["SIG-1", "SIG-5"])
    vector("wrong-payload-type", envelope(INTOTO, cp_bytes, [(ID["ecdsa-a"], sig(INTOTO, cp_bytes, KA))]),
           "checkpoint.json", cp_bytes, CHECKPOINT, policy("ecdsa-a"), "payload-mismatch", [ok("ecdsa-a")], [], ["SIG-1", "SIG-5"])
    vector("other-key-under-trusted-keyid", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KB))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a", "ecdsa-b"), None, [res(ID["ecdsa-a"], "invalid")], [], S)

    good = envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))])
    vector("malformed-no-payload-type", {k: v for k, v in good.items() if k != "payloadType"},
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), "malformed", [], [], ["SIG-1", "SIG-5"])
    vector("malformed-signature-base64", dict(good, signatures=[{"keyid": ID["ecdsa-a"], "sig": "@@@@"}]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), "malformed", [], [], ["SIG-1", "SIG-5"])
    vector("malformed-no-signatures", dict(good, signatures=[]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), "malformed", [], [], ["SIG-1", "SIG-5"])

    vector("no-keyid-tried-in-policy-order", envelope(INTOTO, seal_bytes, [(None, sig(INTOTO, seal_bytes, KA))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-b", "ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], S)
    vector("empty-keyid-no-trusted-key", envelope(INTOTO, seal_bytes, [("", sig(INTOTO, seal_bytes, KB))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None, [res("", "untrusted-key")], [], S)

    r, s = C.parse_der_signature(sig(INTOTO, seal_bytes, KA))
    vector("ecdsa-other-s", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], C.der_signature(r, C.P256_N - s))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], ["SIG-2"])
    vector("ecdsa-raw-signature", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], r.to_bytes(32, "big") + s.to_bytes(32, "big"))]),
           "seal.json", seal_bytes, INTOTO, policy("ecdsa-a"), None, [res(ID["ecdsa-a"], "invalid")], [], ["SIG-2"])
    flipped = bytearray(sig(INTOTO, seal_bytes, ED))
    flipped[40] ^= 0x01
    vector("ed25519-altered-signature", envelope(INTOTO, seal_bytes, [(ID["ed25519-a"], bytes(flipped))]),
           "seal.json", seal_bytes, INTOTO, policy("ed25519-a"), None, [res(ID["ed25519-a"], "invalid")], [], ["SIG-2"])
    vector("unsupported-algorithm", envelope(INTOTO, seal_bytes, [(RSA_ID, b"\x00" * 256)]),
           "seal.json", seal_bytes, INTOTO, policy("rsa", "ecdsa-a"), None, [res(RSA_ID, "unsupported-algorithm")], [], ["SIG-2"])

    vector("checkpoint-envelope", envelope(CHECKPOINT, cp_bytes, [(ID["ecdsa-a"], sig(CHECKPOINT, cp_bytes, KA))]),
           "checkpoint.json", cp_bytes, CHECKPOINT, policy("ecdsa-a"), None, [ok("ecdsa-a")], [WHO["ecdsa-a"]], S + ["CKP-5", "CKP-9"])
    vector("overlay-batch-envelope", envelope(INTOTO, batch_bytes, [(ID["ed25519-a"], sig(INTOTO, batch_bytes, ED))]),
           "seal-0001.json", batch_bytes, INTOTO, policy("ed25519-a"), None, [ok("ed25519-a")], [WHO["ed25519-a"]], S + ["OVL-3"])
    mixed_order_vectors(seal_bytes)
    unusable_key_vectors(seal_bytes)
    policy_and_envelope_vectors(seal_bytes)
    signed_runs()
    redaction_vectors()
    assurance_vectors()
    anchor_vectors()
    print("signature vectors:", len([p for p in OUT.iterdir() if p.is_dir() and p.name != "keys"]))


def redacted_copy(dst, signer, signer_name, event_identity, spoil=None, crashed_line=None):
    """The corpus's completed-eval run with a third overlay batch holding a redact event, that batch signed by `signer`,
    and the redacted blob deleted. Returns the blob's path."""
    shutil.copytree(CONF / "valid" / "completed-eval" / "run", dst)
    blob_rel = next(p for p in sealed_files(dst) if p.startswith("blobs/"))
    the_hash = read_json(dst / "seal.json")["predicate"]["runHash"]
    events = [json.loads(line) for line in (dst / "overlays" / "events.ndjson").read_text(encoding="utf-8").splitlines()]
    shutil.rmtree(dst / "overlays")
    redact = {"schemaVersion": "1.0", "eventId": "ov_0003", "kind": "redact",
              "target": {"run": COMPLETED_ID, "blob": blob_rel.rsplit("/", 1)[1]},
              "reason": "The judge's reasoning quoted a customer's address.",
              "by": {"identity": event_identity, "assurance": "signed"}, "at": "2026-10-03T09:00:00Z"}
    overlay_batches(dst, COMPLETED_ID, [events[:1], events[1:], [redact]], the_hash)
    if crashed_line is not None:  # a half line a crashed writer left, ended with an LF and claimed by batch 3 (OVL-5)
        ev_file, seal3 = dst / "overlays" / "events.ndjson", dst / "overlays" / "seal-0003.json"
        statement = read_json(seal3)
        offset = statement["predicate"]["offset"]
        data = ev_file.read_bytes()
        data = data[:offset] + crashed_line + b"\n" + data[offset:]
        ev_file.write_bytes(data)
        statement["predicate"]["length"] = len(data) - offset
        statement["subject"][0]["digest"]["sha256"] = hashlib.sha256(data[offset:]).hexdigest()
        write_json(seal3, statement)
    batch = (dst / "overlays" / "seal-0003.json").read_bytes()
    signed = batch if spoil != "other-batch" else (dst / "overlays" / "seal-0002.json").read_bytes()
    signature = sig(INTOTO, signed, signer)
    if spoil == "bad-signature":
        signature = signature[:-1] + bytes([signature[-1] ^ 0x01])
    write_json(dst / "overlays" / "seal-0003.dsse.json", envelope(INTOTO, signed, [(ID[signer_name], signature)]))
    (dst / blob_rel).unlink()
    for folder in (dst / blob_rel).parents:
        if folder == dst or any(folder.iterdir()):
            break
        folder.rmdir()
    return blob_rel


def redaction_vectors():
    """OVL-10: a redaction withholds a blob only when its batch is signed by the event's identity and the trust policy
    lets that identity redact."""
    alice, bob = WHO["ecdsa-a"], WHO["ecdsa-b"]
    seal_cases = [
        ("withheld-blob", KA, "ecdsa-a", alice, policy("ecdsa-a", may=("ecdsa-a",)), "withheld"),
        ("redact-signer-not-allowed", KB, "ecdsa-b", bob, policy("ecdsa-a", "ecdsa-b", may=("ecdsa-a",)), "missing"),
        ("redact-signed-by-another", KB, "ecdsa-b", alice, policy("ecdsa-a", "ecdsa-b", may=("ecdsa-a", "ecdsa-b")), "missing"),
        ("redact-no-policy", KA, "ecdsa-a", alice, None, "missing"),
        # The allowed key, but a signature that does not verify, or an envelope over another batch's seal.
        ("redact-bad-signature", KA, "ecdsa-a", alice, policy("ecdsa-a", may=("ecdsa-a",)), "missing", "bad-signature"),
        ("redact-envelope-other-batch", KA, "ecdsa-a", alice, policy("ecdsa-a", may=("ecdsa-a",)), "missing", "other-batch"),
        # SIG-4 (R4-3): `may` is matched exactly, and a value this version does not know grants nothing.
        ("redact-unknown-capability", KA, "ecdsa-a", alice, policy_may("ecdsa-a", ["never-redact"]), "missing"),
        ("redact-beside-unknown-capability", KA, "ecdsa-a", alice, policy_may("ecdsa-a", ["approve-releases", "redact"]),
         "withheld"),
    ]
    for name, key, key_name, who, pol, code, *spoil in seal_cases:
        d = CONF / "seal-vectors" / name
        if d.exists():
            shutil.rmtree(d)
        blob_rel = redacted_copy(d / "run", key, key_name, who, spoil[0] if spoil else None)
        expected = {"kind": "seal", "run": "run", "problems": [[blob_rel, code]], "rules": ["OVL-10", "SEAL-6", "SIG-4"]}
        if pol is not None:
            write_json(d / "policy.json", pol)
            expected["policy"] = "policy.json"
        write_json(d / "expected.json", expected)

    d = CONF / "runs" / "withheld-blob"
    if d.exists():
        shutil.rmtree(d)
    blob_rel = redacted_copy(d / "run", KA, "ecdsa-a", alice)
    write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(d / "expected.json", {"kind": "run", "run": "run", "policy": "policy.json", "outcome": "intact",
                                     "problems": [[blob_rel, "withheld"]], "withheld": 1, "signedBy": [],
                                     "rules": ["OVL-10", "SEAL-6", "SEAL-4", "SEC-5"]})

    # OVL-5 (R4-2): what an appender, a crash or a concurrent writer leaves after the sealed batches never voids an
    # authorized redaction: a blank line, an unfinished line, a file past ENC-17's line count.
    for name, tail in [("withheld-blob-tail-blank-line", b"\n"),
                       ("withheld-blob-tail-unfinished-line", b'{"schemaVersion":"1.0","eventId":"ov_00')]:
        d = CONF / "runs" / name
        if d.exists():
            shutil.rmtree(d)
        blob_rel = redacted_copy(d / "run", KA, "ecdsa-a", alice)
        events = d / "run" / "overlays" / "events.ndjson"
        events.write_bytes(events.read_bytes() + tail)
        write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
        write_json(d / "expected.json", {"kind": "run", "run": "run", "policy": "policy.json", "outcome": "intact",
                                         "problems": [[blob_rel, "withheld"]], "withheld": 1, "signedBy": [],
                                         "rules": ["OVL-5", "OVL-10", "ENC-17"]})

    # The same past ENC-17's line count: generated by the runner (§9.2.1) from runs/withheld-blob.
    d = CONF / "runs" / "withheld-blob-tail-too-many-lines"
    if d.exists():
        shutil.rmtree(d)
    write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(d / "expected.json", {"kind": "run", "run": "run", "policy": "policy.json",
                                     "generate": [{"copy": ["runs/withheld-blob/run", "run"]},
                                                  {"append": ["run/overlays/events.ndjson", [["\n", 1_000_000]]]}],
                                     "outcome": "intact", "problems": [[blob_rel, "withheld"]], "withheld": 1,
                                     "signedBy": [], "rules": ["OVL-5", "OVL-10", "ENC-17"]})

    # OVL-5 (R4N-9): a writer crashed half way through a line; the next one ended that line with an LF and sealed it in
    # its batch with a signed redaction. The half line is one event-invalid line; the chain, and the redaction, stand.
    for kind, name in (("run", "withheld-blob-after-a-crashed-line"), ("chain", "crashed-line-claimed-by-the-next-batch")):
        d = CONF / ("runs" if kind == "run" else "chain-vectors") / name
        if d.exists():
            shutil.rmtree(d)
        blob_rel = redacted_copy(d / "run", KA, "ecdsa-a", alice, crashed_line=b'{"schemaVersion":"1.0","eventId":"ov_00')
        if kind == "run":
            write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
            write_json(d / "expected.json", {"kind": "run", "run": "run", "policy": "policy.json", "outcome": "intact",
                                             "problems": [[blob_rel, "withheld"]], "withheld": 1, "signedBy": [],
                                             "rules": ["OVL-5", "OVL-10"]})
        else:
            write_json(d / "expected.json", {"kind": "chain", "run": "run",
                                             "problems": [["overlays/events.ndjson:3", "event-invalid"]],
                                             "rules": ["OVL-5", "ENC-5"]})

    # R5-1: nothing under overlays/ is a problem of the run: badly named files, or more of them than the limit, are
    # the chain's to report, and the redaction stands.
    junk = [{"write": ["run/overlays/.DS_Store", [["x", 1]]]}, {"write": ["run/overlays/notes 1.txt", [["x", 1]]]},
            {"write": ["run/overlays/events.ndjson.bak", [["{}\n", 1]]]}]  # no case variant: Windows would merge it
    for name, steps, chain_problems in (
            ("withheld-blob-overlay-junk-names", junk,
             [["overlays/.DS_Store", "unexpected-file"], ["overlays/events.ndjson.bak", "unexpected-file"],
              ["overlays/notes 1.txt", "unexpected-file"]]),
            ("withheld-blob-overlay-junk-many", [{"files": ["run/overlays/junk", 19_997]}], [["overlays", "limit"]])):
        for kind in ("run", "chain"):
            d = CONF / ("runs" if kind == "run" else "chain-vectors") / name
            if d.exists():
                shutil.rmtree(d)
            expected = {"kind": kind, "run": "run", "generate": [{"copy": ["runs/withheld-blob/run", "run"]}] + steps}
            if kind == "run":
                write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
                expected |= {"policy": "policy.json", "outcome": "intact", "problems": [[blob_rel, "withheld"]],
                             "withheld": 1, "signedBy": [], "rules": ["RUN-3", "OVL-5", "OVL-10"]}
            else:
                expected |= {"problems": chain_problems, "rules": ["OVL-5", "ENC-17"]}
            write_json(d / "expected.json", expected)

    # R5-2, W3-21: a DSSE envelope at 56 MiB is read; one byte more is malformed and verifies for no one, and is never a
    # problem of the run: the attestation then signs for no one, a batch signature authorizes nothing, an orphan
    # envelope changes nothing.
    limit = 56 * 1024 * 1024
    seal_bytes = (CONF / "valid" / "completed-eval" / "run" / "seal.json").read_bytes()
    att = json.dumps(envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]), indent=2,
                     ensure_ascii=False)
    for name, size, signed in (("attestation-at-56-mib", limit, [WHO["ecdsa-a"]]),
                               ("attestation-beyond-56-mib", limit + 1, [])):
        d = CONF / "runs" / name
        if d.exists():
            shutil.rmtree(d)
        write_json(d / "policy.json", policy("ecdsa-a"))
        write_json(d / "expected.json", {
            "kind": "run", "run": "run", "policy": "policy.json",
            "generate": [{"copy": ["valid/completed-eval/run", "run"]},
                         {"write": ["run/attestation.dsse.json", [[att, 1], [" ", size - len(att) - 1], ["\n", 1]]]}],
            "outcome": "intact", "problems": [], "signedBy": signed, "rules": ["SIG-1", "ENC-17", "ENC-18"]})
    batch_3 = (CONF / "runs" / "withheld-blob" / "run" / "overlays" / "seal-0003.json").read_bytes()
    env_3 = json.dumps(envelope(INTOTO, batch_3, [(ID["ecdsa-a"], sig(INTOTO, batch_3, KA))]), indent=2,
                       ensure_ascii=False)
    d = CONF / "seal-vectors" / "redact-envelope-beyond-limit"
    if d.exists():
        shutil.rmtree(d)
    write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(d / "expected.json", {
        "kind": "seal", "run": "run", "policy": "policy.json",
        "generate": [{"copy": ["runs/withheld-blob/run", "run"]},
                     {"write": ["run/overlays/seal-0003.dsse.json", [[env_3, 1], [" ", limit - len(env_3)], ["\n", 1]]]}],
        "problems": [[blob_rel, "missing"]], "rules": ["SIG-1", "OVL-10", "ENC-17"]})
    d = CONF / "runs" / "withheld-blob-orphan-envelope-beyond-limit"
    if d.exists():
        shutil.rmtree(d)
    write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(d / "expected.json", {
        "kind": "run", "run": "run", "policy": "policy.json",
        "generate": [{"copy": ["runs/withheld-blob/run", "run"]},
                     {"write": ["run/overlays/seal-0004.dsse.json", [["x", limit + 1]]]}],
        "outcome": "intact", "problems": [[blob_rel, "withheld"]], "withheld": 1, "signedBy": [],
        "rules": ["SIG-1", "OVL-5", "ENC-17"]})

    # The effective view of the redacted run, given the policy: the blob is withheld.
    d = CONF / "overlay-views" / "authorized-redaction"
    if d.exists():
        shutil.rmtree(d)
    blob_rel = redacted_copy(d / "run", KA, "ecdsa-a", alice)
    events = [json.loads(line) for line in (d / "run" / "overlays" / "events.ndjson").read_text(encoding="utf-8").splitlines()]
    write_json(d / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(d / "expected.json", {"kind": "overlay-view", "run": "run", "policy": "policy.json", "at": "2026-10-08T12:00:00Z",
                                     "rules": ["OVL-10", "OVL-9"], "view": {
        "results": [], "reviews": [],
        "waivers": [{"target": {"requirement": events[1]["target"]["requirement"]}, "expires": events[1]["expires"],
                     "active": True, "event": events[1]["eventId"]}],
        "withheld": [blob_rel.rsplit("/", 1)[1]], "unsealedEvents": 0}})
    lines_now = len((d / "run" / "overlays" / "events.ndjson").read_bytes().splitlines())
    view = read_json(d / "expected.json")["view"]
    e = CONF / "overlay-views" / "unfinished-line-after-a-million"
    if e.exists():
        shutil.rmtree(e)
    write_json(e / "policy.json", policy("ecdsa-a", may=("ecdsa-a",)))
    write_json(e / "expected.json", {
        "kind": "overlay-view", "run": "run", "policy": "policy.json", "at": "2026-10-08T12:00:00Z",
        "generate": [{"copy": ["overlay-views/authorized-redaction/run", "run"]},
                     {"append": ["run/overlays/events.ndjson",
                                 [["\n", 1_000_000 - lines_now], ['{"schemaVersion":"1.0","eventId":"ov_00', 1]]]}],
        "view": dict(view, unsealedEvents=1_000_000 - lines_now), "rules": ["OVL-5", "ENC-17"]})

    # A checkpoint over the redacted run still finds it (by its seal's run hash) and counts it, given the policy.
    run_hash = read_json(CONF / "valid" / "completed-eval" / "run" / "seal.json")["predicate"]["runHash"]
    for name, pol, result, problems in [
        ("redacted-run", policy("ecdsa-a", may=("ecdsa-a",)),
         {"status": "passed", "subjectVersion": "git:3f2a1c", "oldestClosedAt": "2026-10-02T14:06:23.004Z"}, []),
        ("redacted-run-no-policy", None,
         {"status": "not_measured", "subjectVersion": "git:3f2a1c", "oldestClosedAt": "2026-10-08T00:00:00Z"},  # LANE-9:
         [[f"lanes/quality/runs/{COMPLETED_ID}", "run-unverified"]]),  # a run not intact gives no age; the evaluation time
    ]:
        d = CONF / "lane-vectors" / name
        if d.exists():
            shutil.rmtree(d)
        redacted_copy(d / "runs" / COMPLETED_ID, KA, "ecdsa-a", alice)
        lanes = [{"lane": "quality", "rule": {"kind": "threshold", "lane": "quality", "metric": "triage", "path": "triage",
                                              "op": ">=", "value": 0.6},
                  "runs": [{"runId": COMPLETED_ID, "runHash": run_hash, "origin": "launched"}], "blocking": True}]
        write_json(d / "checkpoint.json", {"schemaVersion": "1.0", "checkpointId": "cp_" + name,
                                           "subject": {"ref": "agent:support/support-triage", "version": "git:3f2a1c"},
                                           "lanes": lanes, "state": "running", "outcome": None})
        expected = {"kind": "lane", "checkpoint": "checkpoint.json", "runs": "runs", "at": "2026-10-08T00:00:00Z",
                    "lanes": [{"lane": "quality", "result": result}], "problems": problems, "rules": ["CKP-8", "OVL-10", "SEAL-4"]}
        if pol is not None:
            write_json(d / "policy.json", pol)
            expected["policy"] = "policy.json"
        write_json(d / "expected.json", expected)


def assurance_vectors():
    """OVL-3 (Q4-46 b): the assurance a reader shows for each event of the verified batches: signed only when the
    event's batch carries a signature that verifies, under the policy, for the event's own identity; never
    authenticated from a file. What the event claims changes nothing."""
    alice, bob = WHO["ecdsa-a"], WHO["ecdsa-b"]
    source = CONF / "valid" / "completed-eval" / "run"
    the_hash = read_json(source / "seal.json")["predicate"]["runHash"]

    def event(n, who, claim):
        return {"schemaVersion": "1.0", "eventId": f"ov_{n:04d}", "kind": "annotate", "target": {"run": COMPLETED_ID},
                "reason": "Reviewed.", "by": {"identity": who, "assurance": claim}, "at": "2026-10-03T09:00:00Z"}
    batches = [[event(1, alice, "signed")],          # signed by alice's key: signed
               [event(2, alice, "signed")],          # no signature: the claim alone is self-attested
               [event(3, bob, "signed")],            # signed by alice's key, for bob: self-attested
               [event(4, alice, "self-attested")],   # signed by alice's key: signed, whatever it claims
               [event(5, alice, "authenticated")]]   # no signature: self-attested
    signed_batches = (1, 3, 4)
    for name, pol, shown in (("assurance-shown", policy("ecdsa-a", "ecdsa-b"),
                              ["signed", "self-attested", "self-attested", "signed", "self-attested"]),
                             ("assurance-without-policy", None, ["self-attested"] * 5)):
        d = CONF / "overlay-views" / name
        if d.exists():
            shutil.rmtree(d)
        shutil.copytree(source, d / "run")
        shutil.rmtree(d / "run" / "overlays")
        overlay_batches(d / "run", COMPLETED_ID, batches, the_hash)
        for k in signed_batches:
            batch = (d / "run" / "overlays" / f"seal-{k:04d}.json").read_bytes()
            write_json(d / "run" / "overlays" / f"seal-{k:04d}.dsse.json",
                       envelope(INTOTO, batch, [(ID["ecdsa-a"], sig(INTOTO, batch, KA))]))
        expected = {"kind": "overlay-view", "run": "run", "at": "2026-10-08T12:00:00Z", "rules": ["OVL-3", "SIG-4"],
                    "view": {"results": [], "reviews": [], "waivers": [], "withheld": [], "unsealedEvents": 0,
                             "assurance": [{"event": f"ov_{n:04d}", "shown": s} for n, s in enumerate(shown, start=1)]}}
        if pol is not None:
            write_json(d / "policy.json", pol)
            expected["policy"] = "policy.json"
        write_json(d / "expected.json", expected)


def anchor_vectors():
    """CKP-9, SIG-8 (Q4-46 c): a checkpoint with no problem of CKP-7 or CKP-8 and a signature verified for a trusted
    identity anchors the runs its lanes name; signed by a key the policy does not trust, or with a problem, none."""
    run_hash = read_json(CONF / "valid" / "completed-eval" / "run" / "seal.json")["predicate"]["runHash"]
    rule = {"kind": "threshold", "lane": "quality", "metric": "triage", "path": "triage", "op": ">=", "value": 0.6}
    passed = {"status": "passed", "subjectVersion": "git:3f2a1c", "oldestClosedAt": "2026-10-02T14:06:23.004Z"}
    missing = "f" * 64
    for name, key, key_name, extra_lane, problems, anchors in (
            ("anchors-signed-checkpoint", KA, "ecdsa-a", False, [], [run_hash]),
            ("anchors-untrusted-signer", KB, "ecdsa-b", False, [], []),
            ("anchors-with-a-problem", KA, "ecdsa-a", True, [[f"lanes/safety/runs/{COMPLETED_ID}", "run-missing"]], [])):
        d = CONF / "lane-vectors" / name
        if d.exists():
            shutil.rmtree(d)
        shutil.copytree(CONF / "valid" / "completed-eval" / "run", d / "runs" / COMPLETED_ID)
        lanes = [{"lane": "quality", "rule": rule, "runs": [{"runId": COMPLETED_ID, "runHash": run_hash, "origin": "launched"}],
                  "blocking": True}]
        results = [{"lane": "quality", "result": passed}]
        if extra_lane:
            lanes.append({"lane": "safety", "rule": dict(rule, lane="quality"),
                          "runs": [{"runId": COMPLETED_ID, "runHash": missing, "origin": "launched"}], "blocking": True})
            results.append({"lane": "safety", "result": None})
        write_json(d / "checkpoint.json", {"schemaVersion": "1.0", "checkpointId": "cp_" + name,
                                           "subject": {"ref": "agent:support/support-triage", "version": "git:3f2a1c"},
                                           "lanes": lanes, "state": "running", "outcome": None})
        cp = (d / "checkpoint.json").read_bytes()
        write_json(d / "checkpoint.dsse.json", envelope(CHECKPOINT, cp, [(ID[key_name], sig(CHECKPOINT, cp, key))]))
        write_json(d / "policy.json", policy("ecdsa-a"))
        write_json(d / "expected.json", {"kind": "lane", "checkpoint": "checkpoint.json", "runs": "runs",
                                         "at": "2026-10-08T00:00:00Z", "policy": "policy.json",
                                         "envelope": "checkpoint.dsse.json", "lanes": results, "problems": problems,
                                         "anchors": anchors, "rules": ["CKP-9", "SIG-8", "CKP-8"]})


def signed_runs():
    """Run vectors at the signed level (spec 04, §4.5): a sealed run with attestation.dsse.json, and a trust policy."""
    cases = [("signed-run", policy("ecdsa-a"), [WHO["ecdsa-a"]]),
             ("signed-run-untrusted-key", policy("ecdsa-b"), []),
             ("signed-run-no-policy-match", policy("ed25519-a", "ecdsa-b"), [])]
    for name, pol, signed_by in cases:
        d = CONF / "runs" / name
        if d.exists():
            shutil.rmtree(d)
        run, _ = small_run(d / "run")
        seal_run(d / "run", run, "producer")
        seal_bytes = (d / "run" / "seal.json").read_bytes()
        write_json(d / "run" / "attestation.dsse.json", envelope(INTOTO, seal_bytes, [(ID["ecdsa-a"], sig(INTOTO, seal_bytes, KA))]))
        write_json(d / "policy.json", pol)
        write_json(d / "expected.json", {"kind": "run", "run": "run", "policy": "policy.json", "outcome": "intact",
                                         "problems": [], "signedBy": signed_by, "rules": ["SIG-5", "SIG-7"]})


def write_private_keys():
    """The private test keys, for the write-side `sign` vectors (spec 09, tools/write_vectors.py): unencrypted PKCS#8
    PEM (RFC 5208 / RFC 5958). P-256 holds an RFC 5915 ECPrivateKey (version 1, the scalar, and the public key, as
    OpenSSL writes it); Ed25519 holds its 32-byte seed (RFC 8410). TEST KEYS, derived from public strings: never
    trust them."""
    def pem(der):
        b64 = base64.b64encode(der).decode()
        return ("-----BEGIN PRIVATE KEY-----\n" + "\n".join(b64[i:i + 64] for i in range(0, len(b64), 64))
                + "\n-----END PRIVATE KEY-----\n")

    p256 = C._der(0x30, C.ID_EC_PUBLIC_KEY + C.PRIME256V1)
    for name, key in (("ecdsa-a", KA), ("ecdsa-b", KB)):
        point = C.spki_der(key.public_key)[-65:]  # 0x04 || X || Y
        ec = C._der(0x30, C._der(0x02, b"\x01") + C._der(0x04, key.d.to_bytes(32, "big"))
                    + C._der(0xA1, C._der(0x03, b"\x00" + point)))
        write(OUT / "keys" / f"{name}.key.pem", pem(C._der(0x30, C._der(0x02, b"\x00") + p256 + C._der(0x04, ec))).encode())
    ed = C._der(0x30, C._der(0x02, b"\x00") + C._der(0x30, C.ID_ED25519) + C._der(0x04, C._der(0x04, ED.seed)))
    write(OUT / "keys" / "ed25519-a.key.pem", pem(ed).encode())
    readme = OUT / "keys" / "README.md"
    readme.write_bytes(readme.read_bytes() + (
        "\n## Private keys: test keys, never trust them\n\n"
        "`<key>.key.pem` is the private key of `<key>.pub.pem`, an unencrypted PKCS#8 `PRIVATE KEY` block (RFC 5208,\n"
        "RFC 5958): for P-256 an RFC 5915 ECPrivateKey with its public key, for Ed25519 the 32-byte seed (RFC 8410).\n"
        "The write-side `sign` vectors (spec 09, `write-vectors/sign/`) sign with them. They are published, so anyone\n"
        "can sign with them: never put them, or their public keys, in a real trust policy.\n").encode())


if __name__ == "__main__":
    main()
