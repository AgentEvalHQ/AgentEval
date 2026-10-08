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


def policy(*names):
    return {"keys": [{"identity": WHO[n], "publicKey": PEM[n]} for n in names]}


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
        "rules": rules})


def ok(name):
    return {"keyid": ID[name], "result": "verified", "identity": WHO[name]}


def res(keyid, result):
    return {"keyid": keyid, "result": result}


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
    signed_runs()
    print("signature vectors:", len([p for p in OUT.iterdir() if p.is_dir() and p.name != "keys"]))


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


if __name__ == "__main__":
    main()
