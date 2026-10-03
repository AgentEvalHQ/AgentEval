"""Which inference provider a Python tool talks to: the same rules the AgentEval CLI uses.

This mirrors ``AgentEval.Core.Providers.InferenceProviderEnvironment`` (C#), so a tool and the CLI run in one
shell reach the same model:

* ``AI_INFERENCE_PROVIDER`` names the provider: ``azure``, ``bitdeer``, ``openai``, ``foundry`` or
  ``openai-compatible``. A provider named but missing variables is an error, never a silent switch to another.
* Unset, the first fully configured provider in that order is used.
* An endpoint that will carry a key must be ``https``, or ``http`` to a loopback host (a local server).

Variables per provider (defaults as in the C# resolver)::

    azure              AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT
    bitdeer            BITDEER_API_KEY; BITDEER_ENDPOINT (https://api-inference.bitdeer.ai/v1), BITDEER_MODEL (zai-org/GLM-5.3-Flash)
    openai             OPENAI_API_KEY; OPENAI_BASE_URL (https://api.openai.com/v1), OPENAI_MODEL (gpt-4o-mini)
    foundry            FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL
    openai-compatible  OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL; OPENAI_COMPATIBLE_API_KEY optional

The resolved ``identity`` ("bitdeer:zai-org/GLM-5.3-Flash") names the model a result came from. Anything a tool
caches or records must carry it: an answer cached for one model is not an answer from another.
"""

from __future__ import annotations

import ipaddress
import os
import re
from dataclasses import dataclass
from typing import Mapping
from urllib.parse import urlparse

SELECTOR_VARIABLE = "AI_INFERENCE_PROVIDER"
BITDEER_DEFAULT_ENDPOINT = "https://api-inference.bitdeer.ai/v1"
BITDEER_DEFAULT_MODEL = "zai-org/GLM-5.3-Flash"
OPENAI_DEFAULT_ENDPOINT = "https://api.openai.com/v1"
OPENAI_DEFAULT_MODEL = "gpt-4o-mini"
USER_AGENT = "AgentEval-tools/1.0 (+https://github.com/AgentEvalHQ/AgentEval)"
AZURE_DEFAULT_API_VERSION = "2024-12-01-preview"  # the probe runner's historical value

#: Auto-detection order, as in the C# resolver.
ORDER = ("azure", "bitdeer", "openai", "foundry", "openai-compatible")

_ALIASES = {
    "azure": "azure", "azure-openai": "azure", "azureopenai": "azure",
    "bitdeer": "bitdeer",
    "openai": "openai",
    "foundry": "foundry", "azure-foundry": "foundry", "azure-ai-foundry": "foundry",
    "openai-compatible": "openai-compatible", "openai_compatible": "openai-compatible",
    "compatible": "openai-compatible", "openai-compat": "openai-compatible",
}

_REQUIRED = {
    "azure": ("AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT"),
    "bitdeer": ("BITDEER_API_KEY",),
    "openai": ("OPENAI_API_KEY",),
    "foundry": ("FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL"),
    "openai-compatible": ("OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_MODEL"),
}


@dataclass(frozen=True)
class Provider:
    """A resolved provider: where a chat completion goes and which model answers it."""

    tag: str
    endpoint: str
    key: str
    model: str
    azure_api_version: str = AZURE_DEFAULT_API_VERSION

    @property
    def identity(self) -> str:
        """``tag:model`` -- the name a cached or recorded answer must carry."""
        return f"{self.tag}:{self.model}"

    @property
    def is_azure(self) -> bool:
        return self.tag == "azure"

    def chat_url(self) -> str:
        if self.is_azure:
            return (f"{self.endpoint}/openai/deployments/{self.model}/chat/completions"
                    f"?api-version={self.azure_api_version}")
        return f"{self.endpoint}/chat/completions"

    def embeddings_url(self, model: str | None = None) -> str:
        if self.is_azure:
            return (f"{self.endpoint}/openai/deployments/{model or self.model}/embeddings"
                    f"?api-version={self.azure_api_version}")
        return f"{self.endpoint}/embeddings"

    def headers(self) -> dict[str, str]:
        # An explicit User-Agent: some providers' edge (Bitdeer's, behind Cloudflare) refuses Python's default
        # `Python-urllib/3.x` with 403 / error 1010 before the request reaches the API.
        common = {"Content-Type": "application/json", "User-Agent": USER_AGENT}
        if self.is_azure:
            return common | {"api-key": self.key}
        return common | {"Authorization": f"Bearer {self.key}"}

    def chat_body(self, messages: list[dict], max_tokens: int) -> dict:
        """A chat-completions body. Azure names the model in the URL; every other provider names it here.

        Temperature is not sent: reasoning deployments reject explicit values, and the provider default is what
        the probes sample at.
        """
        if self.is_azure:
            return {"messages": messages, "max_completion_tokens": max_tokens}
        return {"model": self.model, "messages": messages, "max_tokens": max_tokens}


class ProviderNotConfigured(SystemExit):
    """Raised (as an exit) when no provider can be resolved; the message names the missing variables."""


def _set(env: Mapping[str, str], name: str) -> str | None:
    value = env.get(name)
    return value.strip() if value and value.strip() else None


def _missing(tag: str, env: Mapping[str, str]) -> list[str]:
    return [n for n in _REQUIRED[tag] if not _set(env, n)]


def validate_endpoint(value: str | None) -> tuple[str | None, str | None]:
    """Return (endpoint without a trailing slash, None) or (None, reason). Never echoes the value."""
    if not value:
        return None, "is not set."
    parsed = urlparse(value.strip())
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        return None, "is not an absolute http(s) URL."
    if parsed.scheme == "http":
        host = parsed.hostname or ""
        loopback = host == "localhost"
        if not loopback:
            try:
                loopback = ipaddress.ip_address(host).is_loopback
            except ValueError:
                loopback = False
        if not loopback:
            return None, ("uses plain http to a non-loopback host; an API key would travel in cleartext. "
                          "Use https, or a loopback address for a local server.")
    return value.strip().rstrip("/"), None


def _build(tag: str, env: Mapping[str, str]) -> Provider:
    endpoint_var, endpoint_value = {
        "azure": ("AZURE_OPENAI_ENDPOINT", _set(env, "AZURE_OPENAI_ENDPOINT")),
        "bitdeer": ("BITDEER_ENDPOINT", _set(env, "BITDEER_ENDPOINT") or BITDEER_DEFAULT_ENDPOINT),
        "openai": ("OPENAI_BASE_URL", _set(env, "OPENAI_BASE_URL") or OPENAI_DEFAULT_ENDPOINT),
        "foundry": ("FOUNDRY_ENDPOINT", _set(env, "FOUNDRY_ENDPOINT")),
        "openai-compatible": ("OPENAI_COMPATIBLE_ENDPOINT", _set(env, "OPENAI_COMPATIBLE_ENDPOINT")),
    }[tag]
    endpoint, why = validate_endpoint(endpoint_value)
    if endpoint is None:
        raise ProviderNotConfigured(f"{endpoint_var} {why}")
    if tag == "azure":
        return Provider(tag, endpoint, _set(env, "AZURE_OPENAI_API_KEY") or "", _set(env, "AZURE_OPENAI_DEPLOYMENT") or "",
                        _set(env, "AZURE_OPENAI_API_VERSION") or AZURE_DEFAULT_API_VERSION)
    if tag == "bitdeer":
        return Provider(tag, endpoint, _set(env, "BITDEER_API_KEY") or "", _set(env, "BITDEER_MODEL") or BITDEER_DEFAULT_MODEL)
    if tag == "openai":
        return Provider(tag, endpoint, _set(env, "OPENAI_API_KEY") or "", _set(env, "OPENAI_MODEL") or OPENAI_DEFAULT_MODEL)
    if tag == "foundry":
        return Provider(tag, endpoint, _set(env, "FOUNDRY_API_KEY") or "", _set(env, "FOUNDRY_MODEL") or "")
    return Provider(tag, endpoint, _set(env, "OPENAI_COMPATIBLE_API_KEY") or "no-key-needed",
                    _set(env, "OPENAI_COMPATIBLE_MODEL") or "")


def canonical_tag(name: str) -> str | None:
    """The provider tag a name or alias stands for (``azure-openai`` -> ``azure``), or None if it names none."""
    return _ALIASES.get(name.strip().lower())


def selected_tag(env: Mapping[str, str] | None = None) -> str | None:
    """The provider ``AI_INFERENCE_PROVIDER`` names, or None when it is unset. Reads no other variable.

    For a tool that must NOT auto-detect -- an embedding retriever whose identity is published: with the
    selector unset, ``resolve`` takes the first provider whose keys happen to be in the shell, and that would
    let the shell pick a published column. Such a tool keeps its own default instead. A name that is not a
    provider is still an error, never a silent default.
    """
    env = os.environ if env is None else env
    selector = _set(env, SELECTOR_VARIABLE)
    if not selector:
        return None
    tag = canonical_tag(selector)
    if tag is None:
        raise ProviderNotConfigured(
            f"{SELECTOR_VARIABLE}={selector!r} is not a provider. Use one of: {', '.join(ORDER)}.")
    return tag


def resolve(env: Mapping[str, str] | None = None) -> Provider:
    """Resolve the provider from ``env`` (default ``os.environ``), or exit naming what is missing."""
    env = os.environ if env is None else env
    selector = _set(env, SELECTOR_VARIABLE)
    if selector:
        tag = _ALIASES.get(selector.lower())
        if tag is None:
            raise ProviderNotConfigured(
                f"{SELECTOR_VARIABLE}={selector!r} is not a provider. Use one of: {', '.join(ORDER)}.")
        missing = _missing(tag, env)
        if missing:
            raise ProviderNotConfigured(
                f"{SELECTOR_VARIABLE}={tag} but {', '.join(missing)} is not set.")
        return _build(tag, env)
    for tag in ORDER:
        if not _missing(tag, env):
            return _build(tag, env)
    needs = "; ".join(f"{t}: {' + '.join(_REQUIRED[t])}" for t in ORDER)
    raise ProviderNotConfigured(
        f"No inference provider is configured. Set {SELECTOR_VARIABLE} and one provider's variables ({needs}).")


def slug(identity: str) -> str:
    """A file-name-safe form of an identity, for per-model cache files."""
    return re.sub(r"[^A-Za-z0-9._-]+", "_", identity).strip("_")
